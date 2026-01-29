using System.Text;
using FluentAssertions;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Storage;

namespace MagicRepos.Core.Tests.Storage;

public class ObjectSerializerTests
{
    [Fact]
    public void Serialize_and_deserialize_blob_roundtrips()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("Hello, serializer!");

        // Act
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, content);
        (ObjectType type, byte[] deserialized) = ObjectSerializer.Deserialize(compressed);

        // Assert
        type.Should().Be(ObjectType.Blob);
        deserialized.Should().BeEquivalentTo(content);
    }

    [Fact]
    public void Serialize_and_deserialize_tree_roundtrips()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("tree content data");

        // Act
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Tree, content);
        (ObjectType type, byte[] deserialized) = ObjectSerializer.Deserialize(compressed);

        // Assert
        type.Should().Be(ObjectType.Tree);
        deserialized.Should().BeEquivalentTo(content);
    }

    [Fact]
    public void Serialize_and_deserialize_commit_roundtrips()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("commit content data");

        // Act
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Commit, content);
        (ObjectType type, byte[] deserialized) = ObjectSerializer.Deserialize(compressed);

        // Assert
        type.Should().Be(ObjectType.Commit);
        deserialized.Should().BeEquivalentTo(content);
    }

    [Fact]
    public void ComputeId_matches_serialized_id()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("Compute ID test");

        // Act
        ObjectId computedId = ObjectSerializer.ComputeId(ObjectType.Blob, content);
        (ObjectId serializedId, _) = ObjectSerializer.Serialize(ObjectType.Blob, content);

        // Assert
        computedId.Should().Be(serializedId);
    }

    [Fact]
    public void Serialized_data_is_compressed()
    {
        // Arrange — use a large, highly compressible payload
        byte[] content = Encoding.UTF8.GetBytes(new string('A', 10_000));

        // Act
        (_, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, content);

        // The raw data would be: "blob 10000\0" + 10000 'A's = ~10011 bytes
        // Compressed should be significantly smaller
        byte[] rawHeader = Encoding.UTF8.GetBytes($"blob {content.Length}\0");
        int rawSize = rawHeader.Length + content.Length;

        // Assert
        compressed.Length.Should().BeLessThan(rawSize,
            "deflate compression should reduce the size of highly repetitive data");
    }

    [Fact]
    public void Deserialize_throws_on_invalid_data()
    {
        // Arrange — data that does not decompress to valid format
        // Create valid compressed data but with no null separator
        // Actually, we'll just try to deserialize garbage
        byte[] garbage = [0xFF, 0xFE, 0xFD, 0xFC];

        // Act & Assert — should throw some exception during decompression or parsing
        Action act = () => ObjectSerializer.Deserialize(garbage);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Different_types_with_same_content_produce_different_ids()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("same content");

        // Act
        ObjectId blobId = ObjectSerializer.ComputeId(ObjectType.Blob, content);
        ObjectId treeId = ObjectSerializer.ComputeId(ObjectType.Tree, content);
        ObjectId commitId = ObjectSerializer.ComputeId(ObjectType.Commit, content);

        // Assert — the type is part of the header, so IDs must differ
        blobId.Should().NotBe(treeId);
        blobId.Should().NotBe(commitId);
        treeId.Should().NotBe(commitId);
    }

    [Fact]
    public void Empty_content_roundtrips()
    {
        // Arrange
        byte[] content = [];

        // Act
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, content);
        (ObjectType type, byte[] deserialized) = ObjectSerializer.Deserialize(compressed);

        // Assert
        type.Should().Be(ObjectType.Blob);
        deserialized.Should().BeEmpty();
    }
}
