namespace CobolToQuarkusMigration.Helpers;

/// <summary>
/// Generates unified diffs between original and modified text.
/// Deterministic — no LLM calls.
/// </summary>
public static class DiffGenerator
{
    /// <summary>
    /// Produces a unified diff string between two text blocks.
    /// Uses standard ---/+++ header format with @@ hunk headers.
    /// </summary>
    /// <param name="originalText">The original text.</param>
    /// <param name="modifiedText">The modified text.</param>
    /// <param name="fileName">File name for the diff header (defaults to "a/file" / "b/file").</param>
    /// <param name="contextLines">Number of unchanged context lines around each change (default 3).</param>
    /// <returns>A unified diff string, or empty string if texts are identical.</returns>
    public static string GenerateUnifiedDiff(
        string originalText,
        string modifiedText,
        string? fileName = null,
        int contextLines = 3)
    {
        if (originalText == modifiedText)
            return string.Empty;

        var originalLines = SplitLines(originalText);
        var modifiedLines = SplitLines(modifiedText);

        var hunks = ComputeHunks(originalLines, modifiedLines, contextLines);
        if (hunks.Count == 0)
            return string.Empty;

        var name = fileName ?? "file";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- a/{name}");
        sb.AppendLine($"+++ b/{name}");

        foreach (var hunk in hunks)
        {
            sb.AppendLine(hunk.Header);
            foreach (var line in hunk.Lines)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    private static string[] SplitLines(string text)
    {
        return text.Split('\n');
    }

    /// <summary>
    /// Simple LCS-based diff producing unified hunks.
    /// </summary>
    private static List<DiffHunk> ComputeHunks(string[] original, string[] modified, int contextLines)
    {
        // Build edit script using longest common subsequence
        var editScript = ComputeEditScript(original, modified);

        // Group consecutive edits into hunks with context
        var hunks = new List<DiffHunk>();
        int i = 0;
        while (i < editScript.Count)
        {
            // Skip unchanged lines
            while (i < editScript.Count && editScript[i].Type == EditType.Equal)
                i++;

            if (i >= editScript.Count) break;

            // Find start of this hunk (back up by contextLines)
            int hunkEditStart = i;
            int origStart = editScript[i].OriginalIndex;
            int modStart = editScript[i].ModifiedIndex;

            int contextBefore = Math.Min(contextLines, origStart);
            origStart -= contextBefore;
            modStart -= contextBefore;

            // Find end of hunk (advance past changes, include context between nearby changes)
            int hunkEditEnd = i;
            while (hunkEditEnd < editScript.Count)
            {
                // Advance past current change block
                while (hunkEditEnd < editScript.Count && editScript[hunkEditEnd].Type != EditType.Equal)
                    hunkEditEnd++;

                // Count consecutive unchanged lines
                int equalCount = 0;
                int tempEnd = hunkEditEnd;
                while (tempEnd < editScript.Count && editScript[tempEnd].Type == EditType.Equal)
                {
                    equalCount++;
                    tempEnd++;
                }

                // If fewer than 2*contextLines unchanged lines before next change, merge
                if (tempEnd < editScript.Count && equalCount <= contextLines * 2)
                {
                    hunkEditEnd = tempEnd;
                }
                else
                {
                    break;
                }
            }

            // Build hunk lines
            var hunkLines = new List<string>();
            int origCount = 0;
            int modCount = 0;

            // Context before
            for (int c = 0; c < contextBefore; c++)
            {
                hunkLines.Add($" {original[origStart + c]}");
                origCount++;
                modCount++;
            }

            // Edit entries
            for (int e = hunkEditStart; e < hunkEditEnd && e < editScript.Count; e++)
            {
                var entry = editScript[e];
                switch (entry.Type)
                {
                    case EditType.Equal:
                        hunkLines.Add($" {entry.Text}");
                        origCount++;
                        modCount++;
                        break;
                    case EditType.Delete:
                        hunkLines.Add($"-{entry.Text}");
                        origCount++;
                        break;
                    case EditType.Insert:
                        hunkLines.Add($"+{entry.Text}");
                        modCount++;
                        break;
                }
            }

            // Context after
            int lastOrigIndex = hunkEditEnd < editScript.Count
                ? editScript[Math.Min(hunkEditEnd, editScript.Count - 1)].OriginalIndex
                : original.Length;
            int contextAfter = Math.Min(contextLines, original.Length - lastOrigIndex);
            for (int c = 0; c < contextAfter; c++)
            {
                int idx = lastOrigIndex + c;
                if (idx < original.Length)
                {
                    hunkLines.Add($" {original[idx]}");
                    origCount++;
                    modCount++;
                }
            }

            hunks.Add(new DiffHunk
            {
                Header = $"@@ -{origStart + 1},{origCount} +{modStart + 1},{modCount} @@",
                Lines = hunkLines
            });

            i = hunkEditEnd;
        }

        return hunks;
    }

    /// <summary>
    /// Computes a simple edit script (delete/insert/equal) between two line arrays.
    /// Uses the Myers diff algorithm simplified variant.
    /// </summary>
    private static List<EditEntry> ComputeEditScript(string[] original, string[] modified)
    {
        int n = original.Length;
        int m = modified.Length;

        // Build LCS table
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                if (original[i] == modified[j])
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        // Trace back to build edit script
        var result = new List<EditEntry>();
        int oi = 0, mi = 0;
        while (oi < n || mi < m)
        {
            if (oi < n && mi < m && original[oi] == modified[mi])
            {
                result.Add(new EditEntry(EditType.Equal, original[oi], oi, mi));
                oi++;
                mi++;
            }
            else if (mi < m && (oi >= n || dp[oi, mi + 1] >= dp[oi + 1, mi]))
            {
                result.Add(new EditEntry(EditType.Insert, modified[mi], oi, mi));
                mi++;
            }
            else if (oi < n)
            {
                result.Add(new EditEntry(EditType.Delete, original[oi], oi, mi));
                oi++;
            }
        }

        return result;
    }

    private enum EditType { Equal, Delete, Insert }

    private readonly record struct EditEntry(EditType Type, string Text, int OriginalIndex, int ModifiedIndex);

    private sealed class DiffHunk
    {
        public string Header { get; set; } = string.Empty;
        public List<string> Lines { get; set; } = new();
    }
}
