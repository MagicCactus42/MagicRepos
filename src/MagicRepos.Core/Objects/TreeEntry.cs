namespace MagicRepos.Core.Objects;

/// <summary>
/// File mode for tree entries, matching Git conventions.
/// The integer values represent the octal mode as a decimal number for convenience.
/// </summary>
public enum FileMode
{
    Regular = 0b_001_000_000_110_100_100,    // 0100644
    Executable = 0b_001_000_000_110_101_101, // 0100755
    Directory = 0b_000_100_000_000_000_000,  // 0040000
    Symlink = 0b_001_010_000_000_000_000     // 0120000
}

public static class FileModeExtensions
{
    /// <summary>
    /// Converts the FileMode to its octal string representation (no leading zeros except for directory).
    /// Git uses: "100644", "100755", "40000", "120000"
    /// </summary>
    public static string ToOctalString(this FileMode mode) => mode switch
    {
        FileMode.Regular => "100644",
        FileMode.Executable => "100755",
        FileMode.Directory => "40000",
        FileMode.Symlink => "120000",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown file mode.")
    };

    /// <summary>
    /// Parses an octal mode string to a FileMode.
    /// </summary>
    public static FileMode ParseFileMode(string octal) => octal switch
    {
        "100644" => FileMode.Regular,
        "100755" => FileMode.Executable,
        "40000" or "040000" => FileMode.Directory,
        "120000" => FileMode.Symlink,
        _ => throw new ArgumentException($"Unknown file mode: '{octal}'.", nameof(octal))
    };
}

public sealed class TreeEntry : IComparable<TreeEntry>
{
    public FileMode Mode { get; }
    public string Name { get; }
    public ObjectId Id { get; }

    public TreeEntry(FileMode mode, string name, ObjectId id)
    {
        ArgumentNullException.ThrowIfNull(name);

        Mode = mode;
        Name = name;
        Id = id;
    }

    /// <summary>
    /// Compares by name for sorting within a tree.
    /// </summary>
    public int CompareTo(TreeEntry? other)
    {
        if (other is null) return 1;
        return string.Compare(Name, other.Name, StringComparison.Ordinal);
    }
}
