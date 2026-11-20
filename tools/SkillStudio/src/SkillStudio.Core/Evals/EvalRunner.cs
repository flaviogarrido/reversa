using SkillStudio.Core.IO;
using SkillStudio.Core.Model;

namespace SkillStudio.Core.Evals;

public sealed record CatalogEntry(string Name, string Description);

public sealed record RubricGrade(int Index, bool Passed, string Reason);

/// <summary>Resultado de uma chamada ao modelo, com o consumo de tokens.</summary>
public sealed record ModelCall<T>(T Value, long InputTokens, long OutputTokens);

/// <summary>
/// As três operações que o Playground precisa do modelo. Interface para o runner ser testável
/// sem rede; a implementação real é <see cref="ClaudeEvalModel"/>.
/// </summary>
public interface IEvalModel
{
    /// <summary>Simula o agente vendo o catálogo de skills: devolve a skill escolhida ou null.</summary>
    Task<ModelCall<string?>> ChooseSkillAsync(IReadOnlyList<CatalogEntry> catalog, string prompt, CancellationToken ct);

    /// <summary>Responde ao pedido com a skill carregada (ou sem skill, quando <paramref name="skill"/> é null).</summary>
    Task<ModelCall<string>> RunAsync(SkillDraft? skill, string prompt, CancellationToken ct);

    /// <summary>Avalia a resposta contra critérios em linguagem natural.</summary>
    Task<ModelCall<IReadOnlyList<RubricGrade>>> GradeAsync(string prompt, string output, IReadOnlyList<string> criteria, CancellationToken ct);
}

/// <summary>Erro que torna inútil continuar (credencial recusada, por exemplo).</summary>
public sealed class EvalFatalException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class EvalRunOptions
{
    public int Repetitions { get; set; } = 2;
    public bool IncludeBaseline { get; set; }
    public int MaxParallel { get; set; } = 4;

    /// <summary>Só estes casos (null = todos). Os demais resultados vêm do relatório anterior, se ainda válido.</summary>
    public IReadOnlyCollection<string>? OnlyCaseIds { get; set; }

    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
}

public sealed record EvalProgress(int Done, int Total, string Message);

public sealed class EvalRunner(IEvalModel model)
{
    public event Action<EvalProgress>? Progress;

    /// <summary>Quantas chamadas ao modelo a execução fará (para mostrar antes de gastar).</summary>
    public static int EstimateCalls(SkillDraft draft, EvalSuite suite, EvalRunOptions options)
    {
        var calls = 0;
        foreach (var c in SelectCases(draft, suite, options))
        {
            if (c.Kind == EvalKind.Trigger) { calls += options.Repetitions; continue; }
            var judge = c.Assertions.Any(a => a.Kind == AssertionKind.Rubric) ? 1 : 0;
            calls += options.Repetitions * (1 + judge);
            if (options.IncludeBaseline) calls += options.Repetitions * (1 + judge);
        }
        return calls;
    }

    public static IReadOnlyList<EvalCase> SelectCases(SkillDraft draft, EvalSuite suite, EvalRunOptions options) =>
        suite.Cases
            .Where(c => options.OnlyCaseIds is null || options.OnlyCaseIds.Contains(c.Id))
            .Where(c => !string.IsNullOrWhiteSpace(c.Prompt))
            // Skill user-invoked não entra no catálogo do modelo: teste de gatilho não se aplica.
            .Where(c => c.Kind == EvalKind.Behavior || draft.Invocation == InvocationMode.Model)
            .ToList();

    /// <summary>
    /// Catálogo visto pelo modelo: as skills model-invoked do repositório (sem a versão antiga desta)
    /// mais o rascunho atual, para o teste refletir a description que está no editor.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> BuildCatalog(SkillDraft draft, IEnumerable<SkillSummary> repoSkills) =>
        repoSkills
            .Where(s => s.Invocation == InvocationMode.Model && s.LoadError is null)
            .Where(s => s.Name != draft.OriginalName && s.Name != draft.Name)
            .Select(s => new CatalogEntry(s.Name, SkillSerializer.OneLine(s.Description)))
            .Append(new CatalogEntry(draft.Name, SkillSerializer.OneLine(draft.Description)))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

    public async Task<EvalReport> RunAsync(
        SkillDraft draft,
        EvalSuite suite,
        IReadOnlyList<CatalogEntry> catalog,
        EvalRunOptions options,
        EvalReport? previous,
        CancellationToken ct)
    {
        var cases = SelectCases(draft, suite, options);
        var total = EstimateCalls(draft, suite, options);
        var done = 0;
        long input = 0, output = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxParallel));

        void Tick(string message)
        {
            var n = Interlocked.Increment(ref done);
            Progress?.Invoke(new EvalProgress(n, total, message));
        }

        void Count<T>(ModelCall<T> call)
        {
            Interlocked.Add(ref input, call.InputTokens);
            Interlocked.Add(ref output, call.OutputTokens);
        }

        async Task<RunResult> Guarded(Func<Task<RunResult>> work)
        {
            await gate.WaitAsync(ct);
            try
            {
                return await work();
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not EvalFatalException)
            {
                return new RunResult { Passed = false, Error = ex.Message };
            }
            finally
            {
                gate.Release();
            }
        }

        async Task<RunResult> Trigger(EvalCase c, int rep) => await Guarded(async () =>
        {
            var call = await model.ChooseSkillAsync(catalog, c.Prompt, ct);
            Count(call);
            Tick($"{c.Id} gatilho #{rep + 1}");
            var chosen = call.Value;
            var hit = string.Equals(chosen, draft.Name, StringComparison.Ordinal);
            var expected = c.ShouldTrigger ?? true;
            return new RunResult
            {
                ChosenSkill = chosen,
                Passed = hit == expected,
                Output = chosen is null ? "(nenhuma skill escolhida)" : $"escolheu {chosen}",
            };
        });

        async Task<RunResult> Behavior(EvalCase c, int rep, bool baseline) => await Guarded(async () =>
        {
            var call = await model.RunAsync(baseline ? null : draft, c.Prompt, ct);
            Count(call);
            var label = baseline ? "sem a skill" : "com a skill";
            Tick($"{c.Id} {label} #{rep + 1}");
            var run = new RunResult { Output = call.Value };

            foreach (var a in c.Assertions.Where(a => AssertionChecker.IsDeterministic(a.Kind) && a.Value.Length > 0))
                run.Assertions.Add(AssertionChecker.Check(a, call.Value));

            var rubric = c.Assertions.Where(a => a.Kind == AssertionKind.Rubric && a.Value.Length > 0).ToList();
            if (rubric.Count > 0)
            {
                var grades = await model.GradeAsync(c.Prompt, call.Value, rubric.Select(r => r.Value).ToList(), ct);
                Count(grades);
                Tick($"{c.Id} avaliação #{rep + 1}");
                for (var i = 0; i < rubric.Count; i++)
                {
                    var g = grades.Value.FirstOrDefault(x => x.Index == i + 1);
                    run.Assertions.Add(new AssertionResult
                    {
                        Kind = AssertionKind.Rubric,
                        Value = rubric[i].Value,
                        Passed = g?.Passed ?? false,
                        Reason = g?.Reason ?? "o juiz não avaliou este critério",
                    });
                }
            }
            run.Passed = run.Assertions.All(a => a.Passed);
            return run;
        });

        var tasks = new List<Task<CaseResult>>();
        foreach (var c in cases)
        {
            tasks.Add(Task.Run(async () =>
            {
                var result = new CaseResult { CaseId = c.Id, Title = c.Title, Kind = c.Kind, CaseHash = EvalStore.HashCase(c) };
                var reps = Enumerable.Range(0, Math.Max(1, options.Repetitions)).ToList();
                if (c.Kind == EvalKind.Trigger)
                {
                    result.Runs = [.. await Task.WhenAll(reps.Select(r => Trigger(c, r)))];
                }
                else
                {
                    var main = Task.WhenAll(reps.Select(r => Behavior(c, r, baseline: false)));
                    var basel = options.IncludeBaseline ? Task.WhenAll(reps.Select(r => Behavior(c, r, baseline: true))) : null;
                    result.Runs = [.. await main];
                    if (basel is not null) result.BaselineRuns = [.. await basel];
                }
                return result;
            }, ct));
        }

        var fresh = await Task.WhenAll(tasks);

        var report = new EvalReport
        {
            RanAt = DateTimeOffset.Now,
            Model = options.Model,
            Effort = options.Effort,
            Repetitions = Math.Max(1, options.Repetitions),
            IncludedBaseline = options.IncludeBaseline,
            SkillHash = EvalStore.HashSkill(draft),
            SuiteHash = EvalStore.HashSuite(suite),
            InputTokens = input,
            OutputTokens = output,
        };

        // Execução parcial: aproveita do relatório anterior os casos não rodados, se ele ainda vale para esta versão.
        var reusable = options.OnlyCaseIds is not null && previous is not null
                       && previous.SkillHash == report.SkillHash && previous.Repetitions == report.Repetitions
                       && previous.IncludedBaseline == options.IncludeBaseline;
        var freshById = fresh.ToDictionary(r => r.CaseId, StringComparer.Ordinal);
        foreach (var c in suite.Cases)
        {
            if (freshById.TryGetValue(c.Id, out var r)) report.Cases.Add(r);
            else if (reusable && previous!.Cases.FirstOrDefault(p => p.CaseId == c.Id && p.CaseHash == EvalStore.HashCase(c)) is { } old)
                report.Cases.Add(old);
        }
        if (options.OnlyCaseIds is not null && !reusable)
            report.Note = "Execução parcial: os demais casos precisam rodar de novo para esta versão da skill.";
        return report;
    }
}
