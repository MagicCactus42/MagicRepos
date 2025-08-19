namespace MagicRepos.Core.Diff;

public enum DiffLineType
{
    Context,
    Added,
    Removed
}

public record DiffLine(DiffLineType Type, string Content, int? OldLineNumber, int? NewLineNumber);

public record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, List<DiffLine> Lines);

public record DiffResult(string OldPath, string NewPath, List<DiffHunk> Hunks)
{
    public bool HasChanges => Hunks.Count > 0;
}

public static class DiffEngine
{
    private const int ContextLines = 3;

    /// <summary>
    /// Computes a unified diff between two text strings using the Myers diff algorithm.
    /// Produces hunks with 3 lines of surrounding context.
    /// </summary>
    public static DiffResult Diff(string oldText, string newText, string oldPath = "a", string newPath = "b")
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var edits = MyersDiff(oldLines, newLines);
        var hunks = BuildHunks(edits, oldLines, newLines, ContextLines);

        return new DiffResult(oldPath, newPath, hunks);
    }

    /// <summary>
    /// Splits text into lines. An empty string yields an empty array.
    /// </summary>
    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        // Split preserving line endings awareness but storing content only
        return text.Split('\n')
            .Select(l => l.EndsWith('\r') ? l[..^1] : l)
            .ToArray();
    }

    /// <summary>
    /// Myers shortest edit script algorithm.
    /// Returns a list of edit operations (Equal, Insert, Delete) that transform oldLines into newLines.
    /// </summary>
    private static List<DiffEdit> MyersDiff(string[] oldLines, string[] newLines)
    {
        var n = oldLines.Length;
        var m = newLines.Length;
        var max = n + m;

        if (max == 0)
            return [];

        // V[k] stores the furthest reaching x for diagonal k
        // We use offset so negative k values map to valid indices
        var vSize = 2 * max + 1;
        var v = new int[vSize];
        var offset = max;

        // Store traces for backtracking
        var traces = new List<int[]>();

        // Forward phase: find the shortest edit script length
        var found = false;
        var sesLength = 0;

        for (var d = 0; d <= max; d++)
        {
            // Save current state for backtracking
            traces.Add((int[])v.Clone());

            for (var k = -d; k <= d; k += 2)
            {
                int x;

                // Decide whether to go down or right
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]))
                {
                    // Move down (insert)
                    x = v[k + 1 + offset];
                }
                else
                {
                    // Move right (delete)
                    x = v[k - 1 + offset] + 1;
                }

                var y = x - k;

                // Follow diagonal (equal lines)
                while (x < n && y < m && oldLines[x] == newLines[y])
                {
                    x++;
                    y++;
                }

                v[k + offset] = x;

                // Check if we reached the end
                if (x >= n && y >= m)
                {
                    found = true;
                    sesLength = d;
                    break;
                }
            }

            if (found)
                break;
        }

        // Backtrack to reconstruct the edit script
        return Backtrack(traces, oldLines, newLines, n, m, sesLength, offset);
    }

    /// <summary>
    /// Backtracks through the Myers algorithm traces to reconstruct the edit sequence.
    /// </summary>
    private static List<DiffEdit> Backtrack(
        List<int[]> traces,
        string[] oldLines,
        string[] newLines,
        int n,
        int m,
        int sesLength,
        int offset)
    {
        var edits = new List<DiffEdit>();
        var x = n;
        var y = m;

        for (var d = sesLength; d > 0; d--)
        {
            var v = traces[d];
            var k = x - y;

            int prevK;
            if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }

            var prevX = v[prevK + offset];
            var prevY = prevX - prevK;

            // Trace diagonal (equal lines) backwards
            while (x > prevX && y > prevY)
            {
                x--;
                y--;
                edits.Add(new DiffEdit(DiffEditType.Equal, x, y));
            }

            if (d > 0)
            {
                if (x == prevX)
                {
                    // Insert
                    y--;
                    edits.Add(new DiffEdit(DiffEditType.Insert, -1, y));
                }
                else
                {
                    // Delete
                    x--;
                    edits.Add(new DiffEdit(DiffEditType.Delete, x, -1));
                }
            }
        }

        // Remaining diagonal at d=0
        while (x > 0 && y > 0)
        {
            x--;
            y--;
            edits.Add(new DiffEdit(DiffEditType.Equal, x, y));
        }

        edits.Reverse();
        return edits;
    }

    /// <summary>
    /// Converts a flat list of edits into unified diff hunks with the specified number of context lines.
    /// </summary>
    private static List<DiffHunk> BuildHunks(
        List<DiffEdit> edits,
        string[] oldLines,
        string[] newLines,
        int contextLines)
    {
        var hunks = new List<DiffHunk>();

        if (edits.Count == 0)
            return hunks;

        // Find change regions (non-Equal edits)
        var changeIndices = new List<int>();
        for (var i = 0; i < edits.Count; i++)
        {
            if (edits[i].Type != DiffEditType.Equal)
                changeIndices.Add(i);
        }

        if (changeIndices.Count == 0)
            return hunks;

        // Group changes that are within (2 * contextLines) of each other into a single hunk
        var groups = new List<(int Start, int End)>();
        var groupStart = changeIndices[0];
        var groupEnd = changeIndices[0];

        for (var i = 1; i < changeIndices.Count; i++)
        {
            // If the gap between this change and the previous is small, merge them
            if (changeIndices[i] - groupEnd <= 2 * contextLines)
            {
                groupEnd = changeIndices[i];
            }
            else
            {
                groups.Add((groupStart, groupEnd));
                groupStart = changeIndices[i];
                groupEnd = changeIndices[i];
            }
        }
        groups.Add((groupStart, groupEnd));

        // Build a hunk for each group
        foreach (var (start, end) in groups)
        {
            var hunkStart = Math.Max(0, start - contextLines);
            var hunkEnd = Math.Min(edits.Count - 1, end + contextLines);

            var lines = new List<DiffLine>();
            var oldStart = int.MaxValue;
            var newStart = int.MaxValue;
            var oldCount = 0;
            var newCount = 0;

            for (var i = hunkStart; i <= hunkEnd; i++)
            {
                var edit = edits[i];

                switch (edit.Type)
                {
                    case DiffEditType.Equal:
                    {
                        var oldIdx = edit.OldIndex;
                        var newIdx = edit.NewIndex;
                        var lineNum1 = oldIdx + 1;
                        var lineNum2 = newIdx + 1;

                        if (oldIdx < oldStart) oldStart = oldIdx;
                        if (newIdx < newStart) newStart = newIdx;

                        lines.Add(new DiffLine(DiffLineType.Context, oldLines[oldIdx], lineNum1, lineNum2));
                        oldCount++;
                        newCount++;
                        break;
                    }
                    case DiffEditType.Delete:
                    {
                        var oldIdx = edit.OldIndex;
                        var lineNum1 = oldIdx + 1;

                        if (oldIdx < oldStart) oldStart = oldIdx;

                        lines.Add(new DiffLine(DiffLineType.Removed, oldLines[oldIdx], lineNum1, null));
                        oldCount++;
                        break;
                    }
                    case DiffEditType.Insert:
                    {
                        var newIdx = edit.NewIndex;
                        var lineNum2 = newIdx + 1;

                        if (newIdx < newStart) newStart = newIdx;

                        lines.Add(new DiffLine(DiffLineType.Added, newLines[newIdx], null, lineNum2));
                        newCount++;
                        break;
                    }
                }
            }

            // Convert from 0-indexed to 1-indexed for hunk header
            var hunkOldStart = oldCount > 0 ? oldStart + 1 : 0;
            var hunkNewStart = newCount > 0 ? newStart + 1 : 0;

            // If old or new side is empty but the other isn't, position after last context line
            if (oldCount == 0 && newCount > 0)
                hunkOldStart = newStart + 1;
            if (newCount == 0 && oldCount > 0)
                hunkNewStart = oldStart + 1;

            hunks.Add(new DiffHunk(hunkOldStart, oldCount, hunkNewStart, newCount, lines));
        }

        return hunks;
    }
}

internal enum DiffEditType
{
    Insert,
    Delete,
    Equal
}

internal record DiffEdit(DiffEditType Type, int OldIndex, int NewIndex);
