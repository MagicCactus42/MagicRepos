using System.Text;
using FluentAssertions;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Tests.Objects;

public class BlobObjectTests
{
    [Fact]
    public void Constructor_stores_data_and_computes_id()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("Hello, World!");

        // Act
        var blob = new BlobObject(data);

        // Assert
        blob.Data.Should().BeEquivalentTo(data);
        blob.Id.Should().NotBe(ObjectId.Zero);
    }

    [Fact]
    public void Id_is_deterministic_for_same_data()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("deterministic content");

        // Act
        var blob1 = new BlobObject(data);
        var blob2 = new BlobObject(data);

        // Assert
        blob1.Id.Should().Be(blob2.Id);
    }

    [Fact]
    public void Different_data_produces_different_id()
    {
        // Arrange & Act
        var blob1 = new BlobObject(Encoding.UTF8.GetBytes("content A"));
        var blob2 = new BlobObject(Encoding.UTF8.GetBytes("content B"));

        // Assert
        blob1.Id.Should().NotBe(blob2.Id);
    }

    [Fact]
    public void Serialize_returns_header_plus_data()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("test blob");
        var blob = new BlobObject(data);

        // Act
        byte[] serialized = blob.Serialize();

        // Assert — format is "blob {length}\0{data}"
        string expectedHeader = $"blob {data.Length}\0";
        byte[] expectedHeaderBytes = Encoding.UTF8.GetBytes(expectedHeader);

        serialized.Should().HaveCount(expectedHeaderBytes.Length + data.Length);
        serialized.AsSpan(0, expectedHeaderBytes.Length).ToArray()
            .Should().BeEquivalentTo(expectedHeaderBytes);
        serialized.AsSpan(expectedHeaderBytes.Length).ToArray()
            .Should().BeEquivalentTo(data);
    }

    [Fact]
    public void Id_matches_hash_of_serialized_content()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("verify hash");
        var blob = new BlobObject(data);

        // Act
        byte[] serialized = blob.Serialize();
        ObjectId hashOfSerialized = ObjectId.Hash(serialized);

        // Assert
        blob.Id.Should().Be(hashOfSerialized);
    }

    [Fact]
    public void FromBytes_creates_equivalent_blob()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("from bytes test");

        // Act
        BlobObject blob = BlobObject.FromBytes(data);

        // Assert
        blob.Data.Should().BeEquivalentTo(data);
        blob.Id.Should().Be(new BlobObject(data).Id);
    }

    [Fact]
    public void Constructor_rejects_null_data()
    {
        Action act = () => new BlobObject(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Empty_data_produces_valid_blob()
    {
        // Arrange & Act
        var blob = new BlobObject([]);

        // Assert
        blob.Data.Should().BeEmpty();
        blob.Id.Should().NotBe(ObjectId.Zero);
        blob.Serialize().Should().NotBeEmpty();
    }
}
