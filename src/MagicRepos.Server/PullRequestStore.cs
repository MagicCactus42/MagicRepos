using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicRepos.Server;

public class PullRequest
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public PullRequestState State { get; set; } = PullRequestState.Open;
    public DateTime CreatedAt { get; set; }
    public DateTime? MergedAt { get; set; }
    public List<PullRequestReview> Reviews { get; set; } = new();
}

public class PullRequestReview
{
    public string Reviewer { get; set; } = "";
    public bool Approved { get; set; }
    public string Comment { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PullRequestState
{
    Open,
    Closed,
    Merged
}

/// <summary>
/// Stores pull requests as JSON files under <c>{repoPath}/pullrequests/</c>.
/// Each pull request is stored as <c>{number}.json</c>.
/// </summary>
public class PullRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _prDir;

    public PullRequestStore(string repoPath)
    {
        _prDir = Path.Combine(repoPath, "pullrequests");
    }

    /// <summary>
    /// Creates a new pull request with an atomically assigned number.
    /// </summary>
    public PullRequest Create(string title, string description, string author,
        string sourceBranch, string targetBranch)
    {
        Directory.CreateDirectory(_prDir);

        var pr = new PullRequest
        {
            Number = GetNextNumber(),
            Title = title,
            Description = description,
            Author = author,
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch,
            State = PullRequestState.Open,
            CreatedAt = DateTime.UtcNow
        };

        string json = JsonSerializer.Serialize(pr, JsonOptions);
        File.WriteAllText(GetFilePath(pr.Number), json);
        return pr;
    }

    /// <summary>
    /// Reads a pull request by its number, or returns <see langword="null"/> if not found.
    /// </summary>
    public PullRequest? Get(int number)
    {
        string path = GetFilePath(number);
        if (!File.Exists(path))
            return null;

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PullRequest>(json, JsonOptions);
    }

    /// <summary>
    /// Lists all pull requests, optionally filtered by state.
    /// </summary>
    public IReadOnlyList<PullRequest> List(PullRequestState? stateFilter = null)
    {
        if (!Directory.Exists(_prDir))
            return Array.Empty<PullRequest>();

        var results = new List<PullRequest>();

        foreach (string file in Directory.GetFiles(_prDir, "*.json"))
        {
            string json = File.ReadAllText(file);
            var pr = JsonSerializer.Deserialize<PullRequest>(json, JsonOptions);
            if (pr is null)
                continue;

            if (stateFilter is null || pr.State == stateFilter.Value)
                results.Add(pr);
        }

        return results.OrderBy(pr => pr.Number).ToList();
    }

    /// <summary>
    /// Persists changes to an existing pull request.
    /// </summary>
    public void Update(PullRequest pr)
    {
        Directory.CreateDirectory(_prDir);
        string json = JsonSerializer.Serialize(pr, JsonOptions);
        File.WriteAllText(GetFilePath(pr.Number), json);
    }

    /// <summary>
    /// Determines the next pull request number by scanning existing files.
    /// </summary>
    private int GetNextNumber()
    {
        if (!Directory.Exists(_prDir))
            return 1;

        int max = 0;
        foreach (string file in Directory.GetFiles(_prDir, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name, out int number) && number > max)
                max = number;
        }

        return max + 1;
    }

    private string GetFilePath(int number) => Path.Combine(_prDir, $"{number}.json");
}
