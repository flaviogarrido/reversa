using System.Text.RegularExpressions;
using SkillStudio.Core.Model;

namespace SkillStudio.Core.Evals;

/// <summary>Asserções determinísticas (as de rubrica dependem do modelo juiz).</summary>
public static class AssertionChecker
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static bool IsDeterministic(AssertionKind kind) => kind != AssertionKind.Rubric;

    /// <summary>Null quando a expressão é válida; senão, a mensagem de erro.</summary>
    public static string? RegexError(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.Multiline, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    public static AssertionResult Check(EvalAssertion assertion, string output)
    {
        var result = new AssertionResult { Kind = assertion.Kind, Value = assertion.Value };
        switch (assertion.Kind)
        {
            case AssertionKind.Contains:
                result.Passed = output.Contains(assertion.Value, StringComparison.OrdinalIgnoreCase);
                result.Reason = result.Passed ? "texto encontrado" : "texto ausente na resposta";
                break;
            case AssertionKind.NotContains:
                result.Passed = !output.Contains(assertion.Value, StringComparison.OrdinalIgnoreCase);
                result.Reason = result.Passed ? "texto ausente, como esperado" : "texto proibido apareceu na resposta";
                break;
            case AssertionKind.Regex:
                if (RegexError(assertion.Value) is { } error)
                {
                    result.Reason = $"expressão inválida: {error}";
                    break;
                }
                try
                {
                    result.Passed = Regex.IsMatch(output, assertion.Value, RegexOptions.Multiline, RegexTimeout);
                    result.Reason = result.Passed ? "padrão encontrado" : "padrão não encontrado";
                }
                catch (RegexMatchTimeoutException)
                {
                    result.Reason = "a expressão demorou demais (backtracking)";
                }
                break;
            default:
                throw new ArgumentException("Rubrica não é determinística.", nameof(assertion));
        }
        return result;
    }
}

public enum ConfidenceLevel { None, Low, Medium, High }

/// <summary>Resumo do quanto dá para confiar na skill, a partir dos casos e da última execução.</summary>
public sealed record ConfidenceAssessment(
    ConfidenceLevel Level,
    int Score,
    string Headline,
    IReadOnlyList<string> Reasons,
    double? TriggerAccuracy,
    double? BehaviorPassRate,
    double? BaselinePassRate,
    bool IsStale)
{
    public string LevelLabel => Level switch
    {
        ConfidenceLevel.High => "Alta",
        ConfidenceLevel.Medium => "Média",
        ConfidenceLevel.Low => "Baixa",
        _ => "Sem evidência",
    };
}

public static class ConfidenceCalculator
{
    /// <summary>Mínimos para confiança alta: cobre o caminho feliz, uma variação e um limite.</summary>
    public const int MinBehaviorCases = 3;
    public const int MinTriggerCasesEachSide = 2;
    public const int MinRepetitionsForHigh = 2;

    public static ConfidenceAssessment Assess(SkillDraft draft, EvalSuite suite, EvalReport? report, int validationErrors)
    {
        var reasons = new List<string>();
        var userInvoked = draft.Invocation == InvocationMode.User;
        var behaviorCases = suite.Cases.Count(c => c.Kind == EvalKind.Behavior);
        var positives = suite.Cases.Count(c => c.Kind == EvalKind.Trigger && c.ShouldTrigger == true);
        var negatives = suite.Cases.Count(c => c.Kind == EvalKind.Trigger && c.ShouldTrigger == false);

        if (suite.Cases.Count == 0)
            return new(ConfidenceLevel.None, 0, "Nenhum caso de teste",
                ["Crie casos no Playground ou peça ao assistente para propor alguns."], null, null, null, false);

        var stale = report is not null &&
                    (report.SkillHash != EvalStore.HashSkill(draft) || report.SuiteHash != EvalStore.HashSuite(suite));
        if (report is null)
            return new(ConfidenceLevel.None, 0, $"{suite.Cases.Count} caso(s), ainda sem execução",
                ["Rode o Playground para gerar evidência."], null, null, null, false);

        var byId = suite.Cases.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var results = report.Cases.Where(r => byId.ContainsKey(r.CaseId)).ToList();
        List<CaseResult> triggerResults = userInvoked ? [] : results.Where(r => r.Kind == EvalKind.Trigger).ToList();
        var behaviorResults = results.Where(r => r.Kind == EvalKind.Behavior).ToList();

        double? triggerAcc = triggerResults.Count == 0 ? null : Rate(triggerResults);
        double? behaviorRate = behaviorResults.Count == 0 ? null : Rate(behaviorResults);
        var baselineRuns = behaviorResults.Where(r => r.BaselineRuns is { Count: > 0 }).SelectMany(r => r.BaselineRuns!).ToList();
        double? baselineRate = baselineRuns.Count == 0 ? null : (double)baselineRuns.Count(r => r.Passed) / baselineRuns.Count;

        // Pontuação: comportamento pesa mais; gatilho só conta em skill model-invoked.
        double score;
        if (userInvoked || triggerAcc is null)
            score = (behaviorRate ?? 0) * 100;
        else
            score = (behaviorRate ?? 0) * 60 + triggerAcc.Value * 40;

        var level = score switch { >= 90 => ConfidenceLevel.High, >= 70 => ConfidenceLevel.Medium, _ => ConfidenceLevel.Low };

        void Cap(ConfidenceLevel max, string reason)
        {
            reasons.Add(reason);
            if (level > max) level = max;
        }

        if (validationErrors > 0) Cap(ConfidenceLevel.Low, $"{validationErrors} erro(s) de validação no rascunho.");
        if (stale) Cap(ConfidenceLevel.Low, "A skill ou os casos mudaram depois da última execução: rode de novo.");
        if (behaviorCases < MinBehaviorCases)
            Cap(ConfidenceLevel.Medium, $"Só {behaviorCases} caso(s) de comportamento; o mínimo para confiança alta é {MinBehaviorCases}.");
        if (!userInvoked && (positives < MinTriggerCasesEachSide || negatives < MinTriggerCasesEachSide))
            Cap(ConfidenceLevel.Medium, $"Skill model-invoked precisa de {MinTriggerCasesEachSide}+ casos de gatilho positivos e {MinTriggerCasesEachSide}+ negativos (tem {positives} e {negatives}).");
        if (report.Repetitions < MinRepetitionsForHigh)
            Cap(ConfidenceLevel.Medium, $"Rodado {report.Repetitions}x por caso; use {MinRepetitionsForHigh}+ repetições para medir estabilidade.");
        if (results.Any(r => r.IsFlaky))
            Cap(ConfidenceLevel.Medium, $"{results.Count(r => r.IsFlaky)} caso(s) instáveis: passaram em parte das repetições.");
        if (results.Any(r => r.HasErrors))
            Cap(ConfidenceLevel.Low, "Houve erros de execução (API); o resultado está incompleto.");
        var missing = suite.Cases.Count(c => !report.Cases.Any(r => r.CaseId == c.Id));
        if (missing > 0 && !stale) Cap(ConfidenceLevel.Medium, $"{missing} caso(s) ainda não rodaram.");
        if (baselineRate is { } b && behaviorRate is { } s && s - b < 0.2)
            reasons.Add($"Sem a skill o modelo já passa em {b:P0} dos casos: os casos podem não estar exercitando o que a skill acrescenta.");

        var failing = results.Count(r => !r.Passed);
        var headline = failing == 0
            ? $"{results.Count} de {results.Count} casos passaram"
            : $"{failing} de {results.Count} casos falharam";
        if (stale) headline += " (desatualizado)";

        return new(level, (int)Math.Round(score), headline, reasons, triggerAcc, behaviorRate, baselineRate, stale);
    }

    private static double Rate(List<CaseResult> results) => results.Count == 0 ? 0 : (double)results.Count(r => r.Passed) / results.Count;
}
