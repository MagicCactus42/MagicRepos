using System.Globalization;
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
    /// <summary>
    /// Maximum accepted size of a single protocol message payload (64 MiB).
    /// Guards against a malicious client advertising a huge or negative length
    /// and forcing an out-of-memory allocation.
    /// </summary>
    private const int MaxMessageLength = 64 * 1024 * 1024;

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
        try
        {
            await HandleSessionAsync(ct);
        }
        catch (Exception ex)
        {
            // Never let an unhandled exception tear the process down silently.
            // Log server-side and try to inform the client with a protocol Error.
            Console.Error.WriteLine($"[magicrepos] session error for user '{_authenticatedUser}': {ex}");
            try
            {
                await SendErrorAsync("Internal server error.", ct);
            }
            catch
            {
                // The connection is already gone; nothing more we can do.
            }
        }
    }

    private async Task HandleSessionAsync(CancellationToken ct)
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

        // Reject owner/repo names that could escape the data directory before they
        // reach any filesystem path construction.
        if (!IsValidPathSegment(repoOwner) || !IsValidPathSegment(repoName))
        {
            await SendErrorAsync("Invalid repository owner or name.", ct);
            return;
        }

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
                if (updateParts.Length < 2 ||
                    !IsValidRefName(updateParts[0]) ||
                    !ObjectId.TryParse(updateParts[1], out ObjectId newId))
                {
                    await SendErrorAsync("Malformed ref update.", ct);
                    return;
                }

                refUpdates.Add((updateParts[0], newId));
            }
            else if (type == MessageType.PackData)
            {
                // Payload: objectIdHex(64 bytes) + compressed object data
                if (data.Length <= ObjectId.HexLength)
                {
                    await SendErrorAsync("Malformed pack data.", ct);
                    return;
                }

                string idHex = Encoding.ASCII.GetString(data, 0, ObjectId.HexLength);
                if (!ObjectId.TryParse(idHex, out ObjectId objectId))
                {
                    await SendErrorAsync("Malformed object id in pack data.", ct);
                    return;
                }

                byte[] compressedData = new byte[data.Length - ObjectId.HexLength];
                Buffer.BlockCopy(data, ObjectId.HexLength, compressedData, 0, compressedData.Length);

                // Content-addressing integrity: the bytes must actually hash to the
                // claimed id, otherwise a client could poison the object store.
                if (!ObjectSerializer.TryVerify(compressedData, objectId))
                {
                    await SendErrorAsync($"Object {idHex} failed integrity verification.", ct);
                    return;
                }

                repo.ObjectStore.Write(objectId, compressedData);
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

        // Step 3: Validate that every ref target — and everything reachable from it —
        // was actually received, so we never advertise a ref that points at a missing
        // object (which would corrupt the repository for later pulls).
        foreach (var (refName, newId) in refUpdates)
        {
            if (!repo.Exists(newId))
            {
                await SendErrorAsync($"Push rejected: ref '{refName}' targets missing object {newId}.", ct);
                return;
            }

            if (!AllObjectsPresent(repo, newId))
            {
                await SendErrorAsync(
                    $"Push rejected: ref '{refName}' is missing one or more reachable objects.", ct);
                return;
            }
        }

        // Step 4: Reject non-fast-forward branch updates. An existing branch may only be
        // advanced to a descendant of its current tip; this prevents a stale or malicious
        // push from clobbering commits (there is no force-push in the protocol).
        foreach (var (refName, newId) in refUpdates)
        {
            if (!refName.StartsWith("refs/heads/", StringComparison.Ordinal))
                continue;

            string branchName = refName["refs/heads/".Length..];
            ObjectId? currentTip = repo.Refs.ResolveBranch(branchName);
            if (currentTip is not null && currentTip.Value != newId && !IsAncestor(repo, currentTip.Value, newId))
            {
                await SendErrorAsync(
                    $"Push rejected: non-fast-forward update to '{refName}'. " +
                    "Fetch and integrate the remote changes first.", ct);
                return;
            }
        }

        // Step 5: Update refs
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

        // Step 5: Send Ok
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
            if (!repo.ObjectStore.Exists(objId))
                continue; // A ref may transitively reference an object we do not have.

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
                // The author field is ignored: authorship is always the authenticated user,
                // so it cannot be forged.
                string[] parts = payload.Split('\0');
                if (parts.Length < 5)
                {
                    await SendErrorAsync("Invalid PrCreate payload.", ct);
                    return;
                }

                if (!IsValidRefName($"refs/heads/{parts[3]}") || !IsValidRefName($"refs/heads/{parts[4]}"))
                {
                    await SendErrorAsync("Invalid source or target branch name.", ct);
                    return;
                }

                var pr = prStore.Create(parts[0], parts[1], _authenticatedUser, parts[3], parts[4]);
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
                // The reviewer field is ignored: the review is always attributed to the
                // authenticated user, so approvals cannot be forged.
                string[] parts = payload.Split('\0');
                if (parts.Length < 4
                    || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int prNumber)
                    || !bool.TryParse(parts[2], out bool approved))
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
                    Reviewer = _authenticatedUser,
                    Approved = approved,
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
                if (!int.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out int prNumber))
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

                if (!IsValidRefName($"refs/heads/{pr.SourceBranch}") ||
                    !IsValidRefName($"refs/heads/{pr.TargetBranch}"))
                {
                    await SendErrorAsync("Pull request references an invalid branch name.", ct);
                    return;
                }

                // Perform fast-forward merge: set target branch to source branch tip.
                ObjectId? sourceId = repo.Refs.Resolve(pr.SourceBranch);
                if (sourceId is null)
                {
                    await SendErrorAsync($"Source branch '{pr.SourceBranch}' not found.", ct);
                    return;
                }

                // Only fast-forward merges are supported: the source tip must be a
                // descendant of the current target tip, otherwise the merge would
                // discard commits on the target branch.
                ObjectId? targetId = repo.Refs.Resolve(pr.TargetBranch);
                if (targetId is not null && !IsAncestor(repo, targetId.Value, sourceId.Value))
                {
                    await SendErrorAsync(
                        $"Pull request #{prNumber} cannot be fast-forwarded: " +
                        $"'{pr.TargetBranch}' has diverged from '{pr.SourceBranch}'.", ct);
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
    /// Walks commit parents, trees, and tree entries. Objects that are missing or
    /// malformed simply terminate that branch of the walk.
    /// </summary>
    private static void CollectObjectsRecursive(BareRepository repo, ObjectId id, HashSet<ObjectId> collected)
    {
        if (!collected.Add(id))
            return;

        if (!TryGetDirectReferences(repo, id, out var references))
            return;

        foreach (ObjectId child in references)
            CollectObjectsRecursive(repo, child, collected);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="root"/> and every object
    /// transitively reachable from it are present and well-formed in the store.
    /// Used to reject a push that would leave a ref pointing at missing objects.
    /// </summary>
    private static bool AllObjectsPresent(BareRepository repo, ObjectId root)
    {
        var visited = new HashSet<ObjectId>();
        var stack = new Stack<ObjectId>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ObjectId id = stack.Pop();
            if (!visited.Add(id))
                continue;

            if (!TryGetDirectReferences(repo, id, out var references))
                return false;

            foreach (ObjectId child in references)
                stack.Push(child);
        }

        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="ancestor"/> is reachable by
    /// following commit parents from <paramref name="descendant"/> (i.e. the merge from
    /// descendant onto ancestor would be a fast-forward).
    /// </summary>
    private static bool IsAncestor(BareRepository repo, ObjectId ancestor, ObjectId descendant)
    {
        var visited = new HashSet<ObjectId>();
        var stack = new Stack<ObjectId>();
        stack.Push(descendant);

        while (stack.Count > 0)
        {
            ObjectId id = stack.Pop();
            if (id == ancestor)
                return true;

            if (!visited.Add(id))
                continue;

            foreach (ObjectId parent in EnumerateCommitParents(repo, id))
                stack.Push(parent);
        }

        return false;
    }

    /// <summary>
    /// Yields the parent commit ids of <paramref name="id"/> if it is a commit; otherwise nothing.
    /// </summary>
    private static IEnumerable<ObjectId> EnumerateCommitParents(BareRepository repo, ObjectId id)
    {
        if (!repo.Exists(id))
            yield break;

        byte[] content;
        ObjectType objType;
        try
        {
            (objType, content) = ObjectSerializer.Deserialize(repo.ObjectStore.Read(id));
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            yield break;
        }

        if (objType != ObjectType.Commit)
            yield break;

        foreach (string line in Encoding.UTF8.GetString(content).Split('\n'))
        {
            if (line.StartsWith("parent ", StringComparison.Ordinal))
            {
                if (ObjectId.TryParse(line[7..], out ObjectId parentId))
                    yield return parentId;
            }
            else if (line.Length == 0)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Reads an object and returns the ids it directly references (commit tree+parents,
    /// or tree entries). Returns <see langword="false"/> if the object is missing or
    /// malformed, so callers can treat that as a corrupt/incomplete graph.
    /// </summary>
    private static bool TryGetDirectReferences(BareRepository repo, ObjectId id, out List<ObjectId> references)
    {
        references = new List<ObjectId>();

        if (!repo.Exists(id))
            return false;

        ObjectType objType;
        byte[] content;
        try
        {
            (objType, content) = ObjectSerializer.Deserialize(repo.ObjectStore.Read(id));
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            return false;
        }

        switch (objType)
        {
            case ObjectType.Commit:
                foreach (string line in Encoding.UTF8.GetString(content).Split('\n'))
                {
                    if (line.StartsWith("tree ", StringComparison.Ordinal))
                    {
                        if (!ObjectId.TryParse(line[5..], out ObjectId treeId))
                            return false;
                        references.Add(treeId);
                    }
                    else if (line.StartsWith("parent ", StringComparison.Ordinal))
                    {
                        if (!ObjectId.TryParse(line[7..], out ObjectId parentId))
                            return false;
                        references.Add(parentId);
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

                    int hashStart = nullIdx + 1;
                    if (hashStart + ObjectId.ByteLength > content.Length)
                        return false;

                    references.Add(new ObjectId(content.AsSpan(hashStart, ObjectId.ByteLength)));
                    offset = hashStart + ObjectId.ByteLength;
                }
                break;

            case ObjectType.Blob:
                break;
        }

        return true;
    }

    /// <summary>
    /// Validates a single path segment (repository owner or name) so it cannot be used
    /// to escape the server data directory.
    /// </summary>
    private static bool IsValidPathSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > 255)
            return false;

        if (segment is "." or "..")
            return false;

        foreach (char c in segment)
        {
            if (c == '/' || c == '\\' || c == '\0' || char.IsControl(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a fully-qualified ref name (e.g. "refs/heads/main") so it cannot escape
    /// the refs directory or contain path-traversal or control characters.
    /// </summary>
    private static bool IsValidRefName(string refName)
    {
        if (string.IsNullOrEmpty(refName) || refName.Length > 1024)
            return false;

        if (!refName.StartsWith("refs/", StringComparison.Ordinal))
            return false;

        if (refName.EndsWith('/') || refName.Contains("//", StringComparison.Ordinal))
            return false;

        foreach (string segment in refName.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;

            if (segment.Contains('\\') || segment.Contains('\0') || segment.Any(char.IsControl))
                return false;
        }

        return true;
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
        // Read as unsigned to reject negative lengths, and cap the size so a malicious
        // client cannot force a huge allocation.
        uint length = ((uint)lengthBuf[0] << 24) | ((uint)lengthBuf[1] << 16)
            | ((uint)lengthBuf[2] << 8) | lengthBuf[3];
        if (length > MaxMessageLength)
            throw new InvalidDataException($"Message length {length} exceeds the maximum of {MaxMessageLength} bytes.");

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
