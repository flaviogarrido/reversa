using SkillStudio.Core.Model;

namespace SkillStudio.Core.Validation;

/// <summary>
/// Página da skill no site de documentação (docs/agentes/). Só roda quando o contexto traz a situação
/// das páginas (o editor traz) e só para skills com o prefixo do fork: as páginas do upstream têm nomes próprios.
/// Nunca bloqueia o salvamento.
/// </summary>
public sealed class DocsRules : IValidationRule
{
    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        // Só skills com o prefixo do fork: o upstream também tem skills sem "reversa-" (ex.: impeccable)
        // documentadas em outras pastas do site.
        if (ctx.Docs is not { } docs || string.IsNullOrEmpty(ctx.PreferredPrefix)
            || !docs.Skill.StartsWith(ctx.PreferredPrefix, StringComparison.Ordinal)) yield break;

        if (!docs.HasAnyPage)
        {
            yield return new("RS110", Severity.Info, SkillField.Docs,
                $"A skill não tem página no site de documentação (docs/agentes/{docs.Skill}.md).",
                "Peça ao assistente para gerar o rascunho da página nos três idiomas (botão Documentar).");
            yield break;
        }

        if (docs.MissingLanguages.Count > 0)
            yield return new("RS111", Severity.Warning, SkillField.Docs,
                $"A página do site falta em {string.Join(", ", docs.MissingLanguages)}: o site é publicado em en, pt e es.",
                "Peça ao assistente para completar as traduções.");

        if (!docs.InNav)
            yield return new("RS112", Severity.Warning, SkillField.Docs,
                "A página existe, mas não está no menu do site (nav do mkdocs.yml).",
                "O assistente registra a página no grupo Fork Agents ao gravá-la.");
    }
}
