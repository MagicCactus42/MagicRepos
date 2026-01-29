using System.Text;
using FluentAssertions;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Storage;

namespace MagicRepos.Core.Tests.Storage;

public class IndexFileTests : IDisposable
{
    private readonly string _tempDir;

    public IndexFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "magicrepos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string IndexPath => Path.Combine(_tempDir, "index");

    private static IndexEntry MakeEntry(string path, string content = "default")
    {
        ObjectId id = ObjectId.Hash(Encoding.UTF8.GetBytes(content));
        return new IndexEntry
        {
            ModifiedTimeSeconds = 1718400000,
            ModifiedTimeNanoseconds = 123456,
            FileSize = content.Length,
            ObjectId = id,
            Flags = (ushort)Math.Min(path.Length, 0xFFF),
            Path = path
        };
    }

    [Fact]
    public void CreateEmpty_has_no_entries()
    {
        // Act
        IndexFile index = IndexFile.CreateEmpty();

        // Assert
        index.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AddOrUpdate_adds_new_entry()
    {
        // Arrange
        IndexFile index = IndexFile.CreateEmpty();
        IndexEntry entry = MakeEntry("src/main.cs");

        // Act
        index.AddOrUpdate(entry);

        // Assert
        index.Entries.Should().ContainSingle();
        index.Entries[0].Path.Should().Be("src/main.cs");
    }

    [Fact]
    public void AddOrUpdate_updates_existing_entry()
    {
        // Arrange
        IndexFile index = IndexFile.CreateEmpty();
        IndexEntry original = MakeEntry("file.txt", "original");
        IndexEntry updated = MakeEntry("file.txt", "updated");

        // Act
        index.AddOrUpdate(original);
        index.AddOrUpdate(updated);

        // Assert
        index.Entries.Should().ContainSingle();
        index.Entries[0].ObjectId.Should().Be(updated.ObjectId);
    }

    [Fact]
    public void AddOrUpdate_maintains_sorted_order()
    {
        // Arrange
        IndexFile index = IndexFile.CreateEmpty();

        // Act — add in reverse order
        index.AddOrUpdate(MakeEntry("z.txt"));
        index.AddOrUpdate(MakeEntry("a.txt"));
        index.AddOrUpdate(MakeEntry("m.txt"));

        // Assert
        index.Entries.Should().HaveCount(3);
        index.Entries[0].Path.Should().Be("a.txt");
        index.Entries[1].Path.Should().Be("m.txt");
        index.Entries[2].Path.Should().Be("z.txt");
    }

    [Fact]
    public void Remove_removes_existing_entry()
    {
        // Arrange
        IndexFile index = IndexFile.CreateEmpty();
        index.AddOrUpdate(MakeEntry("file.txt"));
        index.AddOrUpdate(MakeEntry("other.txt"));

        // Act
        index.Remove("file.txt");

        // Assert
        index.Entries.Should().ContainSingle();
        index.Entries[0].Path.Should().Be("other.txt");
    }

    [Fact]
    public void Remove_nonexistent_path_is_noop()
    {
        // Arrange
        IndexFile index = IndexFile.CreateEmpty();
        index.AddOrUpdate(MakeEntry("file.txt"));

        // Act
        index.Remove("nonexistent.txt");

        // Assert
        index.Entries.Should().ContainSingle();
    }

    [Fact]
    public void WriteTo_and_ReadFrom_roundtrip()
    {
        // Arrange
        IndexFile index = IndexFile.CreateEmpty();
        index.AddOrUpdate(MakeEntry("src/app.cs", "app content"));
        index.AddOrUpdate(MakeEntry("README.md", "readme content"));
        index.AddOrUpdate(MakeEntry("tests/test.cs", "test content"));

        // Act
        index.WriteTo(IndexPath);
        IndexFile loaded = IndexFile.ReadFrom(IndexPath);

        // Assert
        loaded.Entries.Should().HaveCount(3);

        for (int i = 0; i < index.Entries.Count; i++)
        {
            loaded.Entries[i].Path.Should().Be(index.Entries[i].Path);
            loaded.Entries[i].ObjectId.Should().Be(index.Entries[i].ObjectId);
            loaded.Entries[i].FileSize.Should().Be(index.Entries[i].FileSize);
            loaded.Entries[i].ModifiedTimeSeconds.Should().Be(index.Entries[i].ModifiedTimeSeconds);
            loaded.Entries[i].ModifiedTimeNanoseconds.Should().Be(index.Entries[i].ModifiedTimeNanoseconds);
            loaded.Entries[i].Flags.Should().Be(index.Entries[i].Flags);
        }
    }

    [Fact]
    public void WriteTo_and_ReadFrom_empty_index_roundtrip()
    {
        // Arrange
        IndexFile index = IndexFile.CreateEmpty();

        // Act
        index.WriteTo(IndexPath);
        IndexFile loaded = IndexFile.ReadFrom(IndexPath);

        // Assert
        loaded.Entries.Should().BeEmpty();
    }

    [Fact]
    public void ReadFrom_detects_corrupted_checksum()
    {
        // Arrange
        IndexFile index = IndexFile.CreateEmpty();
        index.AddOrUpdate(MakeEntry("file.txt"));
        index.WriteTo(IndexPath);

        // Corrupt a byte in the middle of the file
        byte[] bytes = File.ReadAllBytes(IndexPath);
        bytes[10] ^= 0xFF; // flip bits
        File.WriteAllBytes(IndexPath, bytes);

        // Act & Assert
        Action act = () => IndexFile.ReadFrom(IndexPath);
        act.Should().Throw<InvalidDataException>().WithMessage("*checksum*");
    }

    [Fact]
    public void WriteTo_and_ReadFrom_preserves_long_paths()
    {
        // Arrange
        string longPath = "src/" + new string('a', 200) + "/deep/file.txt";
        IndexFile index = IndexFile.CreateEmpty();
        index.AddOrUpdate(MakeEntry(longPath));

        // Act
        index.WriteTo(IndexPath);
        IndexFile loaded = IndexFile.ReadFrom(IndexPath);

        // Assert
        loaded.Entries.Should().ContainSingle();
        loaded.Entries[0].Path.Should().Be(longPath);
    }
}
