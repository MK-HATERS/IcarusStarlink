using System.Text;

namespace IcarusStarlink.Core.IO;

/// <summary>
/// Writes a file so it's never left half-written: the content goes to a temp file beside it (a
/// name unique per call, so two writes at once can't collide), which then replaces the real one
/// in one move. A crash, a forced shutdown or a full disk mid-write leaves the old file as it was,
/// and a failed write removes its temp file. The one place every project does this (a review on
/// 2026-10-23 found it copied by hand in about 14 places, half of them with a fixed ".tmp" name).
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content) =>
        Write(path, temp => File.WriteAllText(temp, content));

    public static void WriteAllText(string path, string content, Encoding encoding) =>
        Write(path, temp => File.WriteAllText(temp, content, encoding));

    public static void WriteAllBytes(string path, byte[] bytes) =>
        Write(path, temp => File.WriteAllBytes(temp, bytes));

    /// <summary>Copies <paramref name="source"/> into <paramref name="path"/> the same safe way.</summary>
    public static void Copy(string source, string path) =>
        Write(path, temp => File.Copy(source, temp, overwrite: true));

    /// <param name="writeTemp">Writes the new content to the temp path it's given.</param>
    public static void Write(string path, Action<string> writeTemp)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            writeTemp(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the original error is the one worth reporting.
            }

            throw;
        }
    }
}
