using System.Text.Json.Nodes;
using SkillStudio.Core.Assistant;
using SkillStudio.Core.Evals;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;
using SkillStudio.Core.Validation;

namespace SkillStudio.Tests;

/// <summary>Playground: runner, pontuação, persistência e regras, sem chamar a API.</summary>
public sealed class EvalTests
{
    private static SkillDraft Draft(InvocationMode mode = InvocationMode.Model) => new()
    {
        Name = "fg-teste",
        OriginalName = "fg-teste",
        Description = "Revisa o legado contra um checklist de LGPD e registra as lacunas.",
        Invocation = mode,
        Body = "Você é o auditor. Responda com uma tabela.\n",
        Files = [new SkillFile { RelativePath = "references/checklist.md", Content = "# Checklist\n" }],
    };

    private static EvalCase Behavior(string id, params EvalAssertion[] asserts) => new()
    {
        Id = id, Title = $"caso {id}", Kind = EvalKind.Behavior, Prompt = $"pedido {id}", Assertions = [.. asserts],
    };

    private static EvalCase Trigger(string id, bool should) => new()
    {
        Id = id, Title = $"gatilho {id}", Kind = EvalKind.Trigger, Prompt = $"pedido {id}", ShouldTrigger = should,
    };

    private static EvalAssertion A(AssertionKind kind, string value) => new() { Kind = kind, Value = value };

    /// <summary>Modelo falso: respostas e escolhas programáveis por pedido.</summary>
    private sealed class FakeModel : IEvalModel
    {
        public Func<string, string?> Choose { get; init; } = _ => null;
        public Func<SkillDraft?, string, string> Answer { get; init; } = (_, _) => "";
        public Func<IReadOnlyList<string>, IReadOnlyList<RubricGrade>> Grade { get; init; } =
            c => c.Select((_, i) => new RubricGrade(i + 1, true, "ok")).ToList();
        public int Calls;

        public Task<ModelCall<string?>> ChooseSkillAsync(IReadOnlyList<CatalogEntry> catalog, string prompt, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ModelCall<string?>(Choose(prompt), 10, 1));
        }

        public Task<ModelCall<string>> RunAsync(SkillDraft? skill, string prompt, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ModelCall<string>(Answer(skill, prompt), 100, 20));
        }

        public Task<ModelCall<IReadOnlyList<RubricGrade>>> GradeAsync(string prompt, string output, IReadOnlyList<string> criteria, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ModelCall<IReadOnlyList<RubricGrade>>(Grade(criteria), 50, 5));
        }
    }

    private static Task<EvalReport> Run(FakeModel model, SkillDraft draft, EvalSuite suite, EvalRunOptions? options = null, EvalReport? previous = null) =>
        new EvalRunner(model).RunAsync(draft, suite, [new("fg-teste", "x"), new("outra", "y")], options ?? new EvalRunOptions(), previous, CancellationToken.None);

    // ------------------------------------------------------------------ runner

    [Fact]
    public async Task Trigger_cases_compare_the_chosen_skill_with_the_expectation()
    {
        var suite = new EvalSuite { Skill = "fg-teste", Cases = [Trigger("c01", true), Trigger("c02", false), Trigger("c03", true)] };
        var model = new FakeModel { Choose = p => p == "pedido c03" ? "outra" : p == "pedido c01" ? "fg-teste" : null };

        var report = await Run(model, Draft(), suite);

        Assert.True(report.Cases.Single(c => c.CaseId == "c01").Passed);
        Assert.True(report.Cases.Single(c => c.CaseId == "c02").Passed);
        Assert.False(report.Cases.Single(c => c.CaseId == "c03").Passed);
        Assert.Equal("outra", report.Cases.Single(c => c.CaseId == "c03").Runs[0].ChosenSkill);
        Assert.Equal(EvalRunner.EstimateCalls(Draft(), suite, new EvalRunOptions()), model.Calls);
    }

    [Fact]
    public async Task Behavior_combines_deterministic_and_rubric_assertions()
    {
        var suite = new EvalSuite
        {
            Cases = [Behavior("c01", A(AssertionKind.Contains, "TABELA"), A(AssertionKind.NotContains, "erro"),
                A(AssertionKind.Regex, @"^\| id"), A(AssertionKind.Rubric, "cita o checklist"), A(AssertionKind.Rubric, "é curta"))],
        };
        var model = new FakeModel
        {
            Answer = (_, _) => "| id | tabela |",
            Grade = c => [new RubricGrade(1, true, "cita"), new RubricGrade(2, false, "longa demais")],
        };

        var report = await Run(model, Draft(), suite, new EvalRunOptions { Repetitions = 1 });
        var run = report.Cases.Single().Runs.Single();

        Assert.Equal(5, run.Assertions.Count);
        Assert.Equal([true, true, true, true, false], run.Assertions.Select(a => a.Passed));
        Assert.False(run.Passed);
        Assert.Equal(2, model.Calls); // resposta + um juiz para as duas rubricas
        Assert.Equal(150, report.InputTokens);
    }

    [Fact]
    public async Task Baseline_runs_without_the_skill_and_flaky_cases_are_detected()
    {
        var suite = new EvalSuite { Cases = [Behavior("c01", A(AssertionKind.Contains, "ok"))] };
        var n = 0;
        var model = new FakeModel
        {
            Answer = (skill, _) => skill is null ? "sem" : Interlocked.Increment(ref n) == 1 ? "ok" : "falhou",
        };

        var report = await Run(model, Draft(), suite, new EvalRunOptions { Repetitions = 2, IncludeBaseline = true, MaxParallel = 1 });
        var c = report.Cases.Single();

        Assert.True(c.IsFlaky);
        Assert.False(c.Passed);
        Assert.Equal(2, c.BaselineRuns!.Count);
        Assert.All(c.BaselineRuns, r => Assert.False(r.Passed));
    }

    [Fact]
    public async Task Api_errors_become_failed_runs_and_fatal_errors_stop()
    {
        var suite = new EvalSuite { Cases = [Behavior("c01", A(AssertionKind.Contains, "x"))] };
        var report = await Run(new FakeModel { Answer = (_, _) => throw new InvalidOperationException("boom") }, Draft(), suite,
            new EvalRunOptions { Repetitions = 1 });
        Assert.True(report.Cases.Single().HasErrors);
        Assert.Equal("boom", report.Cases.Single().Runs.Single().Error);

        await Assert.ThrowsAsync<EvalFatalException>(() =>
            Run(new FakeModel { Answer = (_, _) => throw new EvalFatalException("chave") }, Draft(), suite));
    }

    [Fact]
    public async Task User_invoked_skills_skip_trigger_cases()
    {
        var suite = new EvalSuite { Cases = [Trigger("c01", true), Behavior("c02", A(AssertionKind.Contains, "a"))] };
        var model = new FakeModel { Answer = (_, _) => "a" };

        var report = await Run(model, Draft(InvocationMode.User), suite, new EvalRunOptions { Repetitions = 1 });

        Assert.Equal(["c02"], report.Cases.Select(c => c.CaseId));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Partial_run_reuses_previous_results_only_for_the_same_skill()
    {
        var suite = new EvalSuite { Cases = [Behavior("c01", A(AssertionKind.Contains, "a")), Behavior("c02", A(AssertionKind.Contains, "a"))] };
        var model = new FakeModel { Answer = (_, _) => "a" };
        var full = await Run(model, Draft(), suite);

        var partial = await Run(model, Draft(), suite, new EvalRunOptions { OnlyCaseIds = ["c02"] }, full);
        Assert.Equal(["c01", "c02"], partial.Cases.Select(c => c.CaseId));
        Assert.Null(partial.Note);

        var changed = Draft().With(d => d.Body += "mais uma linha\n");
        var stale = await Run(model, changed, suite, new EvalRunOptions { OnlyCaseIds = ["c02"] }, full);
        Assert.Equal(["c02"], stale.Cases.Select(c => c.CaseId));
        Assert.NotNull(stale.Note);
    }

    [Fact]
    public void Catalog_replaces_the_saved_version_with_the_draft()
    {
        var draft = Draft().With(d => d.Description = "nova description");
        var repo = new[]
        {
            new SkillSummary("fg-teste", "antiga", InvocationMode.Model, "", "", "", "", "", 0, null),
            new SkillSummary("b-outra", "b", InvocationMode.Model, "", "", "", "", "", 0, null),
            new SkillSummary("manual", "m", InvocationMode.User, "", "", "", "", "", 0, null),
        };

        var catalog = EvalRunner.BuildCatalog(draft, repo);

        Assert.Equal(["b-outra", "fg-teste"], catalog.Select(c => c.Name));
        Assert.Equal("nova description", catalog.Single(c => c.Name == "fg-teste").Description);
    }

    // ------------------------------------------------------------------ asserções e juiz

    [Fact]
    public void Invalid_regex_fails_with_a_reason()
    {
        var r = AssertionChecker.Check(A(AssertionKind.Regex, "(abc"), "abc");
        Assert.False(r.Passed);
        Assert.StartsWith("expressão inválida", r.Reason);
    }

    [Fact]
    public void Judge_output_is_parsed_and_garbage_is_tolerated()
    {
        var grades = ClaudeEvalModel.ParseGrades("""{"grades":[{"index":1,"passed":true,"reason":"r"}]}""");
        Assert.Equal(new RubricGrade(1, true, "r"), grades.Single());
        Assert.Empty(ClaudeEvalModel.ParseGrades("não é json"));
    }

    [Fact]
    public void Skill_files_are_readable_by_the_run_tool_only_inside_the_draft()
    {
        Assert.Equal("# Checklist\n", ClaudeEvalModel.ReadSkillFile(Draft(), "references/checklist.md", out var err));
        Assert.False(err);
        ClaudeEvalModel.ReadSkillFile(Draft(), "../../segredo.txt", out err);
        Assert.True(err);
    }

    // ------------------------------------------------------------------ confiança

    private static EvalReport ReportFor(SkillDraft draft, EvalSuite suite, int reps, Func<EvalCase, bool[]> outcomes) => new()
    {
        Repetitions = reps,
        SkillHash = EvalStore.HashSkill(draft),
        SuiteHash = EvalStore.HashSuite(suite),
        Cases = suite.Cases.Select(c => new CaseResult
        {
            CaseId = c.Id, Kind = c.Kind, CaseHash = EvalStore.HashCase(c),
            Runs = outcomes(c).Select(p => new RunResult { Passed = p }).ToList(),
        }).ToList(),
    };

    private static EvalSuite FullSuite() => new()
    {
        Cases =
        [
            Behavior("c01", A(AssertionKind.Rubric, "a")), Behavior("c02", A(AssertionKind.Rubric, "b")), Behavior("c03", A(AssertionKind.Rubric, "c")),
            Trigger("c04", true), Trigger("c05", true), Trigger("c06", false), Trigger("c07", false),
        ],
    };

    [Fact]
    public void Full_passing_suite_gets_high_confidence()
    {
        var suite = FullSuite();
        var a = ConfidenceCalculator.Assess(Draft(), suite, ReportFor(Draft(), suite, 2, _ => [true, true]), 0);
        Assert.Equal(ConfidenceLevel.High, a.Level);
        Assert.Equal(100, a.Score);
        Assert.Empty(a.Reasons);
    }

    [Fact]
    public void No_cases_or_no_run_means_no_evidence()
    {
        Assert.Equal(ConfidenceLevel.None, ConfidenceCalculator.Assess(Draft(), new EvalSuite(), null, 0).Level);
        Assert.Equal(ConfidenceLevel.None, ConfidenceCalculator.Assess(Draft(), FullSuite(), null, 0).Level);
    }

    [Fact]
    public void Changing_the_skill_makes_the_result_stale_and_low()
    {
        var suite = FullSuite();
        var report = ReportFor(Draft(), suite, 2, _ => [true, true]);
        var a = ConfidenceCalculator.Assess(Draft().With(d => d.Body += "x"), suite, report, 0);
        Assert.True(a.IsStale);
        Assert.Equal(ConfidenceLevel.Low, a.Level);
    }

    [Fact]
    public void Missing_negative_triggers_single_repetition_and_flakiness_cap_at_medium()
    {
        var noNegatives = new EvalSuite { Cases = FullSuite().Cases.Where(c => c.ShouldTrigger != false).ToList() };
        Assert.Equal(ConfidenceLevel.Medium,
            ConfidenceCalculator.Assess(Draft(), noNegatives, ReportFor(Draft(), noNegatives, 2, _ => [true, true]), 0).Level);

        var suite = FullSuite();
        Assert.Equal(ConfidenceLevel.Medium, ConfidenceCalculator.Assess(Draft(), suite, ReportFor(Draft(), suite, 1, _ => [true]), 0).Level);

        // Um caso instável (1 de 2) derruba a taxa e o nível.
        var flaky = ConfidenceCalculator.Assess(Draft(), suite, ReportFor(Draft(), suite, 2, c => c.Id == "c01" ? [true, false] : [true, true]), 0);
        Assert.NotEqual(ConfidenceLevel.High, flaky.Level);
        Assert.Contains(flaky.Reasons, r => r.Contains("instáveis"));
    }

    [Fact]
    public void User_invoked_score_ignores_triggers_and_validation_errors_cap_low()
    {
        var draft = Draft(InvocationMode.User);
        var suite = new EvalSuite { Cases = FullSuite().Cases.Where(c => c.Kind == EvalKind.Behavior).ToList() };
        var report = ReportFor(draft, suite, 2, _ => [true, true]);

        Assert.Equal(ConfidenceLevel.High, ConfidenceCalculator.Assess(draft, suite, report, 0).Level);
        Assert.Equal(ConfidenceLevel.Low, ConfidenceCalculator.Assess(draft, suite, report, 1).Level);
    }

    [Fact]
    public void Baseline_close_to_the_skill_is_called_out()
    {
        var draft = Draft(InvocationMode.User);
        var suite = new EvalSuite { Cases = FullSuite().Cases.Where(c => c.Kind == EvalKind.Behavior).ToList() };
        var report = ReportFor(draft, suite, 2, _ => [true, true]);
        foreach (var c in report.Cases) c.BaselineRuns = [new RunResult { Passed = true }, new RunResult { Passed = true }];

        var a = ConfidenceCalculator.Assess(draft, suite, report, 0);
        Assert.Equal(1.0, a.BaselinePassRate);
        Assert.Contains(a.Reasons, r => r.Contains("Sem a skill"));
    }

    // ------------------------------------------------------------------ persistência

    [Fact]
    public void Store_round_trips_cases_and_deletes_an_empty_suite()
    {
        var root = Directory.CreateTempSubdirectory("skillstudio-evals-").FullName;
        try
        {
            var store = new EvalStore(root);
            var suite = new EvalSuite { Skill = "fg-teste", Cases = [Behavior("c01", A(AssertionKind.Regex, "a+")), Trigger("c02", false)] };

            Assert.True(store.SaveSuite(suite));
            Assert.False(store.SaveSuite(suite)); // nada mudou, nada gravado
            var loaded = store.LoadSuite("fg-teste");
            Assert.Equal(EvalStore.HashSuite(suite), EvalStore.HashSuite(loaded));
            Assert.Contains("\"kind\": \"Regex\"", File.ReadAllText(Path.Combine(root, "evals", "fg-teste", "cases.json")));
            Assert.Equal(["fg-teste"], store.ListSkillsWithCases());
            Assert.Equal("c03", loaded.NextId());

            store.SaveLastRun("fg-teste", new EvalReport { Repetitions = 2, Cases = [new CaseResult { CaseId = "c01", Runs = [new RunResult { Output = new string('x', 10_000) }] }] });
            var last = store.LoadLastRun("fg-teste")!;
            Assert.True(last.Cases[0].Runs[0].Output.Length < 10_000);

            store.SaveSuite(new EvalSuite { Skill = "fg-teste" });
            Assert.Empty(store.ListSkillsWithCases());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../fora")]
    [InlineData("a/b")]
    public void Store_rejects_paths_outside_evals(string skill) =>
        Assert.Throws<InvalidOperationException>(() => new EvalStore(Path.GetTempPath()).SkillDir(skill));

    [Fact]
    public void Repository_eval_suites_parse_and_point_to_existing_skills()
    {
        var root = SkillRepository.FindRoot(AppContext.BaseDirectory)!;
        var repo = new SkillRepository(root);
        var store = new EvalStore(root);
        var names = repo.ListNames().ToHashSet();
        foreach (var skill in store.ListSkillsWithCases())
        {
            Assert.Contains(skill, names);
            var suite = store.LoadSuite(skill);
            Assert.NotEmpty(suite.Cases);
            Assert.Equal(suite.Cases.Count, suite.Cases.Select(c => c.Id).Distinct().Count());
            var issues = new SkillValidator().Validate(repo.Load(skill), repo.CreateValidationContext().WithEvals(suite, null)).Issues;
            Assert.DoesNotContain(issues, i => i.Field == SkillField.Tests && i.Severity == Severity.Warning);
        }
    }

    // ------------------------------------------------------------------ regras

    private static IReadOnlyList<ValidationIssue> TestIssues(SkillDraft d, EvalSuite? suite, EvalReport? report = null) =>
        new SkillValidator().Validate(d, new ValidationContext().WithEvals(suite, report)).Issues.Where(i => i.Field == SkillField.Tests).ToList();

    [Fact]
    public void Eval_rules_are_silent_without_a_suite_and_never_errors()
    {
        Assert.DoesNotContain(new SkillValidator().Validate(Draft(), new ValidationContext()).Issues, i => i.Field == SkillField.Tests);
        Assert.Contains(TestIssues(Draft(), new EvalSuite()), i => i.RuleId == "RS100");

        var bad = new EvalSuite
        {
            Cases = [Behavior("c01"), Behavior("c02", A(AssertionKind.Regex, "(")), new EvalCase { Id = "c03", Kind = EvalKind.Behavior, Assertions = [A(AssertionKind.Contains, "x")] }],
        };
        var issues = TestIssues(Draft(), bad);
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
        foreach (var rule in new[] { "RS101", "RS102", "RS104", "RS105", "RS107" })
            Assert.Contains(issues, i => i.RuleId == rule);
    }

    [Fact]
    public void Eval_rules_flag_ignored_triggers_duplicates_and_failures()
    {
        var suite = new EvalSuite { Cases = [Trigger("c01", true), Behavior("c02", A(AssertionKind.Rubric, "a")), Behavior("c03", A(AssertionKind.Rubric, "b"))] };
        suite.Cases[2].Prompt = suite.Cases[1].Prompt;
        var user = Draft(InvocationMode.User);
        Assert.Contains(TestIssues(user, suite), i => i.RuleId == "RS103");
        Assert.Contains(TestIssues(user, suite), i => i.RuleId == "RS106");

        var failing = ReportFor(user, suite, 2, c => c.Id == "c02" ? [false, false] : [true, true]);
        Assert.Contains(TestIssues(user, suite, failing), i => i.RuleId == "RS108");
        Assert.Contains(TestIssues(user.With(d => d.Body += "x"), suite, failing), i => i.RuleId == "RS107");
    }

    // ------------------------------------------------------------------ ferramentas do assistente

    private sealed class Hosts(SkillDraft draft) : IDraftHost, IEvalHost
    {
        public EvalSuite Suite { get; } = new() { Skill = draft.Name };
        public SkillDraft GetDraft() => draft;
        public SkillDraft ApplyPatch(DraftPatch patch) => draft;
        public ValidationReport Validate(SkillDraft d) => new SkillValidator().Validate(d, new ValidationContext().WithEvals(Suite, null));
        public EvalSuite GetSuite() => Suite;

        public EvalSuite ApplyCases(IReadOnlyList<EvalCase> upserts, IReadOnlyList<string> removeIds)
        {
            Suite.Cases.RemoveAll(c => removeIds.Contains(c.Id));
            foreach (var c in upserts)
            {
                if (c.Id.Length == 0) c.Id = Suite.NextId();
                Suite.Cases.RemoveAll(x => x.Id == c.Id);
                Suite.Cases.Add(c);
            }
            return Suite;
        }
    }

    [Fact]
    public void Assistant_can_write_and_read_test_cases()
    {
        var hosts = new Hosts(Draft());
        var tools = new AssistantTools(null!, null!, hosts, hosts);

        var input = JsonNode.Parse("""
            {"cases":[
              {"title":"positivo","kind":"trigger","prompt":"audite a LGPD","should_trigger":true},
              {"title":"tabela","kind":"behavior","prompt":"liste as lacunas","assertions":[{"kind":"rubric","value":"usa tabela"}]}
            ]}
            """)!.AsObject();
        var outcome = tools.Execute(AssistantTools.UpdateTestCases, input);

        Assert.False(outcome.IsError, outcome.Content);
        Assert.Equal(2, hosts.Suite.Cases.Count);
        Assert.Contains("RS101", outcome.Content); // falta gatilho negativo
        Assert.Contains("audite a LGPD", tools.Execute(AssistantTools.GetTestCases, new JsonObject()).Content);

        var removed = tools.Execute(AssistantTools.UpdateTestCases, JsonNode.Parse("""{"remove_ids":["c01"]}""")!.AsObject());
        Assert.False(removed.IsError);
        Assert.Single(hosts.Suite.Cases);
    }

    [Theory]
    [InlineData("""{"cases":[{"kind":"trigger","prompt":"x"}]}""")]
    [InlineData("""{"cases":[{"kind":"behavior","prompt":"x"}]}""")]
    [InlineData("""{"cases":[{"kind":"outro","prompt":"x"}]}""")]
    [InlineData("""{}""")]
    public void Assistant_test_case_input_is_validated(string json)
    {
        var hosts = new Hosts(Draft());
        var outcome = new AssistantTools(null!, null!, hosts, hosts).Execute(AssistantTools.UpdateTestCases, JsonNode.Parse(json)!.AsObject());
        Assert.True(outcome.IsError);
        Assert.Empty(hosts.Suite.Cases);
    }

    [Fact]
    public void Test_case_tools_report_when_the_playground_is_unavailable()
    {
        var hosts = new Hosts(Draft());
        Assert.True(new AssistantTools(null!, null!, hosts).Execute(AssistantTools.GetTestCases, new JsonObject()).IsError);
    }
}
