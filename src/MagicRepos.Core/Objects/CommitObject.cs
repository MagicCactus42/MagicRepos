using System.Text;

namespace MagicRepos.Core.Objects;

public sealed class CommitObject
{
    public ObjectId Id { get; }
    public ObjectId TreeId { get; }
    public IReadOnlyList<ObjectId> Parents { get; }
    public Signature Author { get; }
    public Signature Committer { get; }
    public string Message { get; }

    public CommitObject(
        ObjectId treeId,
        IEnumerable<ObjectId> parents,
        Signature author,
        Signature committer,
        string message)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(committer);
        ArgumentNullException.ThrowIfNull(message);

        TreeId = treeId;
        Parents = parents.ToList().AsReadOnly();
        Author = author;
        Committer = committer;
        Message = message;
        Id = ComputeId();
    }

    /// <summary>
    /// Serializes the commit content (without the header) in Git format:
    /// <code>
    /// tree {hex}\n
    /// parent {hex}\n  (per parent, if any)
    /// author {signature}\n
    /// committer {signature}\n
    /// \n
    /// {message}
    /// </code>
    /// </summary>
    private byte[] SerializeContent()
    {
        var sb = new StringBuilder();
        sb.Append($"tree {TreeId.ToHexString()}\n");

        foreach (var parent in Parents)
        {
            sb.Append($"parent {parent.ToHexString()}\n");
        }

        sb.Append($"author {Author}\n");
        sb.Append($"committer {Committer}\n");
        sb.Append('\n');
        sb.Append(Message);

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private ObjectId ComputeId()
    {
        var content = SerializeContent();
        var header = Encoding.UTF8.GetBytes($"{ObjectType.Commit.ToTypeString()} {content.Length}\0");
        var full = new byte[header.Length + content.Length];
        header.CopyTo(full, 0);
        content.CopyTo(full, header.Length);
        return ObjectId.Hash(full);
    }

    /// <summary>
    /// Returns the full serialized commit including the header: "commit {length}\0{content}".
    /// </summary>
    public byte[] Serialize()
    {
        var content = SerializeContent();
        var header = Encoding.UTF8.GetBytes($"{ObjectType.Commit.ToTypeString()} {content.Length}\0");
        var full = new byte[header.Length + content.Length];
        header.CopyTo(full, 0);
        content.CopyTo(full, header.Length);
        return full;
    }
}
