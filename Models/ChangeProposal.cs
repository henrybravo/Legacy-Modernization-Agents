using System.Text.Json.Serialization;

namespace CobolToQuarkusMigration.Models;

/// <summary>
/// The structured output of ChangeProposalAgent: a set of scoped, paragraph-level
/// COBOL modifications with rationale, risk assessment, and approval tracking.
/// </summary>
public class ChangeProposal
{
    /// <summary>
    /// The source file this proposal applies to.
    /// </summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>
    /// The type of change that was requested.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ChangeType ChangeType { get; set; }

    /// <summary>
    /// Per-paragraph modification entries.
    /// </summary>
    public List<ParagraphChange> AffectedParagraphs { get; set; } = new();

    /// <summary>
    /// Free-text rationale produced by the agent explaining the overall change.
    /// </summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>
    /// Risk level assigned by the agent after considering dependency impact.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RiskLevel RiskLevel { get; set; } = RiskLevel.Medium;

    /// <summary>
    /// Programs identified as potentially impacted by this change (from dependency graph).
    /// </summary>
    public List<string> ImpactedPrograms { get; set; } = new();

    /// <summary>
    /// Current approval state.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ApprovalState ApprovalState { get; set; } = ApprovalState.Pending;

    /// <summary>
    /// The model that generated this proposal.
    /// </summary>
    public string ModelUsed { get; set; } = string.Empty;

    /// <summary>
    /// When the proposal was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single paragraph-level modification within a ChangeProposal.
/// </summary>
public class ParagraphChange
{
    /// <summary>
    /// The name of the COBOL paragraph or section being modified.
    /// </summary>
    public string ParagraphName { get; set; } = string.Empty;

    /// <summary>
    /// The original COBOL text of this paragraph.
    /// </summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>
    /// The proposed replacement COBOL text.
    /// </summary>
    public string ProposedText { get; set; } = string.Empty;

    /// <summary>
    /// Explanation of what changed in this paragraph and why.
    /// </summary>
    public string Explanation { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RiskLevel
{
    Low,
    Medium,
    High
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalState
{
    Pending,
    Approved,
    Rejected
}
