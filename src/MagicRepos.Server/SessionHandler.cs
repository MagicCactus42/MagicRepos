using System.Text;
using MagicRepos.Protocol.Messages;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Storage;

namespace MagicRepos.Server;

/// <summary>
/// Handles a single SSH session by reading/writing a binary protocol on stdin/stdout.
///
/// Wire format (each message):
///   [4 bytes big-endian length][1 byte MessageType][payload bytes]
///
/// The payload encoding is message-type specific.
/// </summary>
public class SessionHandler
{
    private readonly ServerRepositoryManager _repoManager;
    private readonly AccessControl _accessControl;
    private readonly string _authenticatedUser;
    private readonly Stream _input;
    private readonly Stream _output;

    public SessionHandler(ServerRepositoryManager repoManager, AccessControl accessControl,
        string authenticatedUser, Stream input, Stream output)
    {
        _repoManager = repoManager;
        _accessControl = accessControl;
        _authenticatedUser = authenticatedUser;
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Main entry point: reads a <see cref="MessageType.NegotiateRequest"/>,
    /// determines the operation, and dispatches to the appropriate handler.
    /// </summary>
    public async Task HandleAsync(CancellationToken ct = default)
    {
        // Read the NegotiateRequest
        var (msgType, payload) = await ReadMessageAsync(ct);

        if (msgType != MessageType.NegotiateRequest)
        {
            await SendErrorAsync("Expected NegotiateRequest as the first message.", ct);
            return;
        }

        // Parse NegotiateRequest payload: "operation\0username\0repoName"
        string payloadStr = Encoding.UTF8.GetString(payload);
        string[] parts = payloadStr.Split('\0');
        if (parts.Length < 3)
        {
            await SendErrorAsync("Invalid NegotiateRequest format. Expected: operation\\0username\\0repoName", ct);
            return;
        }

        string operation = parts[0];
        string repoOwner = parts[1];
        string repoName = parts[2];

        // Access control checks
        switch (operation)
        {
            case "push":
                if (!_accessControl.CanWrite(_authenticatedUser, repoOwner, repoName))
                {
                    await SendErrorAsync(
                        $"Permission denied: user '{_authenticatedUser}' cannot push to {repoOwner}/{repoName}. " +
                        "You can only push to your own namespace or repos where you are a collaborator.", ct);
                    return;
                }
                break;
            case "pull":
                if (!_accessControl.CanRead(_authenticatedUser))
                {
                    await SendErrorAsync("Permission denied: not authenticated.", ct);
                    return;
                }
                break;
            case "pr":
                if (!_accessControl.CanRead(_authenticatedUser))
                {
                    await SendErrorAsync("Permission denied: not authenticated.", ct);
                    return;
                }
                break;
        }

        // Only auto-create repos in the user's own namespace (or if they have write access)
        BareRepository repo;
        if (_repoManager.Exists(repoOwner, repoName))
        {
            repo = _repoManager.GetOrCreate(repoOwner, repoName);
        }
        else if (operation == "push" && _accessControl.CanWrite(_authenticatedUser, repoOwner, repoName))
        {
            repo = _repoManager.GetOrCreate(repoOwner, repoName);
        }
        else
        {
            await SendErrorAsync($"Repository {repoOwner}/{repoName} not found.", ct);
            return;
        }

        // Send NegotiateResponse (protocol version acknowledgment)
        await WriteMessageAsync(MessageType.NegotiateResponse, Encoding.UTF8.GetBytes("v1"), ct);

        switch (operation)
        {
            case "push":
                await HandlePushAsync(repo, ct);
                break;
            case "pull":
                await HandlePullAsync(repo, ct);
                break;
            case "pr":
                await HandlePrAsync(repo, repoOwner, repoName, ct);
                break;
            default:
                await SendErrorAsync($"Unknown operation: {operation}", ct);
                break;
        }
    }

    /// <summary>
    /// Handles a push operation:
    /// 1. Send RefAdvertisement (current refs)
    /// 2. Receive RefUpdate + PackData chunks + PackComplete
    /// 3. Store objects in the object store
    /// 4. Update refs
    /// 5. Send Ok or Error
    /// </summary>
    private async Task HandlePushAsync(BareRepository repo, CancellationToken ct)
    {
        // Step 1: Advertise current refs
        await SendRefAdvertisementAsync(repo, ct);

        // Step 2: Receive ref updates and pack data
        var refUpdates = new List<(string RefName, ObjectId NewId)>();

        while (true)
        {
            var (type, data) = await ReadMessageAsync(ct);

            if (type == MessageType.RefUpdate)
            {
                // Payload: "refName\0newIdHex"
                string updateStr = Encoding.UTF8.GetString(data);
                string[] updateParts = updateStr.Split('\0');
                if (updateParts.Length >= 2)
                {
                    refUpdates.Add((updateParts[0], ObjectId.Parse(updateParts[1])));
                }
            }
            else if (type == MessageType.PackData)
            {
                // Payload: objectIdHex(64 bytes) + compressed object data
                if (data.Length > ObjectId.HexLength)
                {
                    string idHex = Encoding.ASCII.GetString(data, 0, ObjectId.HexLength);
                    ObjectId objectId = ObjectId.Parse(idHex);
                    byte[] compressedData = new byte[data.Length - ObjectId.HexLength];
                    Buffer.BlockCopy(data, ObjectId.HexLength, compressedData, 0, compressedData.Length);

                    repo.ObjectStore.Write(objectId, compressedData);
                }
            }
            else if (type == MessageType.PackComplete)
            {
                break;
            }
            else
            {
                await SendErrorAsync($"Unexpected message type during push: {type}", ct);
                return;
            }
        }

        // Step 3: Update refs
        foreach (var (refName, newId) in refUpdates)
        {
            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                string branchName = refName["refs/heads/".Length..];
                repo.Refs.CreateBranch(branchName, newId);
            }
            else if (refName.StartsWith("refs/", StringComparison.Ordinal))
            {
                repo.Refs.WriteRef(refName, newId);
            }
        }

        // Step 4: Send Ok
        await WriteMessageAsync(MessageType.Ok, Encoding.UTF8.GetBytes("Push completed successfully."), ct);
    }

    /// <summary>
    /// Handles a pull operation:
    /// 1. Send RefAdvertisement
    /// 2. Receive RefWanted (list of wanted ref names)
    /// 3. Walk the object graph for wanted refs
    /// 4. Send PackData chunks + PackComplete
    /// </summary>
    private async Task HandlePullAsync(BareRepository repo, CancellationToken ct)
    {
        // Step 1: Advertise current refs
        await SendRefAdvertisementAsync(repo, ct);

        // Step 2: Receive wanted refs
        var (type, data) = await ReadMessageAsync(ct);
        if (type != MessageType.RefWanted)
        {
            await SendErrorAsync("Expected RefWanted message.", ct);
            return;
        }

        // Payload: newline-separated ref names
        string wantedStr = Encoding.UTF8.GetString(data);
        string[] wantedRefs = wantedStr.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Step 3: Walk object graph and collect objects to send
        var objectsToSend = new HashSet<ObjectId>();
        foreach (string refName in wantedRefs)
        {
            ObjectId? commitId = repo.Refs.Resolve(refName);
            if (commitId is not null)
            {
                CollectObjectsRecursive(repo, commitId.Value, objectsToSend);
            }
        }

        // Step 4: Send pack data
        foreach (ObjectId objId in objectsToSend)
        {
            byte[] compressed = repo.ObjectStore.Read(objId);
            string idHex = objId.ToHexString();
            byte[] idBytes = Encoding.ASCII.GetBytes(idHex);

            byte[] packPayload = new byte[idBytes.Length + compressed.Length];
            Buffer.BlockCopy(idBytes, 0, packPayload, 0, idBytes.Length);
            Buffer.BlockCopy(compressed, 0, packPayload, idBytes.Length, compressed.Length);

            await WriteMessageAsync(MessageType.PackData, packPayload, ct);
        }

        await WriteMessageAsync(MessageType.PackComplete, Array.Empty<byte>(), ct);
    }

    /// <summary>
    /// Handles pull request operations (create, list, review, merge).
    /// </summary>
    private async Task HandlePrAsync(BareRepository repo, string repoOwner, string repoName, CancellationToken ct)
    {
        var prStore = new PullRequestStore(repo.Path);

        var (type, data) = await ReadMessageAsync(ct);
        string payload = Encoding.UTF8.GetString(data);

        switch (type)
        {
            case MessageType.PrCreate:
            {
                // Payload: "title\0description\0author\0sourceBranch\0targetBranch"
                string[] parts = payload.Split('\0');
                if (parts.Length < 5)
                {
                    await SendErrorAsync("Invalid PrCreate payload.", ct);
                    return;
                }

                var pr = prStore.Create(parts[0], parts[1], parts[2], parts[3], parts[4]);
                string response = $"Created pull request #{pr.Number}: {pr.Title}";
                await WriteMessageAsync(MessageType.PrResponse, Encoding.UTF8.GetBytes(response), ct);
                break;
            }

            case MessageType.PrList:
            {
                // Payload: optional state filter ("open", "closed", "merged", or empty for all)
                PullRequestState? filter = payload switch
                {
                    "open" => PullRequestState.Open,
                    "closed" => PullRequestState.Closed,
                    "merged" => PullRequestState.Merged,
                    _ => null
                };

                var prs = prStore.List(filter);
                var sb = new StringBuilder();
                foreach (var pr in prs)
                {
                    sb.AppendLine($"#{pr.Number} [{pr.State}] {pr.Title} ({pr.SourceBranch} -> {pr.TargetBranch})");
                }

                await WriteMessageAsync(MessageType.PrResponse, Encoding.UTF8.GetBytes(sb.ToString()), ct);
                break;
            }

            case MessageType.PrReview:
            {
                // Payload: "number\0reviewer\0approved(true/false)\0comment"
                string[] parts = payload.Split('\0');
                if (parts.Length < 4 || !int.TryParse(parts[0], out int prNumber))
                {
                    await SendErrorAsync("Invalid PrReview payload.", ct);
                    return;
                }

                var pr = prStore.Get(prNumber);
                if (pr is null)
                {
                    await SendErrorAsync($"Pull request #{prNumber} not found.", ct);
                    return;
                }

                pr.Reviews.Add(new PullRequestReview
                {
                    Reviewer = parts[1],
                    Approved = bool.Parse(parts[2]),
                    Comment = parts[3],
                    CreatedAt = DateTime.UtcNow
                });

                prStore.Update(pr);
                await WriteMessageAsync(MessageType.PrResponse,
                    Encoding.UTF8.GetBytes($"Review added to PR #{prNumber}."), ct);
                break;
            }

            case MessageType.PrMerge:
            {
                // Write access required to merge
                if (!_accessControl.CanWrite(_authenticatedUser, repoOwner, repoName))
                {
                    await SendErrorAsync(
                        $"Permission denied: user '{_authenticatedUser}' cannot merge PRs in {repoOwner}/{repoName}. " +
                        "Write access is required.", ct);
                    return;
                }

                // Payload: "number"
                if (!int.TryParse(payload, out int prNumber))
                {
                    await SendErrorAsync("Invalid PrMerge payload.", ct);
                    return;
                }

                var pr = prStore.Get(prNumber);
                if (pr is null)
                {
                    await SendErrorAsync($"Pull request #{prNumber} not found.", ct);
                    return;
                }

                if (pr.State != PullRequestState.Open)
                {
                    await SendErrorAsync($"Pull request #{prNumber} is not open (state: {pr.State}).", ct);
                    return;
                }

                // Perform fast-forward merge: set target branch to source branch tip
                ObjectId? sourceId = repo.Refs.Resolve(pr.SourceBranch);
                if (sourceId is null)
                {
                    await SendErrorAsync($"Source branch '{pr.SourceBranch}' not found.", ct);
                    return;
                }

                repo.Refs.CreateBranch(pr.TargetBranch, sourceId.Value);
                pr.State = PullRequestState.Merged;
                pr.MergedAt = DateTime.UtcNow;
                prStore.Update(pr);

                await WriteMessageAsync(MessageType.PrResponse,
                    Encoding.UTF8.GetBytes($"Pull request #{prNumber} merged."), ct);
                break;
            }

            default:
                await SendErrorAsync($"Unexpected message type in PR session: {type}", ct);
                break;
        }
    }

    // ──────────────────────────── Helpers ────────────────────────────

    /// <summary>
    /// Recursively collects all reachable object IDs starting from a commit.
    /// Walks commit parents, trees, and tree entries.
    /// </summary>
    private static void CollectObjectsRecursive(BareRepository repo, ObjectId id, HashSet<ObjectId> collected)
    {
        if (!collected.Add(id))
            return;

        if (!repo.Exists(id))
            return;

        byte[] compressed = repo.ObjectStore.Read(id);
        var (objType, content) = ObjectSerializer.Deserialize(compressed);

        switch (objType)
        {
            case ObjectType.Commit:
                // Parse tree and parent IDs from the commit content
                string commitText = Encoding.UTF8.GetString(content);
                foreach (string line in commitText.Split('\n'))
                {
                    if (line.StartsWith("tree ", StringComparison.Ordinal))
                    {
                        ObjectId treeId = ObjectId.Parse(line[5..]);
                        CollectObjectsRecursive(repo, treeId, collected);
                    }
                    else if (line.StartsWith("parent ", StringComparison.Ordinal))
                    {
                        ObjectId parentId = ObjectId.Parse(line[7..]);
                        CollectObjectsRecursive(repo, parentId, collected);
                    }
                    else if (line.Length == 0)
                    {
                        break; // End of headers
                    }
                }
                break;

            case ObjectType.Tree:
                // Parse tree entries: "{mode} {name}\0{32-byte hash}" repeating
                int offset = 0;
                while (offset < content.Length)
                {
                    int nullIdx = Array.IndexOf(content, (byte)0, offset);
                    if (nullIdx < 0) break;

                    // Skip mode and name, read the 32-byte hash after the null
                    int hashStart = nullIdx + 1;
                    if (hashStart + ObjectId.ByteLength > content.Length) break;

                    var entryId = new ObjectId(content.AsSpan(hashStart, ObjectId.ByteLength));
                    CollectObjectsRecursive(repo, entryId, collected);
                    offset = hashStart + ObjectId.ByteLength;
                }
                break;

            case ObjectType.Blob:
                // Leaf node – nothing to traverse
                break;
        }
    }

    /// <summary>
    /// Sends a RefAdvertisement containing all branch refs and HEAD.
    /// Payload: newline-separated entries of "refName hexHash".
    /// </summary>
    private async Task SendRefAdvertisementAsync(BareRepository repo, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // Advertise HEAD
        ObjectId? headId = repo.Refs.ResolveHead();
        if (headId is not null)
        {
            sb.AppendLine($"HEAD {headId.Value.ToHexString()}");
        }

        // Advertise all branches
        foreach (string branch in repo.Refs.ListBranches())
        {
            ObjectId? branchId = repo.Refs.ResolveBranch(branch);
            if (branchId is not null)
            {
                sb.AppendLine($"refs/heads/{branch} {branchId.Value.ToHexString()}");
            }
        }

        await WriteMessageAsync(MessageType.RefAdvertisement, Encoding.UTF8.GetBytes(sb.ToString()), ct);
    }

    private async Task SendErrorAsync(string message, CancellationToken ct)
    {
        await WriteMessageAsync(MessageType.Error, Encoding.UTF8.GetBytes(message), ct);
    }

    // ──────────────────────────── Wire format ────────────────────────────

    /// <summary>
    /// Reads a single message from the input stream.
    /// Format: [4 bytes big-endian payload length][1 byte MessageType][payload bytes]
    /// </summary>
    private async Task<(MessageType Type, byte[] Payload)> ReadMessageAsync(CancellationToken ct)
    {
        byte[] lengthBuf = new byte[4];
        await ReadExactAsync(_input, lengthBuf, ct);
        int length = (lengthBuf[0] << 24) | (lengthBuf[1] << 16) | (lengthBuf[2] << 8) | lengthBuf[3];

        byte[] typeBuf = new byte[1];
        await ReadExactAsync(_input, typeBuf, ct);
        var msgType = (MessageType)typeBuf[0];

        byte[] payload = new byte[length];
        if (length > 0)
        {
            await ReadExactAsync(_input, payload, ct);
        }

        return (msgType, payload);
    }

    /// <summary>
    /// Writes a single message to the output stream.
    /// </summary>
    private async Task WriteMessageAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        int length = payload.Length;
        byte[] header = new byte[5];
        header[0] = (byte)((length >> 24) & 0xFF);
        header[1] = (byte)((length >> 16) & 0xFF);
        header[2] = (byte)((length >> 8) & 0xFF);
        header[3] = (byte)(length & 0xFF);
        header[4] = (byte)type;

        await _output.WriteAsync(header, ct);
        if (payload.Length > 0)
        {
            await _output.WriteAsync(payload, ct);
        }

        await _output.FlushAsync(ct);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream.");
            offset += read;
        }
    }
}
