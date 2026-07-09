using FluentAssertions;
using MagicRepos.Core.Diff;

namespace MagicRepos.Core.Tests.Diff;

/// <summary>
/// Regression tests for the unified-diff hunk-header off-by-one on zero-count sides.
/// </summary>
public class DiffHunkHeaderTests
{
    [Fact]
    public void Pure_insertion_into_empty_file_uses_zero_old_start()
    {
        DiffResult result = DiffEngine.Diff("", "a\nb");

        result.Hunks.Should().ContainSingle();
        DiffHunk hunk = result.Hunks[0];

        // Git emits "@@ -0,0 +1,2 @@": the empty side reports the line before the change.
        hunk.OldCount.Should().Be(0);
        hunk.OldStart.Should().Be(0);
        hunk.NewStart.Should().Be(1);
    }

    [Fact]
    public void Deleting_all_lines_uses_zero_new_start()
    {
        DiffResult result = DiffEngine.Diff("a\nb", "");

        result.Hunks.Should().ContainSingle();
        DiffHunk hunk = result.Hunks[0];

        // Git emits "@@ -1,2 +0,0 @@".
        hunk.NewCount.Should().Be(0);
        hunk.NewStart.Should().Be(0);
        hunk.OldStart.Should().Be(1);
    }
}
