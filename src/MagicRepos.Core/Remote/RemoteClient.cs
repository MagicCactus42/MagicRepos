using System.Diagnostics;
using System.Text;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Storage;
using MagicRepos.Core.Refs;

namespace MagicRepos.Core.Remote;

/// <summary>
/// Represents a parsed remote URL in the format: magicrepos@hostname:username/reponame
/// </summary>
public record RemoteUrl(string Host, string Username, string RepoName)
{
    /// <summary>
    /// Parses a remote URL string of the form "magicrepos@hostname:username/reponame".
    /// </summary>
    public static RemoteUrl Parse(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Expected format: magicrepos@hostname:username/reponame
        const string prefix = "magicrepos@";
        if (!url.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException($"Invalid remote URL: must start with '{prefix}'. Got: '{url}'");

        string remainder = url[prefix.Length..]; // "hostname:username/reponame"

        int colonIndex = remainder.IndexOf(':');
        if (colonIndex < 0)
            throw new FormatException($"Invalid remote URL: missing ':' separator. Got: '{url}'");

        string host = remainder[..colonIndex];
        string pathPart = remainder[(colonIndex + 1)..]; // "username/reponame"

        int slashIndex = pathPart.IndexOf('/');
        if (slashIndex < 0)
            throw new FormatException($"Invalid remote URL: missing '/' between username and repo name. Got: '{url}'");

        string username = pathPart[..slashIndex];
        string repoName = pathPart[(slashIndex + 1)..];

        if (string.IsNullOrEmpty(host))
            throw new FormatException($"Invalid remote URL: empty hostname. Got: '{url}'");
        if (string.IsNullOrEmpty(username))
            throw new FormatException($"Invalid remote URL: empty username. Got: '{url}'");
        if (string.IsNullOrEmpty(repoName))
            throw new FormatException($"Invalid remote URL: empty repo name. Got: '{url}'");

        return new RemoteUrl(host, username, repoName);
    }

    public override string ToString() => $"magicrepos@{Host}:{Username}/{RepoName}";
}

/// <summary>
/// Client-side counterpart to the server's SessionHandler. Spawns an SSH process
/// and communicates via stdin/stdout using the same binary wire format the server expects.
///
/// Wire format (each message):
///   [4 bytes big-endian payload length][1 byte MessageType][payload bytes]
///   (length = payload.Length, NOT including the type byte)
/// </summary>
public class RemoteClient : IDisposable
{
    // MessageType constants matching MagicRepos.Protocol.Messages.MessageType,
    // duplicated here to avoid a dependency from Core -> Protocol.
    private const byte MsgNegotiateRequest = 1;
    private const byte MsgNegotiateResponse = 2;
    private const byte MsgRefAdvertisement = 3;
    private const byte MsgRefUpdate = 4;
    private const byte MsgRefWanted = 5;
    private const byte MsgPackData = 6;
    private const byte MsgPackComplete = 7;
    private const byte MsgOk = 8;
    private const byte MsgError = 9;

    private Process? _process;
    private Stream? _stdin;
    private Stream? _stdout;

    /// <summary>
    /// Connects to the remote host by spawning an SSH process that invokes the
    /// magicrepos-server on the remote end, then communicates via stdin/stdout.
    /// </summary>
    public void Connect(RemoteUrl url)
    {
        _process = new Process();
        _process.StartInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = $"magicrepos@{url.Host} magicrepos-server serve --stdin",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _process.Start();
        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;
    }

    /// <summary>
    /// Pushes local objects and branch refs to the remote.
    /// Sends all objects reachable from local branch tips that the remote does not
    /// already have, then updates the remote's refs.
    /// </summary>
    public async Task PushAsync(ObjectStore localStore, RefStore localRefs, string remoteName, CancellationToken ct = default)
    {
        // 1. Send NegotiateRequest("push\0username\0repoName")
        //    The remoteName is used to look up the URL, but we need the URL to
        //    have been parsed before Connect(). The username/repoName come from
        //    the RemoteUrl used at Connect time. We re-derive them from the
        //    connection info embedded in the negotiate request sent at Connect.
        //    Actually, the caller should pass the RemoteUrl or we parse it from config.
        //    For simplicity, we accept the remote URL parts via a helper overload.
        //    This method is called after Connect(), so we require the URL fields to
        //    have been provided separately.
        throw new InvalidOperationException(
            "Use the overload PushAsync(ObjectStore, RefStore, RemoteUrl, CancellationToken) instead.");
    }

    /// <summary>
    /// Pushes local objects and branch refs to the remote.
    /// </summary>
    public async Task PushAsync(ObjectStore localStore, RefStore localRefs, RemoteUrl remoteUrl, CancellationToken ct = default)
    {
        EnsureConnected();

        // 1. Send NegotiateRequest: "push\0username\0repoName"
        string negotiatePayload = $"push\0{remoteUrl.Username}\0{remoteUrl.RepoName}";
        await WriteMessageAsync(MsgNegotiateRequest, Encoding.UTF8.GetBytes(negotiatePayload), ct);

        // 2. Read NegotiateResponse
        var (respType, respPayload) = await ReadMessageAsync(ct);
        if (respType == MsgError)
            throw new InvalidOperationException($"Remote error during negotiation: {Encoding.UTF8.GetString(respPayload)}");
        if (respType != MsgNegotiateResponse)
            throw new InvalidOperationException($"Expected NegotiateResponse, got message type {respType}.");

        string version = Encoding.UTF8.GetString(respPayload);
        if (version != "v1")
            throw new InvalidOperationException($"Unsupported protocol version: {version}");

        // 3. Read RefAdvertisement (parse remote refs)
        var (advType, advPayload) = await ReadMessageAsync(ct);
        if (advType == MsgError)
            throw new InvalidOperationException($"Remote error: {Encoding.UTF8.GetString(advPayload)}");
        if (advType != MsgRefAdvertisement)
            throw new InvalidOperationException($"Expected RefAdvertisement, got message type {advType}.");

        Dictionary<string, ObjectId> remoteRefs = ParseRefAdvertisement(advPayload);

        // 4. Determine which local branches to push (all refs/heads/*)
        IReadOnlyList<string> localBranches = localRefs.ListBranches();

        // Build set of remote object IDs so we can skip objects the remote already has
        var remoteObjectIds = new HashSet<ObjectId>();
        foreach (ObjectId remoteId in remoteRefs.Values)
        {
            // We cannot walk remote objects (we don't have them locally necessarily),
            // but we can skip sending objects whose commit tips are already known.
            remoteObjectIds.Add(remoteId);
        }

        // 5. Collect all objects reachable from local branch tips
        var allObjects = new HashSet<ObjectId>();
        var refUpdates = new List<(string RefName, ObjectId NewId)>();

        foreach (string branch in localBranches)
        {
            ObjectId? branchId = localRefs.ResolveBranch(branch);
            if (branchId is null)
                continue;

            string refName = $"refs/heads/{branch}";
            refUpdates.Add((refName, branchId.Value));

            // Walk the object graph from this commit
            CollectObjectsRecursive(localStore, branchId.Value, allObjects);
        }

        // 6. Send RefUpdate for each branch
        foreach (var (refName, newId) in refUpdates)
        {
            string updatePayload = $"{refName}\0{newId.ToHexString()}";
            await WriteMessageAsync(MsgRefUpdate, Encoding.UTF8.GetBytes(updatePayload), ct);
        }

        // 7. Send PackData for each object
        foreach (ObjectId objId in allObjects)
        {
            byte[] compressed = localStore.Read(objId);
            string idHex = objId.ToHexString();
            byte[] idBytes = Encoding.ASCII.GetBytes(idHex);

            byte[] packPayload = new byte[idBytes.Length + compressed.Length];
            Buffer.BlockCopy(idBytes, 0, packPayload, 0, idBytes.Length);
            Buffer.BlockCopy(compressed, 0, packPayload, idBytes.Length, compressed.Length);

            await WriteMessageAsync(MsgPackData, packPayload, ct);
        }

        // 8. Send PackComplete
        await WriteMessageAsync(MsgPackComplete, Array.Empty<byte>(), ct);

        // 9. Read Ok/Error response
        var (resultType, resultPayload) = await ReadMessageAsync(ct);
        if (resultType == MsgError)
            throw new InvalidOperationException($"Push failed: {Encoding.UTF8.GetString(resultPayload)}");
        if (resultType != MsgOk)
            throw new InvalidOperationException($"Unexpected response after push: message type {resultType}");
    }

    /// <summary>
    /// Pulls objects from the remote and updates local remote-tracking refs.
    /// Returns a dictionary mapping remote ref names to their object IDs.
    /// </summary>
    public async Task<Dictionary<string, ObjectId>> PullAsync(ObjectStore localStore, RefStore localRefs, string remoteName, CancellationToken ct = default)
    {
        throw new InvalidOperationException(
            "Use the overload PullAsync(ObjectStore, RefStore, RemoteUrl, string, CancellationToken) instead.");
    }

    /// <summary>
    /// Pulls objects from the remote and updates local remote-tracking refs.
    /// Returns a dictionary mapping remote ref names to their object IDs.
    /// </summary>
    public async Task<Dictionary<string, ObjectId>> PullAsync(ObjectStore localStore, RefStore localRefs, RemoteUrl remoteUrl, string remoteName, CancellationToken ct = default)
    {
        EnsureConnected();

        // 1. Send NegotiateRequest: "pull\0username\0repoName"
        string negotiatePayload = $"pull\0{remoteUrl.Username}\0{remoteUrl.RepoName}";
        await WriteMessageAsync(MsgNegotiateRequest, Encoding.UTF8.GetBytes(negotiatePayload), ct);

        // 2. Read NegotiateResponse
        var (respType, respPayload) = await ReadMessageAsync(ct);
        if (respType == MsgError)
            throw new InvalidOperationException($"Remote error during negotiation: {Encoding.UTF8.GetString(respPayload)}");
        if (respType != MsgNegotiateResponse)
            throw new InvalidOperationException($"Expected NegotiateResponse, got message type {respType}.");

        string version = Encoding.UTF8.GetString(respPayload);
        if (version != "v1")
            throw new InvalidOperationException($"Unsupported protocol version: {version}");

        // 3. Read RefAdvertisement
        var (advType, advPayload) = await ReadMessageAsync(ct);
        if (advType == MsgError)
            throw new InvalidOperationException($"Remote error: {Encoding.UTF8.GetString(advPayload)}");
        if (advType != MsgRefAdvertisement)
            throw new InvalidOperationException($"Expected RefAdvertisement, got message type {advType}.");

        Dictionary<string, ObjectId> remoteRefs = ParseRefAdvertisement(advPayload);

        // Handle empty remote (no refs advertised)
        if (remoteRefs.Count == 0)
        {
            // Send an empty RefWanted, then expect PackComplete
            await WriteMessageAsync(MsgRefWanted, Encoding.UTF8.GetBytes(""), ct);

            // Server will send PackComplete with no PackData
            var (completeType, _) = await ReadMessageAsync(ct);
            if (completeType != MsgPackComplete)
                throw new InvalidOperationException($"Expected PackComplete for empty repo, got message type {completeType}.");

            return new Dictionary<string, ObjectId>();
        }

        // 4. Send RefWanted: newline-separated ref names (all advertised branch refs)
        var wantedRefs = new List<string>();
        foreach (string refName in remoteRefs.Keys)
        {
            // Request all advertised refs (branches and HEAD)
            wantedRefs.Add(refName);
        }

        string wantedPayload = string.Join('\n', wantedRefs);
        await WriteMessageAsync(MsgRefWanted, Encoding.UTF8.GetBytes(wantedPayload), ct);

        // 5. Receive PackData messages and store each object
        while (true)
        {
            var (msgType, msgPayload) = await ReadMessageAsync(ct);

            if (msgType == MsgPackData)
            {
                if (msgPayload.Length > ObjectId.HexLength)
                {
                    string idHex = Encoding.ASCII.GetString(msgPayload, 0, ObjectId.HexLength);
                    ObjectId objectId = ObjectId.Parse(idHex);
                    byte[] compressedData = new byte[msgPayload.Length - ObjectId.HexLength];
                    Buffer.BlockCopy(msgPayload, ObjectId.HexLength, compressedData, 0, compressedData.Length);

                    localStore.Write(objectId, compressedData);
                }
            }
            else if (msgType == MsgPackComplete)
            {
                // 6. All objects received
                break;
            }
            else if (msgType == MsgError)
            {
                throw new InvalidOperationException($"Remote error during pull: {Encoding.UTF8.GetString(msgPayload)}");
            }
            else
            {
                throw new InvalidOperationException($"Unexpected message type during pull: {msgType}");
            }
        }

        // 7. Update local remote-tracking refs (refs/remotes/{remoteName}/{branch})
        foreach (var (refName, objectId) in remoteRefs)
        {
            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                string branchName = refName["refs/heads/".Length..];
                string trackingRef = $"remotes/{remoteName}/{branchName}";
                localRefs.WriteRef(trackingRef, objectId);
            }
        }

        // 8. Return map of remote ref -> objectId
        return remoteRefs;
    }

    /// <summary>
    /// Clones a remote repository into a new local directory.
    /// Initializes the repo, adds the remote, pulls all objects, sets up local branches,
    /// and checks out main or master.
    /// </summary>
    public static async Task<Repository> CloneAsync(string url, string? targetDir = null, CancellationToken ct = default)
    {
        var remoteUrl = RemoteUrl.Parse(url);
        string dir = targetDir ?? remoteUrl.RepoName;
        string fullPath = Path.GetFullPath(dir);

        // 1. Init repo
        var repo = Repository.Init(fullPath);

        // 2. Add remote "origin"
        repo.Config.AddRemote("origin", url);
        repo.Config.Save();

        // 3. Pull from origin
        using var client = new RemoteClient();
        client.Connect(remoteUrl);
        Dictionary<string, ObjectId> remoteRefs = await client.PullAsync(
            repo.ObjectStore, repo.Refs, remoteUrl, "origin", ct);

        // 4. Set local branches to match remote
        //    For the default branch (main or master), create a local branch
        string? defaultBranch = null;

        // Prefer "main", then "master", then the first available branch
        if (remoteRefs.ContainsKey("refs/heads/main"))
            defaultBranch = "main";
        else if (remoteRefs.ContainsKey("refs/heads/master"))
            defaultBranch = "master";
        else
        {
            // Pick the first refs/heads/* entry
            foreach (string refName in remoteRefs.Keys)
            {
                if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    defaultBranch = refName["refs/heads/".Length..];
                    break;
                }
            }
        }

        if (defaultBranch is not null && remoteRefs.TryGetValue($"refs/heads/{defaultBranch}", out ObjectId defaultId))
        {
            // Create local branch pointing to the same commit
            repo.Refs.CreateBranch(defaultBranch, defaultId);

            // Set HEAD to point to this branch
            repo.Refs.WriteHead($"ref: refs/heads/{defaultBranch}");

            // 5. Checkout the default branch (write files to working directory)
            repo.CheckoutBranch(defaultBranch);
        }

        return repo;
    }

    /// <summary>
    /// Releases the SSH process and associated streams.
    /// </summary>
    public void Dispose()
    {
        _stdin?.Dispose();
        _stdout?.Dispose();

        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill();
                }
                catch
                {
                    // Best-effort cleanup; ignore errors during disposal
                }
            }

            _process.Dispose();
        }

        _stdin = null;
        _stdout = null;
        _process = null;
    }

    // ──────────────────────────── Object graph walk ────────────────────────────

    /// <summary>
    /// Recursively collects all reachable object IDs starting from a commit.
    /// Walks commit parents, trees, and tree entries, matching the server's
    /// CollectObjectsRecursive logic.
    /// </summary>
    private static void CollectObjectsRecursive(ObjectStore store, ObjectId id, HashSet<ObjectId> collected)
    {
        if (!collected.Add(id))
            return;

        if (!store.Exists(id))
            return;

        byte[] compressed = store.Read(id);
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
                        CollectObjectsRecursive(store, treeId, collected);
                    }
                    else if (line.StartsWith("parent ", StringComparison.Ordinal))
                    {
                        ObjectId parentId = ObjectId.Parse(line[7..]);
                        CollectObjectsRecursive(store, parentId, collected);
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
                    CollectObjectsRecursive(store, entryId, collected);
                    offset = hashStart + ObjectId.ByteLength;
                }
                break;

            case ObjectType.Blob:
                // Leaf node -- nothing to traverse
                break;
        }
    }

    // ──────────────────────────── Ref parsing ────────────────────────────

    /// <summary>
    /// Parses the RefAdvertisement payload: newline-separated entries of "refName hexHash".
    /// </summary>
    private static Dictionary<string, ObjectId> ParseRefAdvertisement(byte[] payload)
    {
        var refs = new Dictionary<string, ObjectId>(StringComparer.Ordinal);

        string text = Encoding.UTF8.GetString(payload);
        if (string.IsNullOrWhiteSpace(text))
            return refs;

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            string trimmed = line.TrimEnd('\r');
            int spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex < 0)
                continue;

            string refName = trimmed[..spaceIndex];
            string hexHash = trimmed[(spaceIndex + 1)..];

            refs[refName] = ObjectId.Parse(hexHash);
        }

        return refs;
    }

    // ──────────────────────────── Wire format ────────────────────────────

    /// <summary>
    /// Writes a single message to the output stream.
    /// Format: [4 bytes big-endian payload length][1 byte MessageType][payload bytes]
    /// </summary>
    private async Task WriteMessageAsync(byte messageType, byte[] payload, CancellationToken ct)
    {
        EnsureConnected();

        int length = payload.Length;
        byte[] header = new byte[5];
        header[0] = (byte)((length >> 24) & 0xFF);
        header[1] = (byte)((length >> 16) & 0xFF);
        header[2] = (byte)((length >> 8) & 0xFF);
        header[3] = (byte)(length & 0xFF);
        header[4] = messageType;

        await _stdin!.WriteAsync(header, ct);
        if (payload.Length > 0)
        {
            await _stdin.WriteAsync(payload, ct);
        }

        await _stdin.FlushAsync(ct);
    }

    /// <summary>
    /// Reads a single message from the input stream.
    /// Format: [4 bytes big-endian payload length][1 byte MessageType][payload bytes]
    /// </summary>
    private async Task<(byte Type, byte[] Payload)> ReadMessageAsync(CancellationToken ct)
    {
        EnsureConnected();

        byte[] lengthBuf = new byte[4];
        await ReadExactAsync(_stdout!, lengthBuf, ct);
        int length = (lengthBuf[0] << 24) | (lengthBuf[1] << 16) | (lengthBuf[2] << 8) | lengthBuf[3];

        byte[] typeBuf = new byte[1];
        await ReadExactAsync(_stdout!, typeBuf, ct);
        byte msgType = typeBuf[0];

        byte[] payload = new byte[length];
        if (length > 0)
        {
            await ReadExactAsync(_stdout!, payload, ct);
        }

        return (msgType, payload);
    }

    /// <summary>
    /// Reads exactly buffer.Length bytes from the stream, looping as needed.
    /// Throws <see cref="EndOfStreamException"/> if the stream ends prematurely.
    /// </summary>
    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream while reading from remote.");
            offset += read;
        }
    }

    /// <summary>
    /// Ensures that the SSH connection has been established.
    /// </summary>
    private void EnsureConnected()
    {
        if (_process is null || _stdin is null || _stdout is null)
            throw new InvalidOperationException("Not connected. Call Connect() first.");
    }
}
