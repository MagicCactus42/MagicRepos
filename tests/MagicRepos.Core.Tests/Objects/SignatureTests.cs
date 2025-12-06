using FluentAssertions;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Tests.Objects;

public class SignatureTests
{
    [Fact]
    public void ToString_formats_correctly_with_positive_offset()
    {
        // Arrange
        var when = new DateTimeOffset(2025, 6, 15, 14, 30, 0, TimeSpan.FromHours(2));
        var sig = new Signature("Alice Smith", "alice@example.com", when);

        // Act
        string result = sig.ToString();

        // Assert
        long expectedUnix = when.ToUnixTimeSeconds();
        result.Should().Be($"Alice Smith <alice@example.com> {expectedUnix} +0200");
    }

    [Fact]
    public void ToString_formats_correctly_with_negative_offset()
    {
        // Arrange
        var when = new DateTimeOffset(2025, 1, 10, 8, 0, 0, TimeSpan.FromHours(-5));
        var sig = new Signature("Bob Jones", "bob@example.com", when);

        // Act
        string result = sig.ToString();

        // Assert
        long expectedUnix = when.ToUnixTimeSeconds();
        result.Should().Be($"Bob Jones <bob@example.com> {expectedUnix} -0500");
    }

    [Fact]
    public void ToString_formats_correctly_with_utc_offset()
    {
        // Arrange
        var when = new DateTimeOffset(2025, 3, 20, 0, 0, 0, TimeSpan.Zero);
        var sig = new Signature("Charlie", "charlie@test.org", when);

        // Act
        string result = sig.ToString();

        // Assert
        long expectedUnix = when.ToUnixTimeSeconds();
        result.Should().Be($"Charlie <charlie@test.org> {expectedUnix} +0000");
    }

    [Fact]
    public void Parse_roundtrips_with_ToString()
    {
        // Arrange
        var when = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.FromHours(3));
        var original = new Signature("Alice Smith", "alice@example.com", when);
        string formatted = original.ToString();

        // Act
        Signature parsed = Signature.Parse(formatted);

        // Assert
        parsed.Name.Should().Be(original.Name);
        parsed.Email.Should().Be(original.Email);
        parsed.When.ToUnixTimeSeconds().Should().Be(original.When.ToUnixTimeSeconds());
        parsed.When.Offset.Should().Be(original.When.Offset);
    }

    [Fact]
    public void Parse_handles_negative_timezone()
    {
        // Arrange
        var when = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(-7));
        var original = new Signature("Dev User", "dev@dev.io", when);
        string formatted = original.ToString();

        // Act
        Signature parsed = Signature.Parse(formatted);

        // Assert
        parsed.When.Offset.Should().Be(TimeSpan.FromHours(-7));
    }

    [Fact]
    public void Parse_handles_half_hour_offset()
    {
        // Arrange — India Standard Time is +05:30
        var when = new DateTimeOffset(2025, 6, 1, 10, 0, 0, new TimeSpan(5, 30, 0));
        var original = new Signature("Dev", "dev@example.com", when);
        string formatted = original.ToString();

        // Act
        Signature parsed = Signature.Parse(formatted);

        // Assert
        parsed.When.Offset.Should().Be(new TimeSpan(5, 30, 0));
    }

    [Fact]
    public void Parse_throws_on_invalid_format()
    {
        Action act = () => Signature.Parse("not a valid signature");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_throws_on_null()
    {
        Action act = () => Signature.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Signature_record_equality()
    {
        var when = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var sig1 = new Signature("Name", "email@test.com", when);
        var sig2 = new Signature("Name", "email@test.com", when);

        sig1.Should().Be(sig2);
    }
}
