using SkillStudio.Core.Evals;
using SkillStudio.Core.Model;

namespace SkillStudio.Core.Validation;

/// <summary>
/// Regras sobre os casos de teste do Playground. Só rodam quando o contexto traz a suíte
/// (o editor traz; a validação de CI das skills existentes não), e nunca bloqueiam o salvamento:
/// testes aumentam a confiança, não são pré-requisito do instalador.
/// </summary>
public sealed class EvalRules : IValidationRule
{
    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        if (ctx.Evals is not { } suite) yield break;

        if (suite.Cases.Count == 0)
        {
            yield return new("RS100", Severity.Info, SkillField.Tests, "A skill não tem casos de teste.",
                "Abra o Playground e crie casos, ou peça ao assistente para propor alguns.");
            yield break;
        }

        var userInvoked = d.Invocation == InvocationMode.User;
        var behavior = suite.Cases.Where(c => c.Kind == EvalKind.Behavior).ToList();
        var triggers = suite.Cases.Where(c => c.Kind == EvalKind.Trigger).ToList();

        if (!userInvoked)
        {
            var pos = triggers.Count(c => c.ShouldTrigger == true);
            var neg = triggers.Count(c => c.ShouldTrigger == false);
            if (pos == 0 || neg == 0)
                yield return new("RS101", Severity.Warning, SkillField.Tests,
                    $"Skill model-invoked precisa de casos de gatilho positivos e negativos (tem {pos} e {neg}).",
                    "Os negativos são pedidos parecidos que NÃO devem acionar a skill: são eles que pegam descriptions amplas demais.");
        }
        else if (triggers.Count > 0)
        {
            yield return new("RS103", Severity.Warning, SkillField.Tests,
                $"{triggers.Count} caso(s) de gatilho serão ignorados: skill user-invoked não entra no catálogo do modelo.",
                "Converta em casos de comportamento ou remova.");
        }

        foreach (var c in behavior.Where(c => c.Assertions.All(a => string.IsNullOrWhiteSpace(a.Value))))
            yield return new("RS102", Severity.Warning, SkillField.Tests,
                $"O caso \"{Label(c)}\" não tem asserções: ele só prova que a skill responde, não que responde certo.",
                "Adicione ao menos um critério de rubrica ou uma checagem de texto.");

        foreach (var c in suite.Cases)
            foreach (var a in c.Assertions.Where(a => a.Kind == AssertionKind.Regex))
                if (AssertionChecker.RegexError(a.Value) is { } err)
                    yield return new("RS104", Severity.Warning, SkillField.Tests, $"Expressão regular inválida no caso \"{Label(c)}\": {err}");

        foreach (var c in suite.Cases.Where(c => string.IsNullOrWhiteSpace(c.Prompt)))
            yield return new("RS105", Severity.Warning, SkillField.Tests, $"O caso \"{Label(c)}\" está sem pedido e não será executado.");

        foreach (var g in suite.Cases.GroupBy(c => c.Prompt.Trim()).Where(g => g.Key.Length > 0 && g.Count() > 1))
            yield return new("RS106", Severity.Info, SkillField.Tests,
                $"{g.Count()} casos com o mesmo pedido ({string.Join(", ", g.Select(Label))}).",
                "Varie a formulação: casos repetidos inflam a taxa de acerto sem cobrir nada novo.");

        if (ctx.LastEvalRun is { } report)
        {
            var assessment = ConfidenceCalculator.Assess(d, suite, report, validationErrors: 0);
            var failing = report.Cases.Count(r => !r.Passed && suite.Cases.Any(c => c.Id == r.CaseId));
            if (assessment.IsStale)
                yield return new("RS107", Severity.Info, SkillField.Tests,
                    "A skill ou os casos mudaram desde a última execução do Playground.", "Rode os testes de novo antes do commit.");
            else if (failing > 0)
                yield return new("RS108", Severity.Warning, SkillField.Tests,
                    $"{failing} caso(s) falharam na última execução do Playground.", "Abra o Playground para ver o motivo de cada falha.");
        }
        else
        {
            yield return new("RS107", Severity.Info, SkillField.Tests,
                "Os casos de teste ainda não foram executados.", "Rode o Playground para gerar a evidência de confiança.");
        }
    }

    private static string Label(EvalCase c) => string.IsNullOrWhiteSpace(c.Title) ? c.Id : c.Title;
}
