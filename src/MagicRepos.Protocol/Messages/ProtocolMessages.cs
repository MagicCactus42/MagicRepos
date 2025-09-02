using MessagePack;

namespace MagicRepos.Protocol.Messages;

public enum NegotiateOperation : byte
{
    Push = 1,
    Pull = 2,
    PullRequest = 3,
}

[MessagePackObject]
public class NegotiateRequest
{
    [Key(0)]
    public NegotiateOperation Operation { get; set; }

    [Key(1)]
    public string Repository { get; set; } = "";

    [Key(2)]
    public string? Username { get; set; }
}

[MessagePackObject]
public class NegotiateResponse
{
    [Key(0)]
    public bool Success { get; set; }

    [Key(1)]
    public string? ErrorMessage { get; set; }
}

[MessagePackObject]
public class RefAdvertisement
{
    /// <summary>
    /// Key: ref path (e.g. "refs/heads/main"), Value: 32-byte SHA-256 hash.
    /// </summary>
    [Key(0)]
    public Dictionary<string, byte[]> Refs { get; set; } = new();
}

[MessagePackObject]
public class RefUpdate
{
    [Key(0)]
    public List<RefUpdateEntry> Updates { get; set; } = new();
}

[MessagePackObject]
public class RefUpdateEntry
{
    [Key(0)]
    public string RefName { get; set; } = "";

    [Key(1)]
    public byte[] OldHash { get; set; } = new byte[32];

    [Key(2)]
    public byte[] NewHash { get; set; } = new byte[32];
}

[MessagePackObject]
public class RefWanted
{
    [Key(0)]
    public List<string> Refs { get; set; } = new();
}

[MessagePackObject]
public class PackData
{
    [Key(0)]
    public byte[] Data { get; set; } = Array.Empty<byte>();

    [Key(1)]
    public int SequenceNumber { get; set; }
}

[MessagePackObject]
public class PackComplete
{
    [Key(0)]
    public int TotalChunks { get; set; }

    [Key(1)]
    public byte[] Checksum { get; set; } = new byte[32];
}

[MessagePackObject]
public class OkResponse
{
    [Key(0)]
    public string? Message { get; set; }
}

[MessagePackObject]
public class ErrorResponse
{
    [Key(0)]
    public string Message { get; set; } = "";

    [Key(1)]
    public int Code { get; set; }
}

[MessagePackObject]
public class PrCreateRequest
{
    [Key(0)]
    public string Title { get; set; } = "";

    [Key(1)]
    public string Description { get; set; } = "";

    [Key(2)]
    public string SourceBranch { get; set; } = "";

    [Key(3)]
    public string TargetBranch { get; set; } = "";
}

[MessagePackObject]
public class PrListRequest
{
    [Key(0)]
    public string? State { get; set; }
}

[MessagePackObject]
public class PrReviewRequest
{
    [Key(0)]
    public int Number { get; set; }

    [Key(1)]
    public bool Approved { get; set; }

    [Key(2)]
    public string? Comment { get; set; }
}

[MessagePackObject]
public class PrMergeRequest
{
    [Key(0)]
    public int Number { get; set; }
}

[MessagePackObject]
public class PrResponse
{
    [Key(0)]
    public bool Success { get; set; }

    [Key(1)]
    public byte[]? Data { get; set; }
}
