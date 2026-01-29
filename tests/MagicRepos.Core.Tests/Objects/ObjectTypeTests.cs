using FluentAssertions;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Tests.Objects;

public class ObjectTypeTests
{
    [Theory]
    [InlineData(ObjectType.Blob, "blob")]
    [InlineData(ObjectType.Tree, "tree")]
    [InlineData(ObjectType.Commit, "commit")]
    public void ToTypeString_returns_correct_string(ObjectType type, string expected)
    {
        type.ToTypeString().Should().Be(expected);
    }

    [Theory]
    [InlineData("blob", ObjectType.Blob)]
    [InlineData("tree", ObjectType.Tree)]
    [InlineData("commit", ObjectType.Commit)]
    public void ParseObjectType_returns_correct_enum(string value, ObjectType expected)
    {
        ObjectTypeExtensions.ParseObjectType(value).Should().Be(expected);
    }

    [Fact]
    public void ParseObjectType_throws_for_unknown_value()
    {
        Action act = () => ObjectTypeExtensions.ParseObjectType("unknown");
        act.Should().Throw<ArgumentException>().WithMessage("*unknown*");
    }

    [Fact]
    public void ToTypeString_throws_for_invalid_enum()
    {
        Action act = () => ((ObjectType)999).ToTypeString();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(ObjectType.Blob)]
    [InlineData(ObjectType.Tree)]
    [InlineData(ObjectType.Commit)]
    public void Roundtrip_through_ToTypeString_and_ParseObjectType(ObjectType type)
    {
        string str = type.ToTypeString();
        ObjectType parsed = ObjectTypeExtensions.ParseObjectType(str);
        parsed.Should().Be(type);
    }
}
