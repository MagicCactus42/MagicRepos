using FluentAssertions;
using MagicRepos.Core.Diff;

namespace MagicRepos.Core.Tests.Diff;

public class DiffEngineTests
{
    [Fact]
    public void Empty_to_content_produces_all_additions()
    {
        // Arrange
        string oldText = "";
        string newText = "line1\nline2\nline3";

        // Act
        DiffResult result = DiffEngine.Diff(oldText, newText);

        // Assert
        result.HasChanges.Should().BeTrue();
        result.Hunks.Should().NotBeEmpty();

        var allLines = result.Hunks.SelectMany(h => h.Lines).ToList();
        allLines.Should().OnlyContain(l => l.Type == DiffLineType.Added);
        allLines.Should().HaveCount(3);
    }

    [Fact]
    public void Content_to_empty_produces_all_deletions()
    {
        // Arrange
        string oldText = "line1\nline2\nline3";
        string newText = "";

        // Act
        DiffResult result = DiffEngine.Diff(oldText, newText);

        // Assert
        result.HasChanges.Should().BeTrue();
        result.Hunks.Should().NotBeEmpty();

        var allLines = result.Hunks.SelectMany(h => h.Lines).ToList();
        allLines.Should().OnlyContain(l => l.Type == DiffLineType.Removed);
        allLines.Should().HaveCount(3);
    }

    [Fact]
    public void No_changes_produces_no_hunks()
    {
        // Arrange
        string text = "line1\nline2\nline3";

        // Act
        DiffResult result = DiffEngine.Diff(text, text);

        // Assert
        result.HasChanges.Should().BeFalse();
        result.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void Single_line_change_produces_correct_hunk()
    {
        // Arrange
        string oldText = "line1\nline2\nline3";
        string newText = "line1\nmodified\nline3";

        // Act
        DiffResult result = DiffEngine.Diff(oldText, newText);

        // Assert
        result.HasChanges.Should().BeTrue();
        result.Hunks.Should().HaveCount(1);

        DiffHunk hunk = result.Hunks[0];
        hunk.Lines.Should().Contain(l => l.Type == DiffLineType.Removed && l.Content == "line2");
        hunk.Lines.Should().Contain(l => l.Type == DiffLineType.Added && l.Content == "modified");
        // Should also have context lines
        hunk.Lines.Should().Contain(l => l.Type == DiffLineType.Context);
    }

    [Fact]
    public void Multiple_changes_far_apart_produce_separate_hunks()
    {
        // Arrange — create enough lines so two changes are far apart (> 2*3 context lines)
        var oldLines = new List<string>();
        for (int i = 0; i < 30; i++)
            oldLines.Add($"line{i}");

        var newLines = new List<string>(oldLines);
        newLines[2] = "CHANGED-2";   // change near the top
        newLines[27] = "CHANGED-27"; // change near the bottom

        string oldText = string.Join("\n", oldLines);
        string newText = string.Join("\n", newLines);

        // Act
        DiffResult result = DiffEngine.Diff(oldText, newText);

        // Assert
        result.HasChanges.Should().BeTrue();
        result.Hunks.Should().HaveCountGreaterThanOrEqualTo(2,
            "changes far apart should produce separate hunks");
    }

    [Fact]
    public void Both_empty_produces_no_changes()
    {
        // Act
        DiffResult result = DiffEngine.Diff("", "");

        // Assert
        result.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Added_lines_have_new_line_numbers()
    {
        // Arrange
        string oldText = "";
        string newText = "alpha\nbeta";

        // Act
        DiffResult result = DiffEngine.Diff(oldText, newText);

        // Assert
        var addedLines = result.Hunks.SelectMany(h => h.Lines)
            .Where(l => l.Type == DiffLineType.Added)
            .ToList();

        addedLines.Should().AllSatisfy(l => l.NewLineNumber.Should().NotBeNull());
    }

    [Fact]
    public void Removed_lines_have_old_line_numbers()
    {
        // Arrange
        string oldText = "alpha\nbeta";
        string newText = "";

        // Act
        DiffResult result = DiffEngine.Diff(oldText, newText);

        // Assert
        var removedLines = result.Hunks.SelectMany(h => h.Lines)
            .Where(l => l.Type == DiffLineType.Removed)
            .ToList();

        removedLines.Should().AllSatisfy(l => l.OldLineNumber.Should().NotBeNull());
    }

    [Fact]
    public void DiffResult_stores_paths()
    {
        // Act
        DiffResult result = DiffEngine.Diff("old", "new", "path/old.txt", "path/new.txt");

        // Assert
        result.OldPath.Should().Be("path/old.txt");
        result.NewPath.Should().Be("path/new.txt");
    }
}
