using FluentAssertions;
using MagicRepos.Core.Ignore;

namespace MagicRepos.Core.Tests.Ignore;

public class IgnoreRuleSetTests : IDisposable
{
    private readonly string _tempDir;

    public IgnoreRuleSetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "magicrepos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Ignores_magicrepos_directory()
    {
        // Arrange
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();

        // Act & Assert — .magicrepos is always ignored regardless of rules
        rules.IsIgnored(".magicrepos", isDirectory: true).Should().BeTrue();
        rules.IsIgnored(".magicrepos/HEAD", isDirectory: false).Should().BeTrue();
        rules.IsIgnored(".magicrepos/objects/ab/cdef", isDirectory: false).Should().BeTrue();
    }

    [Fact]
    public void Simple_file_pattern_matches()
    {
        // Arrange
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();
        rules.AddRule("*.log");

        // Act & Assert
        rules.IsIgnored("debug.log", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("src/output.log", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("file.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Wildcard_pattern_matches()
    {
        // Arrange
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();
        rules.AddRule("temp*");

        // Act & Assert
        rules.IsIgnored("tempfile", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("temporary.txt", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("src/tempdata", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("other.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Negation_overrides_ignore()
    {
        // Arrange
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();
        rules.AddRule("*.log");
        rules.AddRule("!important.log");

        // Act & Assert
        rules.IsIgnored("debug.log", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("important.log", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Directory_only_pattern()
    {
        // Arrange
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();
        rules.AddRule("build/");

        // Act & Assert — trailing slash means only match directories
        rules.IsIgnored("build", isDirectory: true).Should().BeTrue();
        rules.IsIgnored("build", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Double_star_matches_nested_paths()
    {
        // Arrange
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();
        rules.AddRule("**/logs");

        // Act & Assert
        rules.IsIgnored("logs", isDirectory: true).Should().BeTrue();
        rules.IsIgnored("src/logs", isDirectory: true).Should().BeTrue();
        rules.IsIgnored("src/deep/nested/logs", isDirectory: true).Should().BeTrue();
    }

    [Fact]
    public void Load_reads_file_and_skips_comments_and_blanks()
    {
        // Arrange
        string ignorePath = Path.Combine(_tempDir, ".magicreposignore");
        File.WriteAllText(ignorePath, "# Comment\n\n*.log\n\n# Another comment\nbuild/\n");

        // Act
        IgnoreRuleSet rules = IgnoreRuleSet.Load(ignorePath);

        // Assert
        rules.IsIgnored("test.log", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("build", isDirectory: true).Should().BeTrue();
        rules.IsIgnored("src/main.cs", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Load_returns_empty_for_nonexistent_file()
    {
        // Arrange
        string nonexistent = Path.Combine(_tempDir, "no-such-file");

        // Act
        IgnoreRuleSet rules = IgnoreRuleSet.Load(nonexistent);

        // Assert — should not throw and should have no rules (only .magicrepos hardcoded)
        rules.IsIgnored("anything.txt", isDirectory: false).Should().BeFalse();
        rules.IsIgnored(".magicrepos", isDirectory: true).Should().BeTrue();
    }

    [Fact]
    public void Anchored_pattern_only_matches_at_root()
    {
        // Arrange
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();
        rules.AddRule("/rootonly.txt");

        // Act & Assert
        rules.IsIgnored("rootonly.txt", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("sub/rootonly.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Pattern_with_slash_is_implicitly_anchored()
    {
        // Arrange
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();
        rules.AddRule("doc/internal");

        // Act & Assert
        rules.IsIgnored("doc/internal", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("doc/internal/file.txt", isDirectory: false).Should().BeTrue();
    }

    [Fact]
    public void Empty_returns_no_rules()
    {
        // Act
        IgnoreRuleSet rules = IgnoreRuleSet.Empty();

        // Assert
        rules.IsIgnored("file.txt", isDirectory: false).Should().BeFalse();
        rules.IsIgnored("dir", isDirectory: true).Should().BeFalse();
    }
}
