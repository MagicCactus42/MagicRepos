namespace MagicRepos.Core.Config;

/// <summary>
/// Reads and writes a simple INI-like configuration file at <c>.magicrepos/config</c>.
/// <para>
/// Format example:
/// <code>
/// [core]
///     repositoryformatversion = 0
///     bare = false
///
/// [remote "origin"]
///     url = magicrepos@magic-repos:myuser/test-repo
///
/// [user]
///     name = John Doe
///     email = john@example.com
/// </code>
/// </para>
/// Sections are stored internally as a two-level dictionary:
/// outer key = section header (e.g. <c>"core"</c> or <c>remote "origin"</c>),
/// inner key = property name.
/// </summary>
public class RepoConfig
{
    private readonly string _configPath;

    // Outer key: section header string exactly as it appears between brackets,
    //   e.g. "core" or "remote \"origin\"".
    // Inner key: property name (case-insensitive matching on read, preserved on write).
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    // Maintain insertion order of section headers so Save() produces stable output.
    private readonly List<string> _sectionOrder = new();

    public RepoConfig(string configPath)
    {
        _configPath = configPath;
    }

    // ──────────────────────────── Load / Save ────────────────────────────

    /// <summary>
    /// Parses the config file into memory. Silently does nothing if the file does not exist.
    /// </summary>
    public void Load()
    {
        _sections.Clear();
        _sectionOrder.Clear();

        if (!File.Exists(_configPath))
            return;

        string? currentSection = null;

        foreach (string rawLine in File.ReadLines(_configPath))
        {
            string line = rawLine.Trim();

            // Skip blank lines and comments
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                continue;

            // Section header: [section] or [section "subsection"]
            if (line[0] == '[' && line[^1] == ']')
            {
                currentSection = line[1..^1].Trim();
                EnsureSection(currentSection);
                continue;
            }

            // Key = value
            if (currentSection is not null)
            {
                int eqIndex = line.IndexOf('=');
                if (eqIndex < 0)
                    continue;

                string key = line[..eqIndex].Trim();
                string value = line[(eqIndex + 1)..].Trim();
                _sections[currentSection][key] = value;
            }
        }
    }

    /// <summary>
    /// Writes the in-memory configuration back to the config file.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);

        using var writer = new StreamWriter(_configPath, append: false);

        bool first = true;
        foreach (string sectionHeader in _sectionOrder)
        {
            if (!_sections.TryGetValue(sectionHeader, out Dictionary<string, string>? kvps))
                continue;

            if (!first)
                writer.WriteLine();
            first = false;

            writer.WriteLine($"[{sectionHeader}]");
            foreach ((string key, string value) in kvps)
            {
                writer.WriteLine($"    {key} = {value}");
            }
        }
    }

    // ──────────────────────────── Get / Set (simple section) ────────────────────────────

    /// <summary>
    /// Gets a value from a plain section (e.g. <c>[core]</c>).
    /// </summary>
    public string? Get(string section, string key)
    {
        if (_sections.TryGetValue(section, out Dictionary<string, string>? kvps) &&
            kvps.TryGetValue(key, out string? value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Gets a value from a section with a subsection (e.g. <c>[remote "origin"]</c>).
    /// </summary>
    public string? Get(string section, string subsection, string key)
    {
        string header = BuildSectionHeader(section, subsection);
        if (_sections.TryGetValue(header, out Dictionary<string, string>? kvps) &&
            kvps.TryGetValue(key, out string? value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Sets a value in a plain section.
    /// </summary>
    public void Set(string section, string key, string value)
    {
        EnsureSection(section);
        _sections[section][key] = value;
    }

    /// <summary>
    /// Sets a value in a section with a subsection.
    /// </summary>
    public void Set(string section, string subsection, string key, string value)
    {
        string header = BuildSectionHeader(section, subsection);
        EnsureSection(header);
        _sections[header][key] = value;
    }

    // ──────────────────────────── Remote helpers ────────────────────────────

    /// <summary>
    /// Adds (or updates) a remote by setting <c>[remote "{name}"] url = {url}</c>.
    /// </summary>
    public void AddRemote(string name, string url)
    {
        Set("remote", name, "url", url);
    }

    /// <summary>
    /// Returns the URL for the named remote, or <see langword="null"/> if not configured.
    /// </summary>
    public string? GetRemoteUrl(string name)
    {
        return Get("remote", name, "url");
    }

    /// <summary>
    /// Lists all remote names found in the config (i.e. all subsections of <c>[remote]</c>).
    /// </summary>
    public IReadOnlyList<string> ListRemotes()
    {
        const string prefix = "remote \"";
        var remotes = new List<string>();

        foreach (string header in _sectionOrder)
        {
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                header.EndsWith('"'))
            {
                string name = header[prefix.Length..^1];
                remotes.Add(name);
            }
        }

        return remotes;
    }

    // ──────────────────────────── User config helpers ────────────────────────────

    public string? GetUserName() => Get("user", "name");
    public string? GetUserEmail() => Get("user", "email");

    // ──────────────────────────── Private helpers ────────────────────────────

    private void EnsureSection(string header)
    {
        if (_sections.ContainsKey(header))
            return;

        _sections[header] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _sectionOrder.Add(header);
    }

    /// <summary>
    /// Builds the section header string used as the dictionary key,
    /// e.g. <c>remote "origin"</c> for section=remote, subsection=origin.
    /// </summary>
    private static string BuildSectionHeader(string section, string subsection)
    {
        return $"{section} \"{subsection}\"";
    }
}
