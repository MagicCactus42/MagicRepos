using FluentAssertions;
using MagicRepos.Core.Ignore;

namespace MagicRepos.Core.Tests.Ignore;

/// <summary>
/// Regression tests for ignore-pattern bugs found during review.
/// </summary>
public class IgnoreBugFixTests : IDisposable
{
    private readonly string _tempDir;

    public IgnoreBugFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "magicrepos-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private IgnoreRuleSet Load(params string[] lines)
    {
        string path = Path.Combine(_tempDir, ".magicreposignore");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return IgnoreRuleSet.Load(path);
    }

    [Fact]
    public void Negated_character_class_matches_non_members()
    {
        // gitignore "[!0-9]" means "not a digit".
        IgnoreRuleSet rules = Load("file[!0-9].txt");

        rules.IsIgnored("fileA.txt", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("file5.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Positive_character_class_still_works()
    {
        IgnoreRuleSet rules = Load("file[0-9].txt");

        rules.IsIgnored("file5.txt", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("fileA.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Directory_only_rule_ignores_files_inside_the_directory()
    {
        IgnoreRuleSet rules = Load("build/");

        // The directory itself and files within it are ignored...
        rules.IsIgnored("build", isDirectory: true).Should().BeTrue();
        rules.IsIgnored("build/out.o", isDirectory: false).Should().BeTrue();

        // ...but a *file* literally named "build" is not.
        rules.IsIgnored("build", isDirectory: false).Should().BeFalse();
    }
}
