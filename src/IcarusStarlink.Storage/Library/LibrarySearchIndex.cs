using IcarusStarlink.Core.Library;
using Microsoft.Data.Sqlite;

namespace IcarusStarlink.Storage.Library;

/// <summary>
/// A disposable, in-memory FTS5 index over the library — never a source of truth (that's the
/// Extracted_Mods folder itself). Rebuild() does a full rebuild from a fresh folder scan (used
/// once, at startup); Insert/Remove/UpdateNotes make targeted changes afterward so a single
/// mod's metadata edit doesn't require re-reading every other mod's package. In-memory rather
/// than a persisted cache file: library sizes are modest (dozens of mods), so even the full
/// rebuild is cheap, and this avoids stale-cache-file management entirely.
/// </summary>
internal sealed class LibrarySearchIndex : IDisposable
{
    private readonly SqliteConnection _connection;

    public LibrarySearchIndex()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE VIRTUAL TABLE library_fts USING fts5(
                folder_name UNINDEXED,
                name,
                author,
                notes,
                content
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Rebuild(IEnumerable<(LibraryEntry Entry, string SearchableContent)> items)
    {
        using var transaction = _connection.BeginTransaction();

        using (var clearCmd = _connection.CreateCommand())
        {
            clearCmd.Transaction = transaction;
            clearCmd.CommandText = "DELETE FROM library_fts;";
            clearCmd.ExecuteNonQuery();
        }

        using (var insertCmd = _connection.CreateCommand())
        {
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT INTO library_fts (folder_name, name, author, notes, content)
                VALUES ($folderName, $name, $author, $notes, $content);
                """;
            var folderNameParam = insertCmd.Parameters.Add("$folderName", SqliteType.Text);
            var nameParam = insertCmd.Parameters.Add("$name", SqliteType.Text);
            var authorParam = insertCmd.Parameters.Add("$author", SqliteType.Text);
            var notesParam = insertCmd.Parameters.Add("$notes", SqliteType.Text);
            var contentParam = insertCmd.Parameters.Add("$content", SqliteType.Text);

            foreach (var (entry, content) in items)
            {
                folderNameParam.Value = entry.FolderName;
                nameParam.Value = entry.Name;
                authorParam.Value = entry.Author;
                notesParam.Value = entry.Notes;
                contentParam.Value = content;
                insertCmd.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    public void Insert(LibraryEntry entry, string searchableContent)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO library_fts (folder_name, name, author, notes, content)
            VALUES ($folderName, $name, $author, $notes, $content);
            """;
        cmd.Parameters.AddWithValue("$folderName", entry.FolderName);
        cmd.Parameters.AddWithValue("$name", entry.Name);
        cmd.Parameters.AddWithValue("$author", entry.Author);
        cmd.Parameters.AddWithValue("$notes", entry.Notes);
        cmd.Parameters.AddWithValue("$content", searchableContent);
        cmd.ExecuteNonQuery();
    }

    public void Remove(string folderName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM library_fts WHERE folder_name = $folderName;";
        cmd.Parameters.AddWithValue("$folderName", folderName);
        cmd.ExecuteNonQuery();
    }

    public void UpdateNotes(string folderName, string notes)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE library_fts SET notes = $notes WHERE folder_name = $folderName;";
        cmd.Parameters.AddWithValue("$notes", notes);
        cmd.Parameters.AddWithValue("$folderName", folderName);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlySet<string> Search(string query)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT folder_name FROM library_fts WHERE library_fts MATCH $query;";
        cmd.Parameters.AddWithValue("$query", BuildFtsMatchExpression(query));

        var results = new HashSet<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    /// <summary>
    /// FTS5's MATCH syntax treats punctuation specially (AND/OR/NOT, quotes, parens, *, -, :); a
    /// raw user search string passed straight through could throw a syntax error or be
    /// misinterpreted as an operator. Quoting each term (escaping embedded quotes) and appending
    /// '*' makes every term a literal prefix match, then OR-ing them together turns arbitrary
    /// input into a safe, best-effort "any of these words" search.
    /// </summary>
    private static string BuildFtsMatchExpression(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var escaped = terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*");
        return string.Join(" OR ", escaped);
    }

    public void Dispose() => _connection.Dispose();
}
