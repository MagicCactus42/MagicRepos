using System.Text;
using FluentAssertions;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Tests.Objects;

public class CommitObjectTests
{
    private static readonly ObjectId SampleTreeId = ObjectId.Hash(Encoding.UTF8.GetBytes("tree-content"));
    private static readonly ObjectId SampleParentId = ObjectId.Hash(Encoding.UTF8.GetBytes("parent-commit"));
    private static readonly Signature SampleAuthor = new("Alice", "alice@example.com",
        new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.FromHours(2)));
    private static readonly Signature SampleCommitter = new("Bob", "bob@example.com",
        new DateTimeOffset(2025, 6, 15, 13, 0, 0, TimeSpan.FromHours(-5)));

    [Fact]
    public void Constructor_stores_all_properties()
    {
        // Act
        var commit = new CommitObject(
            SampleTreeId,
            [SampleParentId],
            SampleAuthor,
            SampleCommitter,
            "Initial commit");

        // Assert
        commit.TreeId.Should().Be(SampleTreeId);
        commit.Parents.Should().ContainSingle().Which.Should().Be(SampleParentId);
        commit.Author.Should().Be(SampleAuthor);
        commit.Committer.Should().Be(SampleCommitter);
        commit.Message.Should().Be("Initial commit");
        commit.Id.Should().NotBe(ObjectId.Zero);
    }

    [Fact]
    public void Id_is_deterministic()
    {
        // Arrange & Act
        var commit1 = new CommitObject(SampleTreeId, [], SampleAuthor, SampleCommitter, "msg");
        var commit2 = new CommitObject(SampleTreeId, [], SampleAuthor, SampleCommitter, "msg");

        // Assert
        commit1.Id.Should().Be(commit2.Id);
    }

    [Fact]
    public void Different_message_produces_different_id()
    {
        // Arrange & Act
        var commit1 = new CommitObject(SampleTreeId, [], SampleAuthor, SampleCommitter, "message A");
        var commit2 = new CommitObject(SampleTreeId, [], SampleAuthor, SampleCommitter, "message B");

        // Assert
        commit1.Id.Should().NotBe(commit2.Id);
    }

    [Fact]
    public void Serialize_contains_tree_line()
    {
        // Arrange
        var commit = new CommitObject(SampleTreeId, [], SampleAuthor, SampleCommitter, "test");

        // Act
        byte[] serialized = commit.Serialize();
        string text = Encoding.UTF8.GetString(serialized);

        // Assert
        text.Should().Contain($"tree {SampleTreeId.ToHexString()}");
    }

    [Fact]
    public void Serialize_contains_parent_lines()
    {
        // Arrange
        ObjectId parent1 = ObjectId.Hash(Encoding.UTF8.GetBytes("p1"));
        ObjectId parent2 = ObjectId.Hash(Encoding.UTF8.GetBytes("p2"));
        var commit = new CommitObject(SampleTreeId, [parent1, parent2], SampleAuthor, SampleCommitter, "merge");

        // Act
        byte[] serialized = commit.Serialize();
        string text = Encoding.UTF8.GetString(serialized);

        // Assert
        text.Should().Contain($"parent {parent1.ToHexString()}");
        text.Should().Contain($"parent {parent2.ToHexString()}");
    }

    [Fact]
    public void Serialize_with_no_parents_omits_parent_line()
    {
        // Arrange
        var commit = new CommitObject(SampleTreeId, [], SampleAuthor, SampleCommitter, "root commit");

        // Act
        byte[] serialized = commit.Serialize();
        string text = Encoding.UTF8.GetString(serialized);

        // Assert
        text.Should().NotContain("parent ");
    }

    [Fact]
    public void Serialize_contains_author_and_committer()
    {
        // Arrange
        var commit = new CommitObject(SampleTreeId, [], SampleAuthor, SampleCommitter, "test");

        // Act
        byte[] serialized = commit.Serialize();
        string text = Encoding.UTF8.GetString(serialized);

        // Assert
        text.Should().Contain($"author {SampleAuthor}");
        text.Should().Contain($"committer {SampleCommitter}");
    }

    [Fact]
    public void Serialize_contains_message_after_blank_line()
    {
        // Arrange
        var commit = new CommitObject(SampleTreeId, [], SampleAuthor, SampleCommitter, "My commit message");

        // Act
        byte[] serialized = commit.Serialize();
        string text = Encoding.UTF8.GetString(serialized);

        // Assert — message comes after a double newline (blank line separator)
        int blankLineIndex = text.IndexOf("\n\n", StringComparison.Ordinal);
        blankLineIndex.Should().BeGreaterThan(0);
        string messagePart = text[(blankLineIndex + 2)..];
        messagePart.Should().Be("My commit message");
    }

    [Fact]
    public void Id_matches_hash_of_serialized_content()
    {
        // Arrange
        var commit = new CommitObject(SampleTreeId, [SampleParentId], SampleAuthor, SampleCommitter, "verify hash");

        // Act
        byte[] serialized = commit.Serialize();
        ObjectId hashOfSerialized = ObjectId.Hash(serialized);

        // Assert
        commit.Id.Should().Be(hashOfSerialized);
    }
}
