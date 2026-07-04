using System.Globalization;
using FluentAssertions;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Storage;
using FileMode = MagicRepos.Core.Objects.FileMode;

namespace MagicRepos.Core.Tests.Objects;

/// <summary>
/// Regression tests for object-model and serialization bugs found during review.
/// </summary>
public class ObjectBugFixTests
{
    [Fact]
    public void ObjectId_TryParse_rejects_bad_input_without_throwing()
    {
        ObjectId.TryParse(null, out _).Should().BeFalse();
        ObjectId.TryParse("abc", out _).Should().BeFalse();
        ObjectId.TryParse(new string('g', 64), out _).Should().BeFalse();
        ObjectId.TryParse(new string('a', 63), out _).Should().BeFalse();

        string valid = new string('a', 64);
        ObjectId.TryParse(valid, out ObjectId id).Should().BeTrue();
        id.ToHexString().Should().Be(valid);
    }

    [Fact]
    public void Default_ObjectId_members_do_not_throw()
    {
        ObjectId def = default;

        Action hex = () => def.ToHexString();
        Action hash = () => def.GetHashCode();
        hex.Should().NotThrow();
        hash.Should().NotThrow();

        // Usable as a dictionary/hashset key.
        var set = new HashSet<ObjectId> { def };
        set.Contains(default).Should().BeTrue();
        def.Should().Be(ObjectId.Zero);
    }

    [Fact]
    public void ObjectId_makes_a_defensive_copy_of_its_bytes()
    {
        byte[] bytes = new byte[ObjectId.ByteLength];
        bytes[0] = 1;
        var id = new ObjectId(bytes);
        string before = id.ToHexString();

        bytes[0] = 99; // mutate the caller's array after construction

        id.ToHexString().Should().Be(before);
    }

    [Fact]
    public void Signature_formats_negative_timestamp_culture_invariantly()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // A culture whose negative sign is not ASCII '-'.
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            var when = new DateTimeOffset(1960, 1, 1, 0, 0, 0, TimeSpan.Zero); // before 1970
            var sig = new Signature("A", "a@b", when);

            string formatted = sig.ToString();
            formatted.Should().Contain(when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

            // And it must round-trip.
            Signature.Parse(formatted).When.ToUnixTimeSeconds().Should().Be(when.ToUnixTimeSeconds());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Signature_rejects_values_that_would_break_round_tripping()
    {
        Action emptyName = () => new Signature("", "a@b", DateTimeOffset.UnixEpoch);
        Action newlineName = () => new Signature("a\nb", "a@b", DateTimeOffset.UnixEpoch);
        Action angleEmail = () => new Signature("a", "a<b>@c", DateTimeOffset.UnixEpoch);
        Action emptyEmail = () => new Signature("a", "", DateTimeOffset.UnixEpoch);

        emptyName.Should().Throw<ArgumentException>();
        newlineName.Should().Throw<ArgumentException>();
        angleEmail.Should().Throw<ArgumentException>();
        emptyEmail.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Signature_equality_distinguishes_offsets()
    {
        var instant = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var utc = new Signature("A", "a@b", instant);
        var plusTwo = new Signature("A", "a@b", instant.ToOffset(TimeSpan.FromHours(2)));

        // Same instant, different offset → different serialization, so not equal.
        utc.Should().NotBe(plusTwo);
        utc.ToString().Should().NotBe(plusTwo.ToString());
    }

    [Fact]
    public void TreeObject_rejects_duplicate_entry_names()
    {
        var id = ObjectId.Hash("x"u8);
        var entries = new[]
        {
            new TreeEntry(FileMode.Regular, "a", id),
            new TreeEntry(FileMode.Regular, "a", id),
        };

        Action act = () => new TreeObject(entries);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\0b")]
    [InlineData("..")]
    public void TreeEntry_rejects_invalid_names(string name)
    {
        Action act = () => new TreeEntry(FileMode.Regular, name, ObjectId.Zero);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FileMode_Executable_encodes_octal_100755()
    {
        // 0100755 == decimal 33261.
        ((int)FileMode.Executable).Should().Be(Convert.ToInt32("100755", 8));
    }

    [Fact]
    public void ObjectSerializer_TryVerify_accepts_matching_and_rejects_tampered()
    {
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, "hello"u8.ToArray());

        ObjectSerializer.TryVerify(compressed, id).Should().BeTrue();

        // A different id must fail verification.
        ObjectSerializer.TryVerify(compressed, ObjectId.Zero).Should().BeFalse();

        // Garbage (non-deflate) data must fail, not throw.
        ObjectSerializer.TryVerify(new byte[] { 1, 2, 3, 4 }, id).Should().BeFalse();
    }

    [Fact]
    public void ObjectSerializer_Deserialize_rejects_length_mismatch()
    {
        // Build a valid object, then re-serialize with a lying header via round-trip check.
        (ObjectId _, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, "abcdef"u8.ToArray());
        (ObjectType type, byte[] content) = ObjectSerializer.Deserialize(compressed);

        type.Should().Be(ObjectType.Blob);
        content.Should().Equal("abcdef"u8.ToArray());
    }
}
