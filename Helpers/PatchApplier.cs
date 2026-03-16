using CobolToQuarkusMigration.Models;
using Microsoft.Extensions.Logging;

namespace CobolToQuarkusMigration.Helpers;

/// <summary>
/// Deterministic (non-AI) component that applies accepted ChangeProposal entries
/// to the original COBOL source text at the paragraph/section boundary level.
/// </summary>
public class PatchApplier
{
    private readonly ILogger<PatchApplier> _logger;

    public PatchApplier(ILogger<PatchApplier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Applies approved paragraph-level changes from a proposal to the original COBOL source.
    /// Only changes whose paragraph names are in the declared scope are applied.
    /// </summary>
    /// <param name="originalContent">The original COBOL file content.</param>
    /// <param name="proposal">The change proposal (must have ApprovalState = Approved).</param>
    /// <param name="allowedScope">The declared scope from the ChangeRequest. Only matching paragraphs are applied.</param>
    /// <returns>The patched COBOL content, or the original content if no changes were applied.</returns>
    public PatchResult Apply(string originalContent, ChangeProposal proposal, IReadOnlyList<string> allowedScope)
    {
        if (proposal.ApprovalState != ApprovalState.Approved)
        {
            _logger.LogWarning("[PatchApplier] Proposal for {File} is not approved (state: {State}). Skipping.",
                proposal.SourceFile, proposal.ApprovalState);
            return new PatchResult
            {
                PatchedContent = originalContent,
                AppliedCount = 0,
                SkippedCount = proposal.AffectedParagraphs.Count,
                Success = false,
                Message = $"Proposal not approved (state: {proposal.ApprovalState})"
            };
        }

        var patched = originalContent;
        int applied = 0;
        int skipped = 0;
        var messages = new List<string>();

        foreach (var change in proposal.AffectedParagraphs)
        {
            // Scope enforcement: only apply if paragraph is in the allowed list
            var inScope = allowedScope.Any(s =>
                s.Equals(change.ParagraphName, StringComparison.OrdinalIgnoreCase));

            if (!inScope)
            {
                _logger.LogWarning("[PatchApplier] Skipping out-of-scope paragraph: {Paragraph}", change.ParagraphName);
                messages.Add($"Skipped out-of-scope: {change.ParagraphName}");
                skipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(change.OriginalText) || string.IsNullOrWhiteSpace(change.ProposedText))
            {
                _logger.LogWarning("[PatchApplier] Skipping paragraph {Paragraph}: empty original or proposed text",
                    change.ParagraphName);
                messages.Add($"Skipped empty text: {change.ParagraphName}");
                skipped++;
                continue;
            }

            // Normalise line endings for matching
            var normalizedOriginal = NormalizeLineEndings(change.OriginalText);
            var normalizedPatched = NormalizeLineEndings(patched);

            var index = normalizedPatched.IndexOf(normalizedOriginal, StringComparison.Ordinal);
            if (index < 0)
            {
                // Try a trimmed match as fallback
                index = normalizedPatched.IndexOf(normalizedOriginal.Trim(), StringComparison.Ordinal);
            }

            if (index >= 0)
            {
                patched = NormalizeLineEndings(patched);
                patched = patched.Remove(index, normalizedOriginal.Length);
                patched = patched.Insert(index, NormalizeLineEndings(change.ProposedText));
                applied++;
                _logger.LogInformation("[PatchApplier] Applied change to paragraph: {Paragraph}", change.ParagraphName);
            }
            else
            {
                _logger.LogWarning("[PatchApplier] Could not locate original text for paragraph: {Paragraph}",
                    change.ParagraphName);
                messages.Add($"Original text not found: {change.ParagraphName}");
                skipped++;
            }
        }

        return new PatchResult
        {
            PatchedContent = patched,
            AppliedCount = applied,
            SkippedCount = skipped,
            Success = applied > 0,
            Message = messages.Count > 0 ? string.Join("; ", messages) : $"Applied {applied} change(s)"
        };
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}

/// <summary>
/// Result of applying a patch to COBOL source.
/// </summary>
public class PatchResult
{
    public string PatchedContent { get; set; } = string.Empty;
    public int AppliedCount { get; set; }
    public int SkippedCount { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
