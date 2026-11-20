namespace SkillStudio.Core.Model;

/// <summary>Visão resumida de uma skill do repositório, usada na lista e no índice de busca.</summary>
public sealed record SkillSummary(
    string Name,
    string Description,
    InvocationMode Invocation,
    string Team,
    string Phase,
    string Role,
    string Body,
    string FolderPath,
    int FileCount,
    string? LoadError = null)
{
    public bool IsUserInvoked => Invocation == InvocationMode.User;
}
