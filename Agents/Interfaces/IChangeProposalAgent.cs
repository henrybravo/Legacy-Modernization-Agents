using CobolToQuarkusMigration.Models;

namespace CobolToQuarkusMigration.Agents.Interfaces;

/// <summary>
/// Interface for the COBOL Safe Code Modification agent.
/// Proposes targeted, safe COBOL code changes that remain valid COBOL.
/// </summary>
public interface IChangeProposalAgent
{
    /// <summary>
    /// Proposes safe code modifications for a COBOL file based on a change request.
    /// The proposal is scoped to the paragraphs/sections listed in the request and
    /// uses analysis and dependency data to assess risk.
    /// </summary>
    /// <param name="changeRequest">The change request describing what to modify and why.</param>
    /// <param name="cobolFile">The COBOL source file to modify.</param>
    /// <param name="analysis">The structural analysis of the COBOL file.</param>
    /// <param name="dependencyMap">Optional dependency map for ripple-effect detection.</param>
    /// <param name="businessLogic">Optional business logic context for semantic understanding.</param>
    /// <returns>A structured change proposal with per-paragraph modifications.</returns>
    Task<ChangeProposal> ProposeChangesAsync(
        ChangeRequest changeRequest,
        CobolFile cobolFile,
        CobolAnalysis analysis,
        DependencyMap? dependencyMap = null,
        BusinessLogic? businessLogic = null);
}
