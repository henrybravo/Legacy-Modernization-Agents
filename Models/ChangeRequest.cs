using System.Text.Json.Serialization;

namespace CobolToQuarkusMigration.Models;

/// <summary>
/// Describes a requested code modification to a COBOL source file.
/// </summary>
public class ChangeRequest
{
    /// <summary>
    /// The type of change being requested.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ChangeType ChangeType { get; set; }

    /// <summary>
    /// List of paragraph and/or section names that are allowed to be modified.
    /// The agent must not propose modifications outside this scope.
    /// </summary>
    public List<string> Scope { get; set; } = new();

    /// <summary>
    /// Free-text rationale describing why this change is needed.
    /// </summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>
    /// The target COBOL source file name (e.g. "PAYROLL.cbl").
    /// </summary>
    public string TargetFile { get; set; } = string.Empty;
}

/// <summary>
/// Categories of safe COBOL code modifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeType
{
    BugFix,
    LogicUpdate,
    CompliancePatch,
    Performance
}
