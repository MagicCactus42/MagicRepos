using System.Text.RegularExpressions;

namespace MagicRepos.Core.Ignore;

public class IgnoreRuleSet
{
    private readonly List<IgnoreRule> _rules = new();

    /// <summary>
    /// Loads and parses a .magicignore file into an <see cref="IgnoreRuleSet"/>.
    /// </summary>
    public static IgnoreRuleSet Load(string magicIgnorePath)
    {
        var ruleSet = new IgnoreRuleSet();

        if (!File.Exists(magicIgnorePath))
            return ruleSet;

        foreach (var rawLine in File.ReadAllLines(magicIgnorePath))
        {
            var line = rawLine.Trim();

            // Skip blank lines and comments
            if (line.Length == 0 || line[0] == '#')
                continue;

            ruleSet.AddRule(line);
        }

        return ruleSet;
    }

    /// <summary>
    /// Returns an empty rule set with no ignore rules.
    /// </summary>
    public static IgnoreRuleSet Empty() => new();

    /// <summary>
    /// Parses a single ignore pattern and adds it to the rule set.
    /// </summary>
    public void AddRule(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var rule = IgnoreRule.Parse(pattern);
        _rules.Add(rule);
    }

    /// <summary>
    /// Determines whether a given relative path should be ignored, respecting negation rules.
    /// The .magicrepos directory is always ignored.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        // Normalize to forward slashes
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        // Always ignore the .magicrepos directory itself
        if (normalized == ".magicrepos" ||
            normalized.StartsWith(".magicrepos/", StringComparison.Ordinal))
        {
            return true;
        }

        var ignored = false;

        foreach (var rule in _rules)
        {
            if (rule.Matches(normalized, isDirectory))
            {
                ignored = !rule.IsNegated;
            }
        }

        return ignored;
    }
}

internal record IgnoreRule(
    string Pattern,
    bool IsNegated,
    bool IsDirectoryOnly,
    bool IsAnchored,
    Regex CompiledRegex)
{
    /// <summary>
    /// Parses a raw ignore pattern string into an <see cref="IgnoreRule"/>.
    /// </summary>
    public static IgnoreRule Parse(string rawPattern)
    {
        var pattern = rawPattern;
        var isNegated = false;
        var isDirectoryOnly = false;
        var isAnchored = false;

        // Handle negation prefix
        if (pattern.StartsWith('!'))
        {
            isNegated = true;
            pattern = pattern[1..];
        }

        // Handle trailing slash (directory only)
        if (pattern.EndsWith('/'))
        {
            isDirectoryOnly = true;
            pattern = pattern[..^1];
        }

        // Handle leading slash (anchored to repo root)
        if (pattern.StartsWith('/'))
        {
            isAnchored = true;
            pattern = pattern[1..];
        }

        // A pattern containing a slash (other than trailing) is implicitly anchored
        if (!isAnchored && pattern.Contains('/'))
        {
            isAnchored = true;
        }

        var regex = GlobToRegex(pattern, isAnchored);

        return new IgnoreRule(pattern, isNegated, isDirectoryOnly, isAnchored, regex);
    }

    /// <summary>
    /// Tests whether the given relative path matches this rule.
    /// </summary>
    public bool Matches(string relativePath, bool isDirectory)
    {
        // If rule is directory-only but the path is not a directory, skip
        if (IsDirectoryOnly && !isDirectory)
            return false;

        return CompiledRegex.IsMatch(relativePath);
    }

    /// <summary>
    /// Converts a glob pattern to a compiled <see cref="Regex"/>.
    /// Supports *, **, and ? wildcards.
    /// </summary>
    private static Regex GlobToRegex(string pattern, bool isAnchored)
    {
        var regexParts = new List<string>();
        var i = 0;

        while (i < pattern.Length)
        {
            var c = pattern[i];

            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    // ** (double star)
                    if ((i == 0 || pattern[i - 1] == '/') &&
                        (i + 2 >= pattern.Length || pattern[i + 2] == '/'))
                    {
                        // **/ or /** or /**/  — matches zero or more path segments
                        if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                        {
                            // e.g. "**/" — matches any number of directories
                            regexParts.Add("(.+/)?");
                            i += 3; // skip ** and /
                        }
                        else
                        {
                            // trailing ** — matches everything remaining
                            regexParts.Add(".*");
                            i += 2;
                        }
                    }
                    else
                    {
                        // ** not surrounded by / — treat as two single *
                        regexParts.Add("[^/]*[^/]*");
                        i += 2;
                    }
                }
                else
                {
                    // Single * — matches anything except /
                    regexParts.Add("[^/]*");
                    i++;
                }
            }
            else if (c == '?')
            {
                // ? matches any single char except /
                regexParts.Add("[^/]");
                i++;
            }
            else if (c == '[')
            {
                // Character class — pass through until ]
                var end = pattern.IndexOf(']', i + 1);
                if (end >= 0)
                {
                    regexParts.Add(pattern[i..(end + 1)]);
                    i = end + 1;
                }
                else
                {
                    regexParts.Add(Regex.Escape(c.ToString()));
                    i++;
                }
            }
            else
            {
                regexParts.Add(Regex.Escape(c.ToString()));
                i++;
            }
        }

        var regexStr = string.Concat(regexParts);

        // If anchored, the regex must match from the start
        // If not anchored, the pattern can match against any filename component
        if (isAnchored)
        {
            regexStr = "^" + regexStr + "(/.*)?$";
        }
        else
        {
            // Match at the start or after any /
            regexStr = "(^|/)" + regexStr + "(/.*)?$";
        }

        return new Regex(regexStr, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
