using System.Text;

namespace MagicRepos.Core.Storage;

/// <summary>
/// Helpers for crash-safe file writes. Each write goes to a uniquely named temporary
/// file in the same directory, is flushed to disk, and then atomically renamed over the
/// destination, so a reader never observes a partially written file and a crash mid-write
/// leaves either the old contents or the new contents — never a truncated mix.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Prefix of the temporary files created during a write. A crash between creating
    /// the temp file and renaming it can leave one behind, so directory listings that
    /// enumerate data files (e.g. branch refs) must skip names with this prefix.
    /// </summary>
    public const string TempFilePrefix = ".tmp_";

    /// <summary>
    /// Atomically writes <paramref name="bytes"/> to <paramref name="path"/>.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);

        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"Path '{path}' has no directory.", nameof(path));
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"{TempFilePrefix}{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Atomically writes <paramref name="contents"/> to <paramref name="path"/> as UTF-8 (no BOM).
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));
    }
}
