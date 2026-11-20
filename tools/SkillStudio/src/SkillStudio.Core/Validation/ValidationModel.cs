using SkillStudio.Core.Model;

namespace SkillStudio.Core.Validation;

public enum Severity
{
    /// <summary>Bloqueia o salvamento: a skill quebraria o CI, o instalador ou a engine.</summary>
    Error = 0,

    /// <summary>Não bloqueia, mas indica risco real ou desvio das convenções do Reversa.</summary>
    Warning = 1,

    /// <summary>Sugestão de qualidade.</summary>
    Info = 2,
}

/// <summary>Campo do editor ao qual o problema se refere (a UI usa para focar a aba certa).</summary>
public enum SkillField
{
    Name, Description, Invocation, DisplayName, ShortDescription, License, Compatibility,
    Metadata, Body, Files, Package, Tests,
}

public sealed record ValidationIssue(
    string RuleId,
    Severity Severity,
    SkillField Field,
    string Message,
    string? Suggestion = null,
    string? FilePath = null)
{
    public override string ToString() =>
        $"[{RuleId}] {Severity}: {Message}" + (Suggestion is null ? "" : $" → {Suggestion}");
}

/// <summary>Contexto do repositório no momento da validação.</summary>
public sealed class ValidationContext
{
    /// <summary>Nomes de todas as pastas em agents/ (inclui a própria skill quando ela já existe).</summary>
    public IReadOnlySet<string> ExistingSkillNames { get; init; } = new HashSet<string>();

    /// <summary>Prefixo recomendado para skills do fork (ex.: "fg-"). Vazio desliga a regra.</summary>
    public string PreferredPrefix { get; init; } = "";

    /// <summary>
    /// Caminhos relativos (ex.: references/x.md) de arquivos de TODAS as skills do repositório.
    /// Permite distinguir referência quebrada de referência a arquivo de outra skill.
    /// </summary>
    public IReadOnlySet<string> RepoSkillFilePaths { get; init; } = new HashSet<string>();

    /// <summary>Prefixo do upstream que skills customizadas devem evitar.</summary>
    public string UpstreamPrefix { get; init; } = "reversa";

    /// <summary>Casos de teste da skill (Playground). Nulo desliga as regras RS1xx.</summary>
    public Evals.EvalSuite? Evals { get; init; }

    /// <summary>Última execução do Playground, para avisar de falhas e resultados desatualizados.</summary>
    public Evals.EvalReport? LastEvalRun { get; init; }

    public ValidationContext WithEvals(Evals.EvalSuite? suite, Evals.EvalReport? lastRun) => new()
    {
        ExistingSkillNames = ExistingSkillNames,
        PreferredPrefix = PreferredPrefix,
        RepoSkillFilePaths = RepoSkillFilePaths,
        UpstreamPrefix = UpstreamPrefix,
        Evals = suite,
        LastEvalRun = lastRun,
    };

    public static ValidationContext Empty { get; } = new();
}

public interface IValidationRule
{
    IEnumerable<ValidationIssue> Validate(SkillDraft draft, ValidationContext context);
}

public sealed class ValidationReport(IReadOnlyList<ValidationIssue> issues)
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
    public int ErrorCount => Issues.Count(i => i.Severity == Severity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == Severity.Warning);
    public int InfoCount => Issues.Count(i => i.Severity == Severity.Info);
    public bool CanSave => ErrorCount == 0;
}
