using Microsoft.Extensions.Logging;
using CobolToQuarkusMigration.Agents;
using CobolToQuarkusMigration.Agents.Interfaces;
using CobolToQuarkusMigration.Helpers;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.Persistence;
using System.Text.Json;

namespace CobolToQuarkusMigration.Processes;

/// <summary>
/// Orchestrates the COBOL Safe Code Modification (CSCM) pipeline.
/// Pipeline: Ingest → Analyze → Dependency Graph → Change Planning → Safe Patch Generation → Review/Diff → Output.
/// </summary>
public class CscmProcess
{
    private readonly ICobolAnalyzerAgent _cobolAnalyzerAgent;
    private readonly IChangeProposalAgent _changeProposalAgent;
    private readonly IDependencyMapperAgent _dependencyMapperAgent;
    private readonly FileHelper _fileHelper;
    private readonly PatchApplier _patchApplier;
    private readonly ILogger<CscmProcess> _logger;
    private readonly EnhancedLogger _enhancedLogger;
    private readonly IMigrationRepository? _migrationRepository;
    private readonly AppSettings _settings;

    public CscmProcess(
        ICobolAnalyzerAgent cobolAnalyzerAgent,
        IChangeProposalAgent changeProposalAgent,
        IDependencyMapperAgent dependencyMapperAgent,
        FileHelper fileHelper,
        PatchApplier patchApplier,
        ILogger<CscmProcess> logger,
        EnhancedLogger enhancedLogger,
        AppSettings settings,
        IMigrationRepository? migrationRepository = null)
    {
        _cobolAnalyzerAgent = cobolAnalyzerAgent;
        _changeProposalAgent = changeProposalAgent;
        _dependencyMapperAgent = dependencyMapperAgent;
        _fileHelper = fileHelper;
        _patchApplier = patchApplier;
        _logger = logger;
        _enhancedLogger = enhancedLogger;
        _settings = settings;
        _migrationRepository = migrationRepository;
    }

    /// <summary>
    /// Runs the CSCM pipeline for a single change request.
    /// </summary>
    /// <param name="changeRequest">The change request describing what to modify.</param>
    /// <param name="cobolSourceFolder">Folder containing COBOL source files.</param>
    /// <param name="outputFolder">Folder for patched output files and diffs.</param>
    /// <param name="autoApproveLowRisk">If true, automatically approve proposals with RiskLevel = Low.</param>
    /// <param name="proposeOnly">If true, run steps 1-3 only (Analyze → Dependencies → Proposal). Skip patch application.</param>
    /// <param name="progressCallback">Optional callback for progress reporting.</param>
    /// <returns>The result of the CSCM pipeline run.</returns>
    public async Task<CscmResult> RunAsync(
        ChangeRequest changeRequest,
        string cobolSourceFolder,
        string outputFolder,
        bool autoApproveLowRisk = false,
        bool proposeOnly = false,
        Action<string, int, int>? progressCallback = null)
    {
        var result = new CscmResult { TargetFile = changeRequest.TargetFile };
        var totalSteps = proposeOnly ? 4 : 5;

        try
        {
            _enhancedLogger.ShowSectionHeader("COBOL SAFE CODE MODIFICATION", "Targeted, safe COBOL code changes");
            _logger.LogInformation("Starting CSCM for {File}, change type: {Type}",
                changeRequest.TargetFile, changeRequest.ChangeType);

            // Step 1: Find and load the target file
            _enhancedLogger.ShowStep(1, totalSteps, "File Discovery", $"Loading {changeRequest.TargetFile}");
            progressCallback?.Invoke("Loading target file", 1, totalSteps);

            var cobolFiles = await _fileHelper.ScanDirectoryForCobolFilesAsync(cobolSourceFolder);
            var targetFile = cobolFiles.FirstOrDefault(f =>
                f.FileName.Equals(changeRequest.TargetFile, StringComparison.OrdinalIgnoreCase));

            if (targetFile == null)
            {
                result.Success = false;
                result.ErrorMessage = $"Target file '{changeRequest.TargetFile}' not found in {cobolSourceFolder}";
                _enhancedLogger.ShowError(result.ErrorMessage);
                return result;
            }

            _enhancedLogger.ShowSuccess($"Loaded {targetFile.FileName} ({targetFile.Content.Length} chars)");

            // Create run
            int runId = 0;
            if (_migrationRepository != null)
            {
                runId = await _migrationRepository.StartRunAsync(cobolSourceFolder, outputFolder);
                await _migrationRepository.SaveCobolFilesAsync(runId, new[] { targetFile });
                result.RunId = runId;
            }

            // Step 2: Analyze COBOL structure
            _enhancedLogger.ShowStep(2, totalSteps, "Technical Analysis", "Analyzing COBOL code structure");
            progressCallback?.Invoke("Analyzing COBOL structure", 2, totalSteps);

            var analysis = await _cobolAnalyzerAgent.AnalyzeCobolFileAsync(targetFile);
            _enhancedLogger.ShowSuccess($"Analysis complete: {analysis.Paragraphs.Count} paragraphs, {analysis.Variables.Count} variables");

            if (_migrationRepository != null && runId > 0)
            {
                await _migrationRepository.SaveAnalysesAsync(runId, new[] { analysis });
            }

            // Validate that requested scope paragraphs exist in the analysis
            ValidateScope(changeRequest, analysis);

            // Step 3: Map dependencies
            _enhancedLogger.ShowStep(3, totalSteps, "Dependency Mapping", "Analyzing inter-program dependencies");
            progressCallback?.Invoke("Mapping dependencies", 3, totalSteps);

            DependencyMap? dependencyMap = null;
            try
            {
                dependencyMap = await _dependencyMapperAgent.AnalyzeDependenciesAsync(
                    cobolFiles, new List<CobolAnalysis> { analysis });
                _enhancedLogger.ShowSuccess($"Found {dependencyMap.Dependencies.Count} dependency relationships");

                if (_migrationRepository != null && runId > 0)
                {
                    await _migrationRepository.SaveDependencyMapAsync(runId, dependencyMap);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dependency mapping failed — proceeding without dependency context");
                _enhancedLogger.ShowWarning("Dependency mapping unavailable — proceeding without ripple-effect detection");
            }

            // Step 4: Generate change proposal
            _enhancedLogger.ShowStep(4, totalSteps, "Change Proposal", "Generating safe modification proposal");
            progressCallback?.Invoke("Generating change proposal", 4, totalSteps);

            var proposal = await _changeProposalAgent.ProposeChangesAsync(
                changeRequest, targetFile, analysis, dependencyMap);

            result.Proposal = proposal;
            _logger.LogInformation("Proposal generated: {Count} paragraph(s) affected, risk: {Risk}",
                proposal.AffectedParagraphs.Count, proposal.RiskLevel);

            if (proposal.AffectedParagraphs.Count == 0)
            {
                _enhancedLogger.ShowWarning("No changes proposed. The model returned an empty proposal.");
                _logger.LogWarning("Empty proposal — rationale: {Rationale}", proposal.Rationale);
                result.Success = true;
                result.Message = $"No changes proposed. Rationale: {proposal.Rationale}";

                if (_migrationRepository != null && runId > 0)
                    await _migrationRepository.CompleteRunAsync(runId, "Completed", "No changes proposed");

                return result;
            }

            // Generate diffs for each paragraph change
            foreach (var change in proposal.AffectedParagraphs)
            {
                var diff = DiffGenerator.GenerateUnifiedDiff(
                    change.OriginalText, change.ProposedText, $"{targetFile.FileName}:{change.ParagraphName}");
                result.Diffs.Add(new CscmDiffEntry
                {
                    ParagraphName = change.ParagraphName,
                    OriginalText = change.OriginalText,
                    ProposedText = change.ProposedText,
                    UnifiedDiff = diff
                });
            }

            _enhancedLogger.ShowSuccess($"Generated {result.Diffs.Count} diff(s), risk level: {proposal.RiskLevel}");

            // Auto-approve low-risk proposals if configured
            if (autoApproveLowRisk && proposal.RiskLevel == RiskLevel.Low)
            {
                proposal.ApprovalState = ApprovalState.Approved;
                _logger.LogInformation("Auto-approved low-risk proposal for {File}", targetFile.FileName);
            }

            // Persist proposal
            if (_migrationRepository != null && runId > 0)
            {
                await PersistProposalAsync(runId, proposal, result.Diffs);
            }

            // Step 5: Apply patch (only if approved and not propose-only mode)
            if (proposeOnly)
            {
                result.PatchApplied = false;
                result.Message = $"Propose-only mode: {proposal.AffectedParagraphs.Count} change(s) proposed (risk: {proposal.RiskLevel}). " +
                    "Diffs persisted. Run without --propose-only to apply.";
                _enhancedLogger.ShowSuccess(result.Message);
            }
            else
            {
            _enhancedLogger.ShowStep(5, totalSteps, "Patch Application", "Applying approved changes");
            progressCallback?.Invoke("Applying changes", 5, totalSteps);

            if (proposal.ApprovalState == ApprovalState.Approved)
            {
                var patchResult = _patchApplier.Apply(
                    targetFile.Content, proposal, changeRequest.Scope);

                if (patchResult.Success)
                {
                    // Write patched file
                    Directory.CreateDirectory(outputFolder);
                    var outputPath = Path.Combine(outputFolder, targetFile.FileName);
                    await File.WriteAllTextAsync(outputPath, patchResult.PatchedContent);

                    // Write diff file alongside
                    var diffPath = Path.Combine(outputFolder, $"{Path.GetFileNameWithoutExtension(targetFile.FileName)}.diff");
                    var allDiffs = string.Join("\n", result.Diffs.Select(d => d.UnifiedDiff));
                    await File.WriteAllTextAsync(diffPath, allDiffs);

                    result.PatchedFilePath = outputPath;
                    result.DiffFilePath = diffPath;
                    _enhancedLogger.ShowSuccess($"Patched file written: {outputPath}");
                    _enhancedLogger.ShowSuccess($"Diff written: {diffPath}");
                }
                else
                {
                    _enhancedLogger.ShowWarning($"Patch application: {patchResult.Message}");
                }

                result.PatchApplied = patchResult.Success;
                result.Message = patchResult.Message;
            }
            else
            {
                result.PatchApplied = false;
                result.Message = $"Proposal pending review (risk: {proposal.RiskLevel}). " +
                    "Diffs have been persisted — approve via portal or CLI to apply.";
                _enhancedLogger.ShowWarning(result.Message);
            }
            } // end of !proposeOnly block

            if (_migrationRepository != null && runId > 0)
            {
                await _migrationRepository.CompleteRunAsync(runId, "Completed",
                    $"CSCM {changeRequest.ChangeType}: {proposal.AffectedParagraphs.Count} change(s), " +
                    $"risk={proposal.RiskLevel}, applied={result.PatchApplied}");
            }

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during CSCM process for {File}", changeRequest.TargetFile);
            _enhancedLogger.ShowError($"CSCM failed: {ex.Message}");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            throw;
        }
    }

    private void ValidateScope(ChangeRequest changeRequest, CobolAnalysis analysis)
    {
        var knownParagraphs = analysis.Paragraphs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownScopes = changeRequest.Scope
            .Where(s => !knownParagraphs.Contains(s))
            .ToList();

        if (unknownScopes.Count > 0)
        {
            _logger.LogWarning("Scope contains paragraph(s) not found in analysis: {Unknown}. " +
                "The agent may not be able to locate them.",
                string.Join(", ", unknownScopes));
            _enhancedLogger.ShowWarning($"Warning: scope paragraph(s) not found in analysis: {string.Join(", ", unknownScopes)}");
        }
    }

    private async Task PersistProposalAsync(int runId, ChangeProposal proposal, List<CscmDiffEntry> diffs)
    {
        if (_migrationRepository is not HybridMigrationRepository hybridRepo)
            return;

        try
        {
            // Use the SQLite repository's connection to persist CSCM-specific data
            var sqliteRepo = GetSqliteRepository(hybridRepo);
            if (sqliteRepo == null) return;

            await using var connection = sqliteRepo.CreateConnection();
            await connection.OpenAsync();

            // Insert proposal
            await using var proposalCmd = connection.CreateCommand();
            proposalCmd.CommandText = @"
INSERT INTO cscm_proposals (run_id, source_file, change_type, requested_scope, rationale, 
    affected_paragraphs, risk_level, approval_state, model_used)
VALUES (@runId, @sourceFile, @changeType, @scope, @rationale, 
    @paragraphs, @riskLevel, @approvalState, @modelUsed)
RETURNING id";
            proposalCmd.Parameters.AddWithValue("@runId", runId);
            proposalCmd.Parameters.AddWithValue("@sourceFile", proposal.SourceFile);
            proposalCmd.Parameters.AddWithValue("@changeType", proposal.ChangeType.ToString());
            proposalCmd.Parameters.AddWithValue("@scope", JsonSerializer.Serialize(
                proposal.AffectedParagraphs.Select(p => p.ParagraphName).ToList()));
            proposalCmd.Parameters.AddWithValue("@rationale", proposal.Rationale);
            proposalCmd.Parameters.AddWithValue("@paragraphs", JsonSerializer.Serialize(
                proposal.AffectedParagraphs.Select(p => p.ParagraphName).ToList()));
            proposalCmd.Parameters.AddWithValue("@riskLevel", proposal.RiskLevel.ToString());
            proposalCmd.Parameters.AddWithValue("@approvalState", proposal.ApprovalState.ToString());
            proposalCmd.Parameters.AddWithValue("@modelUsed", proposal.ModelUsed);

            var proposalId = Convert.ToInt64(await proposalCmd.ExecuteScalarAsync());

            // Insert diffs
            foreach (var diff in diffs)
            {
                await using var diffCmd = connection.CreateCommand();
                diffCmd.CommandText = @"
INSERT INTO cscm_diffs (proposal_id, paragraph_name, original_text, proposed_text, unified_diff, revert_snapshot)
VALUES (@proposalId, @paragraph, @original, @proposed, @diff, @snapshot)";
                diffCmd.Parameters.AddWithValue("@proposalId", proposalId);
                diffCmd.Parameters.AddWithValue("@paragraph", diff.ParagraphName);
                diffCmd.Parameters.AddWithValue("@original", diff.OriginalText);
                diffCmd.Parameters.AddWithValue("@proposed", diff.ProposedText);
                diffCmd.Parameters.AddWithValue("@diff", diff.UnifiedDiff);
                diffCmd.Parameters.AddWithValue("@snapshot", diff.OriginalText); // revert snapshot = original
            }

            _logger.LogInformation("Persisted CSCM proposal {ProposalId} with {DiffCount} diffs for run {RunId}",
                proposalId, diffs.Count, runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist CSCM proposal — pipeline continues");
        }
    }

    /// <summary>
    /// Extracts the SqliteMigrationRepository from the HybridMigrationRepository via reflection
    /// since it's an internal field. Returns null if not accessible.
    /// </summary>
    private static SqliteMigrationRepository? GetSqliteRepository(HybridMigrationRepository hybrid)
    {
        var field = typeof(HybridMigrationRepository)
            .GetField("_sqliteRepo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(hybrid) as SqliteMigrationRepository;
    }
}

/// <summary>
/// Result of a CSCM pipeline run.
/// </summary>
public class CscmResult
{
    public string TargetFile { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Message { get; set; }
    public int RunId { get; set; }
    public ChangeProposal? Proposal { get; set; }
    public List<CscmDiffEntry> Diffs { get; set; } = new();
    public bool PatchApplied { get; set; }
    public string? PatchedFilePath { get; set; }
    public string? DiffFilePath { get; set; }
}

/// <summary>
/// A diff entry for a single paragraph change.
/// </summary>
public class CscmDiffEntry
{
    public string ParagraphName { get; set; } = string.Empty;
    public string OriginalText { get; set; } = string.Empty;
    public string ProposedText { get; set; } = string.Empty;
    public string UnifiedDiff { get; set; } = string.Empty;
}
