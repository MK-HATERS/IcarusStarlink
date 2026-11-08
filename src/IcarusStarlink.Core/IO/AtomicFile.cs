using System.Text;

namespace IcarusStarlink.Core.IO;

/// <summary>
/// Writes a file so it's never left half-written: the content goes to a temp file beside it (a
/// name unique per call, so two writes at once can't collide), is flushed to the disk, and then
/// replaces the real one in one move. A crash, a forced shutdown, a power cut or a full disk
/// mid-write leaves the old file or the whole new one, and a failed write removes its temp file.
/// A target another program holds for a moment (a sync client, a virus scan) is waited for
/// briefly. The one place every project does this (it used to be copied by hand in about 14
/// places, half of them with a fixed ".tmp" name).
/// </summary>
public static class AtomicFile
{
    /// <summary>Waits between tries of the final move: about 0.75 s in all, short enough for a save on the UI thread.</summary>
    private static readonly int[] MoveRetryDelaysMs = [50, 100, 200, 400];

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
            FlushToDisk(temp);
            MoveIntoPlace(temp, path);
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

    /// <summary>
    /// The last step of a safe write: moves the finished temp file over the real one. A file another
    /// program holds for a moment (OneDrive uploading the previous save, a virus scan) is tried again
    /// a few times over about 0.75 s; a lasting failure is thrown as before. The temp file is left
    /// for the caller to remove when this throws.
    /// </summary>
    public static void MoveIntoPlace(string temp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < MoveRetryDelaysMs.Length && IsBriefLock(ex, path))
            {
                Thread.Sleep(MoveRetryDelaysMs[attempt]);
            }
        }
    }

    private static bool IsBriefLock(Exception ex, string path) => ex switch
    {
        // ERROR_SHARING_VIOLATION (32), ERROR_LOCK_VIOLATION (33).
        IOException io => (io.HResult & 0xFFFF) is 32 or 33,
        // Replacing a file another program holds without delete sharing reports "access denied";
        // a read-only target never clears, so it isn't waited on.
        UnauthorizedAccessException => IsWritableExistingFile(path),
        _ => false,
    };

    private static bool IsWritableExistingFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && !file.IsReadOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Forces the temp file's bytes to the disk before the rename, so a power cut right after a save
    /// leaves the old file or the whole new one, never a file of zeros. Best effort: a program that
    /// holds the temp file only costs that guarantee, not the save.
    /// </summary>
    private static void FlushToDisk(string temp)
    {
        try
        {
            using var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The rename below still keeps the write all-or-nothing against a crash.
        }
    }
}
