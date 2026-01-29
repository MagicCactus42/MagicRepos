namespace MagicRepos.Core.Objects;

public enum ObjectType
{
    Blob,
    Tree,
    Commit
}

public static class ObjectTypeExtensions
{
    public static string ToTypeString(this ObjectType type) => type switch
    {
        ObjectType.Blob => "blob",
        ObjectType.Tree => "tree",
        ObjectType.Commit => "commit",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown object type.")
    };

    public static ObjectType ParseObjectType(string value) => value switch
    {
        "blob" => ObjectType.Blob,
        "tree" => ObjectType.Tree,
        "commit" => ObjectType.Commit,
        _ => throw new ArgumentException($"Unknown object type string: '{value}'.", nameof(value))
    };
}
