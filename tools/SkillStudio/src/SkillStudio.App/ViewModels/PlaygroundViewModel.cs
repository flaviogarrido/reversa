using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkillStudio.Core.Assistant;
using SkillStudio.Core.Evals;
using SkillStudio.Core.Model;
using Wpf.Ui.Controls;

namespace SkillStudio.App.ViewModels;

public enum CaseStatus { NotRun, Passed, Failed, Flaky, Stale, Error }

public sealed partial class AssertionViewModel : ObservableObject
{
    public static IReadOnlyList<AssertionKind> KindOptions { get; } =
        [AssertionKind.Rubric, AssertionKind.Contains, AssertionKind.NotContains, AssertionKind.Regex];

    [ObservableProperty] private AssertionKind _kind = AssertionKind.Rubric;
    [ObservableProperty] private string _value = "";

    public EvalAssertion ToModel() => new() { Kind = Kind, Value = Value };
    public static AssertionViewModel From(EvalAssertion a) => new() { Kind = a.Kind, Value = a.Value };
}

public sealed class RunViewModel(int number, RunResult run, bool baseline)
{
    public string Header => $"{(baseline ? "Sem a skill" : "Com a skill")} · execução {number}";
    public bool Passed => run.Passed;
    public bool IsBaseline => baseline;
    public string Verdict => run.Error is not null ? "erro" : run.Passed ? "passou" : "falhou";
    public SymbolRegular Symbol => run.Error is not null ? SymbolRegular.ErrorCircle24 : run.Passed ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.DismissCircle24;
    public string Output => run.Error is not null ? $"Erro: {run.Error}" : run.Output;
    public string Checks => string.Join("\n", run.Assertions.Select(a =>
        $"{(a.Passed ? "✓" : "✗")} [{PlaygroundViewModel.KindLabel(a.Kind)}] {a.Value}\n    {a.Reason}"));
    public bool HasChecks => run.Assertions.Count > 0;
}

public sealed partial class EvalCaseViewModel : ObservableObject
{
    public event Action? Changed;

    public static IReadOnlyList<EvalKind> KindOptions { get; } = [EvalKind.Behavior, EvalKind.Trigger];

    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private EvalKind _kind = EvalKind.Behavior;
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private bool _shouldTrigger = true;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private CaseStatus _status = CaseStatus.NotRun;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private CaseResult? _result;

    public ObservableCollection<AssertionViewModel> Assertions { get; } = [];
    public ObservableCollection<RunViewModel> Runs { get; } = [];

    public EvalCaseViewModel()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Status) or nameof(ResultSummary) or nameof(Result)) return;
            Changed?.Invoke();
        };
        Assertions.CollectionChanged += (_, _) => Changed?.Invoke();
    }

    public bool IsTrigger => Kind == EvalKind.Trigger;
    public bool IsBehavior => Kind == EvalKind.Behavior;
    public string KindLabel => IsTrigger ? (ShouldTrigger ? "gatilho +" : "gatilho −") : "comportamento";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? TextPreview(Prompt) : Title;

    public SymbolRegular StatusSymbol => Status switch
    {
        CaseStatus.Passed => SymbolRegular.CheckmarkCircle24,
        CaseStatus.Failed => SymbolRegular.DismissCircle24,
        CaseStatus.Flaky => SymbolRegular.ArrowSwap24,
        CaseStatus.Stale => SymbolRegular.History24,
        CaseStatus.Error => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Circle24,
    };

    public string StatusBrushKey => Status switch
    {
        CaseStatus.Passed => "SystemFillColorSuccessBrush",
        CaseStatus.Failed or CaseStatus.Error => "SystemFillColorCriticalBrush",
        CaseStatus.Flaky or CaseStatus.Stale => "SystemFillColorCautionBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    public string StatusLabel => Status switch
    {
        CaseStatus.Passed => "passou",
        CaseStatus.Failed => "falhou",
        CaseStatus.Flaky => "instável",
        CaseStatus.Stale => "desatualizado",
        CaseStatus.Error => "erro",
        _ => "não rodou",
    };

    partial void OnKindChanged(EvalKind value)
    {
        OnPropertyChanged(nameof(IsTrigger));
        OnPropertyChanged(nameof(IsBehavior));
        OnPropertyChanged(nameof(KindLabel));
    }

    partial void OnShouldTriggerChanged(bool value) => OnPropertyChanged(nameof(KindLabel));
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnPromptChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

    partial void OnStatusChanged(CaseStatus value)
    {
        OnPropertyChanged(nameof(StatusSymbol));
        OnPropertyChanged(nameof(StatusBrushKey));
        OnPropertyChanged(nameof(StatusLabel));
    }

    partial void OnResultChanged(CaseResult? value)
    {
        Runs.Clear();
        if (value is null) return;
        for (var i = 0; i < value.Runs.Count; i++) Runs.Add(new RunViewModel(i + 1, value.Runs[i], false));
        if (value.BaselineRuns is { } b)
            for (var i = 0; i < b.Count; i++) Runs.Add(new RunViewModel(i + 1, b[i], true));
    }

    public AssertionViewModel AddAssertion(AssertionKind kind, string value = "")
    {
        var vm = new AssertionViewModel { Kind = kind, Value = value };
        vm.PropertyChanged += (_, _) => Changed?.Invoke();
        Assertions.Add(vm);
        return vm;
    }

    public EvalCase ToModel() => new()
    {
        Id = Id,
        Title = Title.Trim(),
        Kind = Kind,
        Prompt = Prompt,
        ShouldTrigger = IsTrigger ? ShouldTrigger : null,
        Assertions = IsBehavior ? Assertions.Select(a => a.ToModel()).ToList() : [],
        Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
    };

    public static EvalCaseViewModel From(EvalCase c)
    {
        var vm = new EvalCaseViewModel
        {
            Id = c.Id,
            Title = c.Title,
            Kind = c.Kind,
            Prompt = c.Prompt,
            ShouldTrigger = c.ShouldTrigger ?? true,
            Notes = c.Notes ?? "",
        };
        foreach (var a in c.Assertions) vm.AddAssertion(a.Kind, a.Value);
        return vm;
    }

    private static string TextPreview(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length == 0 ? "(sem pedido)" : line.Length > 60 ? line[..60] + "…" : line;
    }
}

/// <summary>Aba Playground: casos de teste da skill aberta, execução contra o Claude e placar de confiança.</summary>
public sealed partial class PlaygroundViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private bool _loading;

    public PlaygroundViewModel(MainViewModel main)
    {
        _main = main;
        Cases.CollectionChanged += (_, _) => OnCasesChanged();
    }

    public event Action? SuiteChanged;

    public ObservableCollection<EvalCaseViewModel> Cases { get; } = [];
    public ObservableCollection<string> ConfidenceReasons { get; } = [];

    public IReadOnlyList<string> EffortOptions { get; } = ["low", "medium", "high"];
    public IReadOnlyList<int> RepetitionOptions { get; } = [1, 2, 3, 5];

    [ObservableProperty] private EvalCaseViewModel? _selectedCase;
    [ObservableProperty] private int _repetitions = 2;
    [ObservableProperty] private bool _includeBaseline;
    [ObservableProperty] private string _effort = "medium";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _runStatus = "";
    [ObservableProperty] private string _confidenceLabel = "Sem evidência";
    [ObservableProperty] private int _confidenceScore;
    [ObservableProperty] private string _confidenceHeadline = "Nenhum caso de teste";
    [ObservableProperty] private ConfidenceLevel _confidenceLevel = ConfidenceLevel.None;
    [ObservableProperty] private string _metrics = "";
    [ObservableProperty] private string _estimate = "";
    [ObservableProperty] private bool _isUserInvoked = true;

    public EvalReport? LastReport { get; private set; }

    public string ConfidenceBrushKey => ConfidenceLevel switch
    {
        ConfidenceLevel.High => "SystemFillColorSuccessBrush",
        ConfidenceLevel.Medium => "SystemFillColorCautionBrush",
        ConfidenceLevel.Low => "SystemFillColorCriticalBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    partial void OnConfidenceLevelChanged(ConfidenceLevel value) => OnPropertyChanged(nameof(ConfidenceBrushKey));
    partial void OnRepetitionsChanged(int value) => UpdateEstimate();
    partial void OnIncludeBaselineChanged(bool value) => UpdateEstimate();

    public static string KindLabel(AssertionKind k) => k switch
    {
        AssertionKind.Contains => "contém",
        AssertionKind.NotContains => "não contém",
        AssertionKind.Regex => "regex",
        _ => "rubrica",
    };

    // ------------------------------------------------------------------ carga e estado

    public void Load(EvalSuite suite, EvalReport? report)
    {
        _loading = true;
        try
        {
            Cases.Clear();
            foreach (var c in suite.Cases) AddCaseVm(EvalCaseViewModel.From(c));
            LastReport = report;
            if (report is not null)
            {
                Repetitions = RepetitionOptions.Contains(report.Repetitions) ? report.Repetitions : Repetitions;
                IncludeBaseline = report.IncludedBaseline;
                if (EffortOptions.Contains(report.Effort)) Effort = report.Effort;
            }
            SelectedCase = Cases.FirstOrDefault();
            RunStatus = report is null ? "" : $"Última execução: {report.RanAt.LocalDateTime:g} · {report.Model} · {report.InputTokens + report.OutputTokens:N0} tokens";
        }
        finally
        {
            _loading = false;
        }
    }

    public EvalSuite ToSuite(string skill) => new() { Skill = skill, Cases = Cases.Select(c => c.ToModel()).ToList() };

    private EvalCaseViewModel AddCaseVm(EvalCaseViewModel vm)
    {
        vm.Changed += OnCasesChanged;
        Cases.Add(vm);
        return vm;
    }

    private void OnCasesChanged()
    {
        if (_loading) return;
        SuiteChanged?.Invoke();
    }

    /// <summary>Chamado pelo editor a cada revalidação: atualiza status dos casos e o placar.</summary>
    public void Refresh(SkillDraft draft, int validationErrors)
    {
        IsUserInvoked = draft.Invocation == InvocationMode.User;
        var suite = ToSuite(draft.Name);
        var skillHash = EvalStore.HashSkill(draft);
        foreach (var c in Cases)
        {
            var r = LastReport?.Cases.FirstOrDefault(x => x.CaseId == c.Id);
            if (!ReferenceEquals(c.Result, r)) c.Result = r;
            if (r is null) { c.Status = CaseStatus.NotRun; c.ResultSummary = ""; continue; }
            c.ResultSummary = $"{r.PassCount}/{r.Runs.Count}";
            c.Status = LastReport!.SkillHash != skillHash || r.CaseHash != EvalStore.HashCase(c.ToModel()) ? CaseStatus.Stale
                : r.HasErrors ? CaseStatus.Error
                : r.Passed ? CaseStatus.Passed
                : r.IsFlaky ? CaseStatus.Flaky
                : CaseStatus.Failed;
        }

        var a = ConfidenceCalculator.Assess(draft, suite, LastReport, validationErrors);
        ConfidenceLevel = a.Level;
        ConfidenceLabel = a.LevelLabel;
        ConfidenceScore = a.Score;
        ConfidenceHeadline = a.Headline;
        ConfidenceReasons.Clear();
        foreach (var reason in a.Reasons) ConfidenceReasons.Add(reason);
        var parts = new List<string>();
        if (a.BehaviorPassRate is { } b) parts.Add($"comportamento {b:P0}");
        if (a.TriggerAccuracy is { } t) parts.Add($"gatilho {t:P0}");
        if (a.BaselinePassRate is { } bl) parts.Add($"sem a skill {bl:P0}");
        Metrics = string.Join(" · ", parts);
        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        var draft = _main.BuildDraft();
        var calls = EvalRunner.EstimateCalls(draft, ToSuite(draft.Name), Options(null));
        Estimate = calls == 0 ? "Nada para rodar" : $"≈ {calls} chamada(s) ao Claude";
    }

    // ------------------------------------------------------------------ edição dos casos

    [RelayCommand]
    private void AddBehaviorCase()
    {
        var vm = NewCase(EvalKind.Behavior);
        vm.Title = "Caminho feliz";
        vm.AddAssertion(AssertionKind.Rubric);
        SelectedCase = vm;
    }

    [RelayCommand]
    private void AddTriggerCase(string? should)
    {
        var vm = NewCase(EvalKind.Trigger);
        vm.ShouldTrigger = should != "false";
        vm.Title = vm.ShouldTrigger ? "Deve acionar" : "Não deve acionar";
        SelectedCase = vm;
    }

    private EvalCaseViewModel NewCase(EvalKind kind)
    {
        var suite = new EvalSuite { Cases = Cases.Select(c => c.ToModel()).ToList() };
        return AddCaseVm(new EvalCaseViewModel { Id = suite.NextId(), Kind = kind });
    }

    [RelayCommand]
    private void DuplicateCase()
    {
        if (SelectedCase is null) return;
        var model = SelectedCase.ToModel();
        model.Id = new EvalSuite { Cases = Cases.Select(c => c.ToModel()).ToList() }.NextId();
        model.Title = (model.Title + " (cópia)").Trim();
        SelectedCase = AddCaseVm(EvalCaseViewModel.From(model));
    }

    [RelayCommand]
    private void RemoveCase()
    {
        if (SelectedCase is null) return;
        var index = Cases.IndexOf(SelectedCase);
        Cases.Remove(SelectedCase);
        SelectedCase = Cases.Count == 0 ? null : Cases[Math.Min(index, Cases.Count - 1)];
    }

    [RelayCommand]
    private void AddAssertion(string? kind)
    {
        SelectedCase?.AddAssertion(Enum.TryParse<AssertionKind>(kind, out var k) ? k : AssertionKind.Rubric);
    }

    [RelayCommand]
    private void RemoveAssertion(AssertionViewModel? assertion)
    {
        if (assertion is not null) SelectedCase?.Assertions.Remove(assertion);
    }

    /// <summary>Usado pelo assistente (update_test_cases).</summary>
    public EvalSuite ApplyCases(IReadOnlyList<EvalCase> upserts, IReadOnlyList<string> removeIds, string skill)
    {
        foreach (var id in removeIds)
            if (Cases.FirstOrDefault(c => c.Id == id) is { } gone) Cases.Remove(gone);
        foreach (var c in upserts)
        {
            var existing = string.IsNullOrEmpty(c.Id) ? null : Cases.FirstOrDefault(x => x.Id == c.Id);
            if (existing is not null)
            {
                var index = Cases.IndexOf(existing);
                Cases.RemoveAt(index);
                var vm = EvalCaseViewModel.From(c);
                vm.Changed += OnCasesChanged;
                Cases.Insert(index, vm);
            }
            else
            {
                c.Id = new EvalSuite { Cases = Cases.Select(x => x.ToModel()).ToList() }.NextId();
                AddCaseVm(EvalCaseViewModel.From(c));
            }
        }
        SelectedCase ??= Cases.FirstOrDefault();
        return ToSuite(skill);
    }

    // ------------------------------------------------------------------ execução

    private EvalRunOptions Options(IReadOnlyCollection<string>? only) => new()
    {
        Repetitions = Repetitions,
        IncludeBaseline = IncludeBaseline,
        OnlyCaseIds = only,
        Model = _main.Settings.Model,
        Effort = Effort,
    };

    [RelayCommand]
    private Task RunAll() => Run(null, "todos os casos");

    [RelayCommand]
    private Task RunSelected() => SelectedCase is null ? Task.CompletedTask : Run([SelectedCase.Id], $"o caso {SelectedCase.DisplayTitle}");

    [RelayCommand]
    private Task RunFailed()
    {
        var ids = Cases.Where(c => c.Status is not CaseStatus.Passed).Select(c => c.Id).ToList();
        return ids.Count == 0 ? Task.CompletedTask : Run(ids, $"{ids.Count} caso(s) que não passaram");
    }

    [RelayCommand]
    private void CancelRun() => _cts?.Cancel();

    private async Task Run(IReadOnlyCollection<string>? only, string what)
    {
        if (IsRunning || !_main.HasRepository) return;
        var draft = _main.BuildDraft();
        if (string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.Body))
        {
            RunStatus = "Preencha ao menos o nome e as instruções da skill antes de rodar.";
            return;
        }
        var suite = ToSuite(draft.Name);
        var options = Options(only);
        var calls = EvalRunner.EstimateCalls(draft, suite, options);
        if (calls == 0)
        {
            RunStatus = IsUserInvoked && suite.Cases.All(c => c.Kind == EvalKind.Trigger)
                ? "Skill user-invoked: só casos de comportamento rodam."
                : "Nenhum caso com pedido preenchido para rodar.";
            return;
        }
        if (_main.ConfirmActionAsync is { } confirm &&
            !await confirm("Rodar o Playground?",
                $"Vai rodar {what} com {options.Repetitions} repetição(ões){(options.IncludeBaseline ? " e linha de base sem a skill" : "")}: " +
                $"cerca de {calls} chamada(s) ao {options.Model} (esforço {options.Effort}), cobradas na sua conta da API.",
                "Rodar"))
            return;

        var model = new ClaudeEvalModel(new ClaudeEvalOptions
        {
            Model = _main.Settings.Model,
            Effort = Effort,
            ApiKey = _main.Settings.GetApiKey(),
        });
        var runner = new EvalRunner(model);
        runner.Progress += p => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ProgressText = $"{p.Done}/{p.Total} · {p.Message}";
        });
        var catalog = EvalRunner.BuildCatalog(draft, _main.RepoSummaries);

        IsRunning = true;
        Progress = 0;
        ProgressText = "Enviando...";
        RunStatus = "";
        _cts = new CancellationTokenSource();
        try
        {
            var previous = LastReport;
            var report = await Task.Run(() => runner.RunAsync(draft, suite, catalog, options, previous, _cts.Token));
            LastReport = report;
            var failed = report.Cases.Count(c => !c.Passed);
            RunStatus = $"Concluído: {report.Cases.Count - failed} de {report.Cases.Count} passaram · " +
                        $"{report.InputTokens:N0} tokens de entrada, {report.OutputTokens:N0} de saída" +
                        (report.Note is null ? "" : $" · {report.Note}");
            _main.OnEvalRunCompleted(report);
        }
        catch (OperationCanceledException)
        {
            RunStatus = "Execução cancelada; o resultado anterior foi mantido.";
        }
        catch (EvalFatalException ex)
        {
            RunStatus = ex.Message;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Anthropic.Exceptions.AnthropicException)
        {
            RunStatus = $"Não foi possível falar com a API do Claude: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            ProgressText = "";
            _cts.Dispose();
            _cts = null;
        }
    }

    // ------------------------------------------------------------------ assistente

    [RelayCommand]
    private Task ProposeWithAssistant() => _main.AskAssistant(AssistantPrompts.ProposeTests, "Propor casos de teste");

    [RelayCommand]
    private Task ExplainFailures()
    {
        var failing = Cases.Where(c => c.Status is CaseStatus.Failed or CaseStatus.Flaky or CaseStatus.Error && c.Result is not null).ToList();
        if (failing.Count == 0)
        {
            RunStatus = "Nenhuma falha na última execução para analisar.";
            return Task.CompletedTask;
        }
        var sb = new StringBuilder();
        foreach (var c in failing)
        {
            sb.Append($"## {c.Id} · {c.DisplayTitle} ({c.KindLabel}) · {c.ResultSummary}\n");
            sb.Append("Pedido: ").Append(c.Prompt).Append('\n');
            foreach (var run in c.Result!.Runs.Where(r => !r.Passed).Take(2))
            {
                if (run.ChosenSkill is not null || c.IsTrigger) sb.Append("Escolha do modelo: ").Append(run.ChosenSkill ?? "nenhuma").Append('\n');
                if (run.Error is not null) sb.Append("Erro: ").Append(run.Error).Append('\n');
                foreach (var a in run.Assertions.Where(a => !a.Passed))
                    sb.Append($"✗ [{KindLabel(a.Kind)}] {a.Value} → {a.Reason}\n");
                if (c.IsBehavior) sb.Append("Trecho da resposta: ").Append(Core.TextUtil.Truncate(run.Output, 1500)).Append('\n');
            }
            sb.Append('\n');
        }
        return _main.AskAssistant(AssistantPrompts.ExplainFailures(sb.ToString()), $"Analisar {failing.Count} falha(s) do Playground");
    }
}
