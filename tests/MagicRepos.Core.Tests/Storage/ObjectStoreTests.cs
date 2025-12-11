using System.Text;
using FluentAssertions;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Storage;

namespace MagicRepos.Core.Tests.Storage;

public class ObjectStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ObjectStore _store;

    public ObjectStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "magicrepos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new ObjectStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Write_and_read_roundtrips()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("store test content");
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, content);

        // Act
        _store.Write(id, compressed);
        byte[] readBack = _store.Read(id);

        // Assert
        readBack.Should().BeEquivalentTo(compressed);
    }

    [Fact]
    public void Exists_returns_false_for_missing_object()
    {
        // Arrange
        ObjectId id = ObjectId.Hash(Encoding.UTF8.GetBytes("nonexistent"));

        // Act & Assert
        _store.Exists(id).Should().BeFalse();
    }

    [Fact]
    public void Exists_returns_true_after_write()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("existence check");
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, content);

        // Act
        _store.Write(id, compressed);

        // Assert
        _store.Exists(id).Should().BeTrue();
    }

    [Fact]
    public void Read_throws_for_missing_object()
    {
        // Arrange
        ObjectId id = ObjectId.Hash(Encoding.UTF8.GetBytes("missing object"));

        // Act & Assert
        Action act = () => _store.Read(id);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Write_is_idempotent()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("idempotent write");
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, content);

        // Act — writing twice should not throw
        _store.Write(id, compressed);
        _store.Write(id, compressed);

        // Assert
        _store.Exists(id).Should().BeTrue();
        _store.Read(id).Should().BeEquivalentTo(compressed);
    }

    [Fact]
    public void Objects_stored_in_prefix_suffix_directory_structure()
    {
        // Arrange
        byte[] content = Encoding.UTF8.GetBytes("directory structure test");
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, content);

        // Act
        _store.Write(id, compressed);

        // Assert — file should exist at objects/{prefix}/{suffix}
        string expectedPath = Path.Combine(_tempDir, "objects", id.Prefix, id.Suffix);
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public void Write_and_deserialize_full_roundtrip()
    {
        // Arrange
        byte[] originalContent = Encoding.UTF8.GetBytes("full roundtrip content");
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, originalContent);

        // Act
        _store.Write(id, compressed);
        byte[] readCompressed = _store.Read(id);
        (ObjectType type, byte[] content) = ObjectSerializer.Deserialize(readCompressed);

        // Assert
        type.Should().Be(ObjectType.Blob);
        content.Should().BeEquivalentTo(originalContent);
    }
}
