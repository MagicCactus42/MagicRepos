using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Tests.Objects;

public class ObjectIdTests
{
    [Fact]
    public void Hash_of_known_input_produces_expected_SHA256()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("hello world");
        byte[] expected = SHA256.HashData(data);

        // Act
        ObjectId id = ObjectId.Hash(data);

        // Assert
        id.Bytes.ToArray().Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Parse_valid_hex_string_roundtrips()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("test data");
        ObjectId original = ObjectId.Hash(data);
        string hex = original.ToHexString();

        // Act
        ObjectId parsed = ObjectId.Parse(hex);

        // Assert
        parsed.Should().Be(original);
        parsed.ToHexString().Should().Be(hex);
    }

    [Fact]
    public void Parse_invalid_hex_throws()
    {
        // Too short
        Action tooShort = () => ObjectId.Parse("abcdef");
        tooShort.Should().Throw<ArgumentException>();

        // Wrong length (63 chars)
        Action wrongLength = () => ObjectId.Parse(new string('a', 63));
        wrongLength.Should().Throw<ArgumentException>();

        // Null
        Action nullArg = () => ObjectId.Parse(null!);
        nullArg.Should().Throw<ArgumentNullException>();

        // Non-hex characters in valid-length string
        string nonHex = new string('g', 64);
        Action nonHexAction = () => ObjectId.Parse(nonHex);
        nonHexAction.Should().Throw<Exception>();
    }

    [Fact]
    public void Prefix_returns_first_two_hex_chars()
    {
        // Arrange
        string hex = "ab" + new string('0', 62);
        ObjectId id = ObjectId.Parse(hex);

        // Act & Assert
        id.Prefix.Should().Be("ab");
    }

    [Fact]
    public void Suffix_returns_remaining_62_hex_chars()
    {
        // Arrange
        string hex = "ab" + new string('c', 62);
        ObjectId id = ObjectId.Parse(hex);

        // Act & Assert
        id.Suffix.Should().Be(new string('c', 62));
    }

    [Fact]
    public void Equality_works_for_same_hash()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("identical");
        ObjectId id1 = ObjectId.Hash(data);
        ObjectId id2 = ObjectId.Hash(data);

        // Act & Assert
        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.Equals(id2).Should().BeTrue();
        id1.Equals((object)id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    public void Inequality_for_different_hash()
    {
        // Arrange
        ObjectId id1 = ObjectId.Hash(Encoding.UTF8.GetBytes("first"));
        ObjectId id2 = ObjectId.Hash(Encoding.UTF8.GetBytes("second"));

        // Act & Assert
        id1.Should().NotBe(id2);
        (id1 != id2).Should().BeTrue();
        (id1 == id2).Should().BeFalse();
    }

    [Fact]
    public void Zero_is_all_zeroes()
    {
        // Act
        ObjectId zero = ObjectId.Zero;

        // Assert
        zero.ToHexString().Should().Be(new string('0', 64));
        zero.Bytes.ToArray().Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void ToHexString_returns_lowercase_64_chars()
    {
        // Arrange
        byte[] data = Encoding.UTF8.GetBytes("some content");
        ObjectId id = ObjectId.Hash(data);

        // Act
        string hex = id.ToHexString();

        // Assert
        hex.Should().HaveLength(64);
        hex.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ToString_returns_same_as_ToHexString()
    {
        // Arrange
        ObjectId id = ObjectId.Hash(Encoding.UTF8.GetBytes("test"));

        // Act & Assert
        id.ToString().Should().Be(id.ToHexString());
    }

    [Fact]
    public void Constructor_rejects_wrong_byte_length()
    {
        // Arrange & Act
        Action tooFew = () => new ObjectId(new byte[16]);
        Action tooMany = () => new ObjectId(new byte[64]);
        Action nullBytes = () => new ObjectId((byte[])null!);

        // Assert
        tooFew.Should().Throw<ArgumentException>();
        tooMany.Should().Throw<ArgumentException>();
        nullBytes.Should().Throw<ArgumentException>();
    }
}
