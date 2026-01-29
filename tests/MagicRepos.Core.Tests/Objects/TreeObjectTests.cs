using System.Text;
using FluentAssertions;
using MagicRepos.Core.Objects;
using FileMode = MagicRepos.Core.Objects.FileMode;

namespace MagicRepos.Core.Tests.Objects;

public class TreeObjectTests
{
    private static ObjectId MakeId(string content) =>
        ObjectId.Hash(Encoding.UTF8.GetBytes(content));

    [Fact]
    public void Constructor_sorts_entries_by_name()
    {
        // Arrange
        var entries = new List<TreeEntry>
        {
            new(FileMode.Regular, "zebra.txt", MakeId("z")),
            new(FileMode.Regular, "apple.txt", MakeId("a")),
            new(FileMode.Regular, "mango.txt", MakeId("m")),
        };

        // Act
        var tree = new TreeObject(entries);

        // Assert
        tree.Entries.Should().HaveCount(3);
        tree.Entries[0].Name.Should().Be("apple.txt");
        tree.Entries[1].Name.Should().Be("mango.txt");
        tree.Entries[2].Name.Should().Be("zebra.txt");
    }

    [Fact]
    public void Id_is_deterministic_for_same_entries()
    {
        // Arrange
        var entries = new List<TreeEntry>
        {
            new(FileMode.Regular, "file.txt", MakeId("content")),
        };

        // Act
        var tree1 = new TreeObject(entries);
        var tree2 = new TreeObject(entries);

        // Assert
        tree1.Id.Should().Be(tree2.Id);
    }

    [Fact]
    public void Different_entries_produce_different_id()
    {
        // Arrange & Act
        var tree1 = new TreeObject([new TreeEntry(FileMode.Regular, "a.txt", MakeId("a"))]);
        var tree2 = new TreeObject([new TreeEntry(FileMode.Regular, "b.txt", MakeId("b"))]);

        // Assert
        tree1.Id.Should().NotBe(tree2.Id);
    }

    [Fact]
    public void Serialize_produces_header_plus_entries()
    {
        // Arrange
        var id = MakeId("content");
        var tree = new TreeObject([new TreeEntry(FileMode.Regular, "test.txt", id)]);

        // Act
        byte[] serialized = tree.Serialize();

        // Assert — should start with "tree {length}\0"
        string headerPrefix = "tree ";
        serialized.AsSpan(0, headerPrefix.Length).ToArray()
            .Should().BeEquivalentTo(Encoding.UTF8.GetBytes(headerPrefix));

        // Should contain null separator
        serialized.Should().Contain(0);
    }

    [Fact]
    public void Id_matches_hash_of_serialized_content()
    {
        // Arrange
        var tree = new TreeObject([
            new TreeEntry(FileMode.Regular, "file.txt", MakeId("data")),
            new TreeEntry(FileMode.Directory, "subdir", MakeId("dir")),
        ]);

        // Act
        byte[] serialized = tree.Serialize();
        ObjectId hashOfSerialized = ObjectId.Hash(serialized);

        // Assert
        tree.Id.Should().Be(hashOfSerialized);
    }

    [Fact]
    public void Entries_contain_various_file_modes()
    {
        // Arrange
        var entries = new List<TreeEntry>
        {
            new(FileMode.Regular, "file.txt", MakeId("r")),
            new(FileMode.Executable, "script.sh", MakeId("x")),
            new(FileMode.Directory, "subdir", MakeId("d")),
            new(FileMode.Symlink, "link", MakeId("l")),
        };

        // Act
        var tree = new TreeObject(entries);

        // Assert
        tree.Entries.Should().HaveCount(4);
        tree.Entries.Select(e => e.Mode).Should().Contain(FileMode.Regular);
        tree.Entries.Select(e => e.Mode).Should().Contain(FileMode.Executable);
        tree.Entries.Select(e => e.Mode).Should().Contain(FileMode.Directory);
        tree.Entries.Select(e => e.Mode).Should().Contain(FileMode.Symlink);
    }

    [Fact]
    public void Empty_tree_is_valid()
    {
        // Arrange & Act
        var tree = new TreeObject([]);

        // Assert
        tree.Entries.Should().BeEmpty();
        tree.Id.Should().NotBe(ObjectId.Zero);
    }

    [Fact]
    public void TreeEntry_CompareTo_sorts_by_name_ordinal()
    {
        // Arrange
        var a = new TreeEntry(FileMode.Regular, "alpha", MakeId("a"));
        var b = new TreeEntry(FileMode.Regular, "beta", MakeId("b"));

        // Act & Assert
        a.CompareTo(b).Should().BeNegative();
        b.CompareTo(a).Should().BePositive();
        a.CompareTo(a).Should().Be(0);
        a.CompareTo(null).Should().Be(1);
    }
}
