using Microsoft.Extensions.Logging;
using CobolToQuarkusMigration.Agents.Infrastructure;
using CobolToQuarkusMigration.Agents.Interfaces;
using CobolToQuarkusMigration.Helpers;
using CobolToQuarkusMigration.Models;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace CobolToQuarkusMigration.Agents;

/// <summary>
/// Agent that proposes safe, scoped modifications to COBOL source code.
/// Output language is always COBOL — this is not a transpilation agent.
/// Inherits dual-API fallback, reasoning-exhaustion escalation, and retry logic from AgentBase.
/// </summary>
public class ChangeProposalAgent : AgentBase, IChangeProposalAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    protected override string AgentName => "ChangeProposalAgent";

    #region Constructors (dual-API pattern)

    private ChangeProposalAgent(
        IChatClient chatClient,
        ILogger logger,
        string modelId,
        EnhancedLogger? enhancedLogger = null,
        ChatLogger? chatLogger = null,
        RateLimiter? rateLimiter = null,
        AppSettings? settings = null)
        : base(chatClient, logger, modelId, enhancedLogger, chatLogger, rateLimiter, settings)
    {
    }

    private ChangeProposalAgent(
        ResponsesApiClient responsesClient,
        ILogger logger,
        string modelId,
        EnhancedLogger? enhancedLogger = null,
        ChatLogger? chatLogger = null,
        RateLimiter? rateLimiter = null,
        AppSettings? settings = null)
        : base(responsesClient, logger, modelId, enhancedLogger, chatLogger, rateLimiter, settings)
    {
    }

    /// <summary>
    /// Factory method — routes to the correct constructor based on which API client is available.
    /// Prefers ResponsesApiClient (codex models) when provided; falls back to IChatClient.
    /// </summary>
    public static ChangeProposalAgent Create(
        ResponsesApiClient? responsesClient,
        IChatClient? chatClient,
        ILogger logger,
        string modelId,
        EnhancedLogger? enhancedLogger = null,
        ChatLogger? chatLogger = null,
        RateLimiter? rateLimiter = null,
        AppSettings? settings = null)
    {
        if (responsesClient != null)
            return new ChangeProposalAgent(responsesClient, logger, modelId, enhancedLogger, chatLogger, rateLimiter, settings);
        if (chatClient != null)
            return new ChangeProposalAgent(chatClient, logger, modelId, enhancedLogger, chatLogger, rateLimiter, settings);
        throw new ArgumentException("Either responsesClient or chatClient must be provided.");
    }

    #endregion

    /// <inheritdoc />
    public async Task<ChangeProposal> ProposeChangesAsync(
        ChangeRequest changeRequest,
        CobolFile cobolFile,
        CobolAnalysis analysis,
        DependencyMap? dependencyMap = null,
        BusinessLogic? businessLogic = null)
    {
        Logger.LogInformation("[{Agent}] Proposing {ChangeType} changes for {File}, scope: [{Scope}]",
            AgentName, changeRequest.ChangeType, cobolFile.FileName,
            string.Join(", ", changeRequest.Scope));

        // Pre-flight size check
        var estimatedTokens = TokenHelper.EstimateCobolTokens(cobolFile.Content);
        var autoChunkThreshold = Settings?.ChunkingSettings?.AutoChunkCharThreshold ?? 150_000;
        if (cobolFile.Content.Length > autoChunkThreshold)
        {
            Logger.LogWarning("[{Agent}] File {File} exceeds auto-chunk threshold ({Chars} chars > {Threshold}). " +
                "Consider using CscmChunkedProcess for large files.",
                AgentName, cobolFile.FileName, cobolFile.Content.Length, autoChunkThreshold);
        }

        var systemPrompt = PromptLoader.LoadSection("ChangeProposalAgent", "System");
        var userPrompt = BuildUserPrompt(changeRequest, cobolFile, analysis, dependencyMap, businessLogic);

        var (response, usedFallback, fallbackReason) = await ExecuteWithFallbackAsync(
            systemPrompt, userPrompt, $"CSCM:{cobolFile.FileName}");

        if (usedFallback)
        {
            Logger.LogWarning("[{Agent}] Used fallback for {File}: {Reason}",
                AgentName, cobolFile.FileName, fallbackReason);
            return CreateFallbackProposal(cobolFile, changeRequest, fallbackReason ?? "Unknown error");
        }

        return ParseProposal(response, cobolFile, changeRequest);
    }

    private string BuildUserPrompt(
        ChangeRequest changeRequest,
        CobolFile cobolFile,
        CobolAnalysis analysis,
        DependencyMap? dependencyMap,
        BusinessLogic? businessLogic)
    {
        var analysisSummary = BuildAnalysisSummary(analysis, businessLogic);
        var dependencyContext = BuildDependencyContext(dependencyMap, cobolFile.FileName);

        return PromptLoader.LoadSection("ChangeProposalAgent", "User", new Dictionary<string, string>
        {
            ["ChangeRequestType"] = changeRequest.ChangeType.ToString(),
            ["ChangeRequestScope"] = string.Join(", ", changeRequest.Scope),
            ["ChangeRequestRationale"] = changeRequest.Rationale,
            ["FileName"] = cobolFile.FileName,
            ["CobolContent"] = cobolFile.Content,
            ["AnalysisSummary"] = analysisSummary,
            ["DependencyContext"] = dependencyContext
        });
    }

    private static string BuildAnalysisSummary(CobolAnalysis analysis, BusinessLogic? businessLogic)
    {
        var parts = new List<string>
        {
            $"Program: {analysis.FileName}",
            $"Description: {analysis.ProgramDescription}"
        };

        if (analysis.Paragraphs.Count > 0)
        {
            parts.Add($"Paragraphs ({analysis.Paragraphs.Count}): " +
                string.Join(", ", analysis.Paragraphs.Select(p => p.Name)));
        }

        if (analysis.CopybooksReferenced.Count > 0)
        {
            parts.Add($"Copybooks referenced: {string.Join(", ", analysis.CopybooksReferenced)}");
        }

        if (businessLogic != null && !string.IsNullOrWhiteSpace(businessLogic.BusinessPurpose))
        {
            parts.Add($"Business purpose: {businessLogic.BusinessPurpose}");
        }

        return string.Join("\n", parts);
    }

    private static string BuildDependencyContext(DependencyMap? dependencyMap, string fileName)
    {
        if (dependencyMap == null || dependencyMap.Dependencies.Count == 0)
            return "No dependency data available.";

        var relevant = dependencyMap.Dependencies
            .Where(d => d.SourceFile.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                     || d.TargetFile.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (relevant.Count == 0)
            return "No dependencies found for this file.";

        var lines = relevant.Select(d =>
            $"  {d.SourceFile} --[{d.DependencyType}]--> {d.TargetFile}" +
            (d.LineNumber > 0 ? $" (line {d.LineNumber})" : ""));

        return $"Dependencies involving {fileName}:\n{string.Join("\n", lines)}";
    }

    private ChangeProposal ParseProposal(string response, CobolFile cobolFile, ChangeRequest changeRequest)
    {
        // Strip markdown code fences if the model wrapped the JSON
        var json = response.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0) json = json[(firstNewline + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        try
        {
            var raw = JsonSerializer.Deserialize<RawProposalResponse>(json, JsonOptions);
            if (raw == null)
                return CreateFallbackProposal(cobolFile, changeRequest, "Empty JSON response from model");

            var proposal = new ChangeProposal
            {
                SourceFile = cobolFile.FileName,
                ChangeType = changeRequest.ChangeType,
                Rationale = raw.Rationale ?? string.Empty,
                RiskLevel = Enum.TryParse<RiskLevel>(raw.RiskLevel, true, out var rl) ? rl : RiskLevel.Medium,
                ImpactedPrograms = raw.ImpactedPrograms ?? new List<string>(),
                ApprovalState = ApprovalState.Pending,
                ModelUsed = ModelId,
                AffectedParagraphs = (raw.AffectedParagraphs ?? new List<RawParagraphChange>())
                    .Select(p => new ParagraphChange
                    {
                        ParagraphName = p.ParagraphName ?? string.Empty,
                        OriginalText = p.OriginalText ?? string.Empty,
                        ProposedText = p.ProposedText ?? string.Empty,
                        Explanation = p.Explanation ?? string.Empty
                    })
                    .ToList()
            };

            // Scope enforcement: reject any paragraph not in the declared scope
            var outOfScope = proposal.AffectedParagraphs
                .Where(p => !changeRequest.Scope.Any(s =>
                    s.Equals(p.ParagraphName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (outOfScope.Count > 0)
            {
                Logger.LogWarning("[{Agent}] Rejecting {Count} out-of-scope paragraph(s): {Names}",
                    AgentName, outOfScope.Count,
                    string.Join(", ", outOfScope.Select(p => p.ParagraphName)));
                foreach (var p in outOfScope)
                    proposal.AffectedParagraphs.Remove(p);
            }

            return proposal;
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "[{Agent}] Failed to parse proposal JSON for {File}", AgentName, cobolFile.FileName);
            return CreateFallbackProposal(cobolFile, changeRequest, $"JSON parse error: {ex.Message}");
        }
    }

    private static ChangeProposal CreateFallbackProposal(CobolFile cobolFile, ChangeRequest changeRequest, string reason)
    {
        return new ChangeProposal
        {
            SourceFile = cobolFile.FileName,
            ChangeType = changeRequest.ChangeType,
            Rationale = $"Fallback: {reason}",
            RiskLevel = RiskLevel.High,
            ApprovalState = ApprovalState.Pending,
            ModelUsed = "fallback"
        };
    }

    #region JSON deserialization helpers

    private sealed class RawProposalResponse
    {
        public List<RawParagraphChange>? AffectedParagraphs { get; set; }
        public string? Rationale { get; set; }
        public string? RiskLevel { get; set; }
        public List<string>? ImpactedPrograms { get; set; }
    }

    private sealed class RawParagraphChange
    {
        public string? ParagraphName { get; set; }
        public string? OriginalText { get; set; }
        public string? ProposedText { get; set; }
        public string? Explanation { get; set; }
    }

    #endregion
}
