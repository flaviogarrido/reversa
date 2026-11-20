using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkillStudio.App.Services;
using SkillStudio.Core.Assistant;
using SkillStudio.Core.Docs;
using SkillStudio.Core.Evals;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;
using SkillStudio.Core.Search;
using SkillStudio.Core.Validation;

namespace SkillStudio.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDraftHost, IEvalHost
{
    public const int TabIdentity = 0, TabBody = 1, TabFiles = 2, TabMetadata = 3, TabPreview = 4, TabPlayground = 5, TabRules = 6;

    private readonly SkillValidator _validator = new();
    private readonly DispatcherTimer _debounce;
    private AppSettings _settings;
    private SkillRepository? _repo;
    private SkillSearchIndex _index = SkillSearchIndex.Build([]);
    private ValidationContext _context = ValidationContext.Empty;
    private SkillDraft _base = new();
    private string _savedSnapshot = "";
    private bool _suspend;
    private SkillAssistant? _assistant;
    private CancellationTokenSource? _chatCts;
    private EvalStore? _evalStore;
    private DocsStore? _docsStore;
    private IReadOnlyList<SkillSummary> _summaries = [];

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        Playground = new PlaygroundViewModel(this);
        Playground.SuiteChanged += ScheduleValidation;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Revalidate(); };

        SkillsView = CollectionViewSource.GetDefaultView(Skills);
        SkillsView.Filter = o => o is SkillListItem s && MatchesFilter(s);

        PropertyChanged += OnAnyPropertyChanged;
        Files.CollectionChanged += (_, _) => ScheduleValidation();

        var root = !string.IsNullOrWhiteSpace(_settings.RepositoryPath) && SkillRepository.IsReversaRoot(_settings.RepositoryPath)
            ? _settings.RepositoryPath
            : SkillRepository.FindRoot(AppContext.BaseDirectory) ?? SkillRepository.FindRoot(Environment.CurrentDirectory);
        if (root is not null) OpenRepository(root);
        NewDraft();
    }

    public IReadOnlyList<RuleInfo> Rules => RuleCatalog.All;
    public AppSettings Settings => _settings;
    public PlaygroundViewModel Playground { get; }
    public IReadOnlyList<SkillSummary> RepoSummaries => _summaries;

    // ------------------------------------------------------------------ repositório e lista

    public ObservableCollection<SkillListItem> Skills { get; } = [];
    public ICollectionView SkillsView { get; }

    [ObservableProperty] private string _repositoryPath = "";
    [ObservableProperty] private string _repositoryStatus = "Nenhum repositório aberto";
    [ObservableProperty] private string _skillFilter = "";
    [ObservableProperty] private bool _showOnlyFork;
    [ObservableProperty] private SkillListItem? _selectedSkill;

    partial void OnSkillFilterChanged(string value) => SkillsView.Refresh();
    partial void OnShowOnlyForkChanged(bool value) => SkillsView.Refresh();

    private bool MatchesFilter(SkillListItem s)
    {
        if (ShowOnlyFork && s.IsUpstream) return false;
        if (string.IsNullOrWhiteSpace(SkillFilter)) return true;
        return s.Name.Contains(SkillFilter, StringComparison.OrdinalIgnoreCase)
               || s.Description.Contains(SkillFilter, StringComparison.OrdinalIgnoreCase)
               || s.Team.Contains(SkillFilter, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasRepository => _repo is not null;

    public void OpenRepository(string root)
    {
        try
        {
            _repo = new SkillRepository(root);
            _evalStore = new EvalStore(_repo.Root);
            _docsStore = new DocsStore(_repo.Root);
            RepositoryPath = _repo.Root;
            _settings.RepositoryPath = _repo.Root;
            _settings.Save();
            RefreshSkills();
            OnPropertyChanged(nameof(HasRepository));
            _assistant = null; // o índice e o repositório mudaram
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RepositoryStatus = ex.Message;
        }
    }

    private void RefreshSkills()
    {
        if (_repo is null) return;
        var summaries = _repo.ListSkills();
        _summaries = summaries;
        var withTests = _evalStore?.ListSkillsWithCases() ?? new HashSet<string>();
        Skills.Clear();
        foreach (var s in summaries) Skills.Add(new SkillListItem(s, withTests.Contains(s.Name)));
        _index = SkillSearchIndex.Build(summaries);
        _context = _repo.CreateValidationContext(_settings.PreferredPrefix);
        var (pkg, version) = _repo.ReadPackageInfo();
        var fork = summaries.Count(s => !new SkillListItem(s).IsUpstream);
        RepositoryStatus = $"{pkg} v{version} · {summaries.Count} skills ({fork} do fork)";
        ScheduleValidation();
    }

    // ------------------------------------------------------------------ campos do editor

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isUserInvoked = true;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _shortDescription = "";
    [ObservableProperty] private bool _autoShortDescription = true;
    [ObservableProperty] private bool _autoDisplayName = true;
    [ObservableProperty] private string _license = "MIT";
    [ObservableProperty] private string _compatibility = SkillDraft.DefaultCompatibility;
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _version = "1.0.0";
    [ObservableProperty] private string _framework = "reversa";
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private string _team = "";
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private FileItemViewModel? _selectedFile;
    [ObservableProperty] private int _selectedTabIndex;

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    /// <summary>Editar, dividir ou visualizar o Markdown das instruções.</summary>
    public MarkdownViewMode BodyView { get; } = new();

    /// <summary>Mesmo controle para o arquivo de referência selecionado (só Markdown tem visualização).</summary>
    public MarkdownViewMode FileView { get; } = new();

    partial void OnSelectedFileChanged(FileItemViewModel? oldValue, FileItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedFilePathChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedFilePathChanged;
        FileView.CanPreview = newValue?.CanPreview ?? false;
    }

    private void OnSelectedFilePathChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileItemViewModel.RelativePath) && sender is FileItemViewModel f)
            FileView.CanPreview = f.CanPreview;
    }

    public IReadOnlyList<string> TeamSuggestions { get; } = MetadataRules.KnownTeams;
    public IReadOnlyList<string> PhaseSuggestions { get; } =
        ["reconhecimento", "escavacao", "interpretacao", "geracao", "revisao", "forward", "maintenance", "pricing", "traducao", "qualquer"];
    public IReadOnlyList<string> RoleSuggestions { get; } = ["specialist", "orchestrator", "analyst", "reviewer", "publisher", "help"];

    [ObservableProperty] private string _editorTitle = "Nova skill";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isExistingSkill;

    public int DescriptionLength => SkillSerializer.OneLine(Description).Length;
    public int BodyLineCount => string.IsNullOrEmpty(Body) ? 0 : Body.Split('\n').Length;
    public string SuggestedName => NameRules.Slugify(Name);

    partial void OnNameChanged(string value)
    {
        if (AutoDisplayName && !_suspend) DisplayName = SkillSerializer.SuggestDisplayName(value);
        OnPropertyChanged(nameof(SuggestedName));
    }

    partial void OnDescriptionChanged(string value)
    {
        if (AutoShortDescription && !_suspend) ShortDescription = SkillSerializer.SuggestShortDescription(value);
        OnPropertyChanged(nameof(DescriptionLength));
    }

    partial void OnAutoShortDescriptionChanged(bool value)
    {
        if (value) ShortDescription = SkillSerializer.SuggestShortDescription(Description);
    }

    partial void OnAutoDisplayNameChanged(bool value)
    {
        if (value) DisplayName = SkillSerializer.SuggestDisplayName(Name);
    }

    partial void OnBodyChanged(string value) => OnPropertyChanged(nameof(BodyLineCount));

    private static readonly HashSet<string> NonDraftProperties =
    [
        nameof(Issues), nameof(ErrorCount), nameof(WarningCount), nameof(InfoCount), nameof(CanSave), nameof(PreviewSkillMd),
        nameof(PreviewOpenAiYaml), nameof(SimilarSkills), nameof(IsDirty), nameof(EditorTitle), nameof(SelectedTabIndex),
        nameof(SelectedFile), nameof(ChatInput), nameof(IsAssistantBusy), nameof(AssistantStatus), nameof(SkillFilter),
        nameof(SelectedSkill), nameof(RepositoryStatus), nameof(RepositoryPath), nameof(ValidationSummary), nameof(StatusMessage),
        nameof(DescriptionLength), nameof(BodyLineCount), nameof(SuggestedName), nameof(VerifyOutput), nameof(IsVerifying),
        nameof(ShowOnlyFork), nameof(HasRepository), nameof(IsExistingSkill), nameof(TokenUsage), nameof(CredentialSource),
    ];

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || NonDraftProperties.Contains(e.PropertyName)) return;
        ScheduleValidation();
    }

    public void ScheduleValidation()
    {
        if (_suspend) return;
        _debounce.Stop();
        _debounce.Start();
    }

    // ------------------------------------------------------------------ rascunho ⇄ campos

    public SkillDraft BuildDraft()
    {
        var d = _base.Clone();
        d.Name = Name.Trim();
        d.Description = Description;
        d.Invocation = IsUserInvoked ? InvocationMode.User : InvocationMode.Model;
        d.DisplayName = DisplayName;
        d.ShortDescription = ShortDescription;
        d.License = License;
        d.Compatibility = Compatibility;
        d.Metadata.Author = Author;
        d.Metadata.Version = Version;
        d.Metadata.Framework = Framework;
        d.Metadata.Phase = Phase;
        d.Metadata.Team = Team;
        d.Metadata.Role = Role;
        d.Metadata.Stage = Stage;
        d.Body = Body;
        d.Files = Files.Select(f => f.ToModel()).ToList();
        return d;
    }

    private void LoadDraft(SkillDraft d, string title)
    {
        _suspend = true;
        try
        {
            _base = d.Clone();
            Name = d.Name;
            Description = d.Description;
            IsUserInvoked = d.Invocation == InvocationMode.User;
            DisplayName = d.DisplayName;
            ShortDescription = d.ShortDescription;
            AutoDisplayName = string.IsNullOrEmpty(d.DisplayName) || d.DisplayName == SkillSerializer.SuggestDisplayName(d.Name);
            AutoShortDescription = string.IsNullOrEmpty(d.ShortDescription) || d.ShortDescription == SkillSerializer.SuggestShortDescription(d.Description);
            License = d.License;
            Compatibility = d.Compatibility;
            Author = d.Metadata.Author;
            Version = d.Metadata.Version;
            Framework = d.Metadata.Framework;
            Phase = d.Metadata.Phase;
            Team = d.Metadata.Team;
            Role = d.Metadata.Role;
            Stage = d.Metadata.Stage;
            Body = d.Body;
            Files.Clear();
            foreach (var f in d.Files) AddFileVm(FileItemViewModel.From(f));
            SelectedFile = Files.FirstOrDefault();
            EditorTitle = title;
            IsExistingSkill = !d.IsNew;
            LoadEvals(d);
            if (AutoDisplayName) DisplayName = SkillSerializer.SuggestDisplayName(Name);
            if (AutoShortDescription) ShortDescription = SkillSerializer.SuggestShortDescription(Description);
        }
        finally
        {
            _suspend = false;
        }
        Revalidate();
        _savedSnapshot = Snapshot();
        IsDirty = false;
    }

    private string Snapshot()
    {
        var d = BuildDraft();
        return string.Join("\u0001", SkillSerializer.RenderAll(d).Select(f => f.RelativePath + "\u0002" + f.Content))
               + "\u0003" + EvalStore.HashSuite(Playground.ToSuite(d.Name));
    }

    private void LoadEvals(SkillDraft d)
    {
        if (_evalStore is not null && d.OriginalName is { Length: > 0 } name)
        {
            try
            {
                Playground.Load(_evalStore.LoadSuite(name), _evalStore.LoadLastRun(name));
                return;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
            {
                StatusMessage = $"Não foi possível ler os casos de teste de {name}: {ex.Message}";
            }
        }
        Playground.Load(new EvalSuite { Skill = d.Name }, null);
    }

    private ValidationContext EvalContext(string skillName) =>
        _context.WithEvals(Playground.ToSuite(skillName), Playground.LastReport).WithDocs(DocsStatusOf(skillName));

    /// <summary>Situação das páginas do site; nula quando o repositório não tem o site ou o nome ainda é inválido.</summary>
    private DocsStatus? DocsStatusOf(string skillName)
    {
        if (_docsStore is not { IsAvailable: true } docs || string.IsNullOrWhiteSpace(skillName)) return null;
        try
        {
            return docs.Status(skillName.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Resultado novo do Playground: grava já se a skill está salva e sem alterações; senão, no próximo Salvar.</summary>
    public void OnEvalRunCompleted(EvalReport report)
    {
        Revalidate();
        if (!IsDirty && IsExistingSkill && _evalStore is not null)
        {
            try
            {
                _evalStore.SaveLastRun(Name.Trim(), report);
                StatusMessage = $"Resultado gravado em evals/{Name.Trim()}/{EvalStore.LastRunFile}. Faça commit junto com a skill.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                StatusMessage = $"Falha ao gravar o resultado: {ex.Message}";
            }
        }
        else
        {
            StatusMessage = "Resultado do Playground será gravado em evals/ no próximo Salvar.";
        }
    }

    // ------------------------------------------------------------------ validação e prévia

    public ObservableCollection<IssueViewModel> Issues { get; } = [];
    public ObservableCollection<SearchHit> SimilarSkills { get; } = [];

    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;
    [ObservableProperty] private bool _canSave;
    [ObservableProperty] private string _previewSkillMd = "";
    [ObservableProperty] private string _previewOpenAiYaml = "";
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private string _statusMessage = "";

    private string _lastSimilarQuery = "";

    public void Revalidate()
    {
        var draft = BuildDraft();
        var report = _validator.Validate(draft, EvalContext(draft.Name));
        Issues.Clear();
        foreach (var i in report.Issues) Issues.Add(new IssueViewModel(i));
        ErrorCount = report.ErrorCount;
        WarningCount = report.WarningCount;
        InfoCount = report.InfoCount;
        CanSave = report.CanSave && _repo is not null;
        ValidationSummary = report.ErrorCount > 0
            ? $"{report.ErrorCount} erro(s) impedem salvar"
            : report.WarningCount > 0 ? $"Pronta para salvar, com {report.WarningCount} aviso(s)" : "Tudo certo";
        PreviewSkillMd = SkillSerializer.RenderSkillMd(draft);
        PreviewOpenAiYaml = SkillSerializer.RenderOpenAiYaml(draft);
        IsDirty = _savedSnapshot.Length > 0 && Snapshot() != _savedSnapshot;
        Playground.Refresh(draft, report.ErrorCount);

        var query = $"{Name.Replace('-', ' ')} {Description}";
        if (query != _lastSimilarQuery)
        {
            _lastSimilarQuery = query;
            SimilarSkills.Clear();
            foreach (var hit in _index.Search(query, 6, draft.OriginalName)) SimilarSkills.Add(hit);
        }
    }

    [RelayCommand]
    private void GoToIssue(IssueViewModel? issue)
    {
        if (issue is null) return;
        SelectedTabIndex = issue.Issue.Field switch
        {
            SkillField.Body => TabBody,
            SkillField.Files => TabFiles,
            SkillField.Metadata or SkillField.License or SkillField.Compatibility => TabMetadata,
            SkillField.Package => TabPreview,
            SkillField.Tests => TabPlayground,
            _ => TabIdentity,
        };
        if (issue.Issue.Field == SkillField.Files && issue.Issue.FilePath is { } path)
            SelectedFile = Files.FirstOrDefault(f => f.RelativePath == path) ?? SelectedFile;
    }

    // ------------------------------------------------------------------ comandos do editor

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Confirmação genérica (título, mensagem, texto do botão principal).</summary>
    public Func<string, string, string, Task<bool>>? ConfirmActionAsync { get; set; }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty || ConfirmAsync is null) return true;
        return await ConfirmAsync("Alterações não salvas", "Há alterações não salvas nesta skill. Descartar e continuar?");
    }

    [RelayCommand]
    private async Task NewSkill()
    {
        if (!await ConfirmDiscardAsync()) return;
        NewDraft();
    }

    private void NewDraft()
    {
        var d = new SkillDraft
        {
            Name = _settings.PreferredPrefix,
            Metadata = new SkillMetadata { Author = _settings.Author },
            Body = """
                Você é o ... (papel da skill em uma frase).

                ## Antes de começar

                1. Leia `.reversa/state.json` para saber em que ponto o projeto está.

                ## Processo

                1. ...

                ## Saída

                - Escreva o resultado em `_reversa_sdd/...`.

                ## Limites

                - Não altere arquivos fora de `.reversa/` e da pasta de saída.
                """,
        };
        LoadDraft(d, "Nova skill");
        _savedSnapshot = "";
        SelectedTabIndex = TabIdentity;
        StatusMessage = "Rascunho novo. Preencha os campos ou peça ajuda ao assistente.";
    }

    [RelayCommand]
    private async Task OpenSkill(SkillListItem? item)
    {
        if (item is null || _repo is null) return;
        if (!await ConfirmDiscardAsync()) return;
        try
        {
            LoadDraft(_repo.Load(item.Name), item.Name);
            StatusMessage = $"Skill {item.Name} aberta.";
        }
        catch (Exception ex) when (ex is IOException or YamlDotNet.Core.YamlException)
        {
            StatusMessage = $"Não foi possível abrir {item.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UseAsTemplate(SkillListItem? item)
    {
        if (item is null || _repo is null) return;
        if (!await ConfirmDiscardAsync()) return;
        var d = _repo.Load(item.Name);
        d.OriginalName = null;
        d.Name = _settings.PreferredPrefix;
        d.DisplayName = "";
        d.ShortDescription = "";
        d.Metadata.Author = _settings.Author;
        d.Metadata.Version = "1.0.0";
        d.Files = d.Files.Where(f => !f.IsBinary).ToList();
        LoadDraft(d, $"Nova skill (a partir de {item.Name})");
        _savedSnapshot = "";
        StatusMessage = $"Estrutura de {item.Name} copiada. Dê um nome novo e adapte o conteúdo.";
    }

    [RelayCommand]
    private void Save()
    {
        if (_repo is null) return;
        Revalidate();
        if (!CanSave)
        {
            StatusMessage = "Corrija os erros antes de salvar.";
            return;
        }
        try
        {
            var draft = BuildDraft();
            var result = _repo.Save(draft);
            var testsChanged = false;
            if (_evalStore is not null)
            {
                testsChanged = _evalStore.SaveSuite(Playground.ToSuite(draft.Name));
                if (Playground.LastReport is { } lastRun) _evalStore.SaveLastRun(draft.Name, lastRun);
            }
            RefreshSkills();
            LoadDraft(_repo.Load(draft.Name), draft.Name);
            SelectedSkill = Skills.FirstOrDefault(s => s.Name == draft.Name);
            var tests = testsChanged ? $" Casos de teste em evals/{draft.Name}/." : "";
            StatusMessage = result.Written.Count + result.Deleted.Count == 0
                ? (testsChanged ? $"Skill sem mudanças.{tests}" : "Nada mudou no disco.")
                : $"Salvo em agents/{draft.Name}/ · {result.Written.Count} arquivo(s) gravado(s){(result.Deleted.Count > 0 ? $", {result.Deleted.Count} removido(s)" : "")}.{tests} Rode \"Verificar repositório\" antes do commit.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"Falha ao salvar: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ApplySuggestedName()
    {
        if (!string.IsNullOrEmpty(SuggestedName)) Name = SuggestedName;
    }

    [RelayCommand]
    private void AddFile()
    {
        var n = 1;
        string path;
        do path = $"references/arquivo-{n++}.md"; while (Files.Any(f => f.RelativePath == path));
        var file = AddFileVm(new FileItemViewModel { RelativePath = path, Content = "# Título\n\n" });
        SelectedFile = file;
        SelectedTabIndex = TabFiles;
    }

    private FileItemViewModel AddFileVm(FileItemViewModel vm)
    {
        vm.PropertyChanged += (_, _) => ScheduleValidation();
        Files.Add(vm);
        return vm;
    }

    [RelayCommand]
    private void RemoveFile(FileItemViewModel? file)
    {
        file ??= SelectedFile;
        if (file is null || file.IsBinary) return;
        Files.Remove(file);
        SelectedFile = Files.FirstOrDefault();
    }

    [RelayCommand]
    private void InsertFileReference(FileItemViewModel? file)
    {
        file ??= SelectedFile;
        if (file is null) return;
        Body = Body.TrimEnd() + $"\n\nConsulte `{file.RelativePath}` para ...\n";
        SelectedTabIndex = TabBody;
    }

    [RelayCommand]
    private void OpenSkillFolder()
    {
        if (_repo is null) return;
        var folder = _repo.SkillFolder(string.IsNullOrWhiteSpace(Name) ? "" : Name.Trim());
        Process.Start(new ProcessStartInfo("explorer.exe", Directory.Exists(folder) ? folder : _repo.AgentsDir) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ReloadRepository()
    {
        if (_repo is not null) OpenRepository(_repo.Root);
        StatusMessage = "Lista de skills recarregada.";
    }

    [RelayCommand]
    private void ChooseRepository()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Selecione a raiz do checkout do Reversa (pasta com agents/ e package.json)" };
        if (dialog.ShowDialog() != true) return;
        var root = SkillRepository.FindRoot(dialog.FolderName);
        if (root is null)
        {
            StatusMessage = "Essa pasta não é um checkout do Reversa (falta package.json com o bin \"reversa\" e a pasta agents/).";
            return;
        }
        OpenRepository(root);
    }

    // ------------------------------------------------------------------ verificação oficial

    [ObservableProperty] private string _verifyOutput = "";
    [ObservableProperty] private bool _isVerifying;

    [RelayCommand]
    private async Task VerifyRepository()
    {
        if (_repo is null) return;
        IsVerifying = true;
        VerifyOutput = "Rodando os gates do CI...";
        try
        {
            var steps = await RepoVerifier.RunAsync(_repo.Root);
            VerifyOutput = string.Join("\n\n", steps.Select(s => $"{(s.Success ? "✓" : "✗")} {s.Title}\n{s.Output}"));
            StatusMessage = steps.All(s => s.Success)
                ? "Verificação do repositório aprovada: o CI deve passar."
                : "A verificação do repositório falhou. Veja o resultado na aba Pré-visualização.";
            SelectedTabIndex = TabPreview;
        }
        finally
        {
            IsVerifying = false;
        }
    }

    // ------------------------------------------------------------------ assistente (Claude)

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = [];

    [ObservableProperty] private string _chatInput = "";
    [ObservableProperty] private bool _isAssistantBusy;
    [ObservableProperty] private string _assistantStatus = "Pronto";
    [ObservableProperty] private string _tokenUsage = "";

    public string CredentialSource => _settings.DescribeCredentialSource();

    private SkillAssistant EnsureAssistant()
    {
        if (_assistant is not null) return _assistant;
        if (_repo is null) throw new InvalidOperationException("Abra um repositório antes de usar o assistente.");
        var options = new AssistantOptions
        {
            Model = _settings.Model,
            Effort = _settings.Effort,
            EnableWebSearch = _settings.EnableWebSearch,
            ApiKey = _settings.GetApiKey(),
            PreferredPrefix = _settings.PreferredPrefix,
            Author = _settings.Author,
        };
        var repo = _repo;
        _assistant = new SkillAssistant(options, new AssistantTools(repo, () => _index, this, this, _docsStore, () => OnUi(() => { Revalidate(); return true; })), Skills.Count);
        _assistant.EventRaised += OnAssistantEvent;
        return _assistant;
    }

    private void OnAssistantEvent(AssistantEvent e)
    {
        void Add() => ChatMessages.Add(new ChatMessageViewModel(e.Kind switch
        {
            AssistantEventKind.Text => ChatRole.Assistant,
            AssistantEventKind.Tool => ChatRole.Tool,
            AssistantEventKind.WebSearch => ChatRole.Web,
            AssistantEventKind.Error => ChatRole.Error,
            _ => ChatRole.Notice,
        }, e.Text));
        if (Application.Current.Dispatcher.CheckAccess()) Add();
        else Application.Current.Dispatcher.Invoke(Add);
    }

    [RelayCommand]
    private Task StartInterview() => SendToAssistant(AssistantPrompts.StartInterview, "Iniciar entrevista");

    [RelayCommand]
    private Task SendChat()
    {
        var text = ChatInput.Trim();
        if (text.Length == 0) return Task.CompletedTask;
        ChatInput = "";
        return SendToAssistant(text, text);
    }

    [RelayCommand]
    private Task ReviewWithAssistant() => SendToAssistant(
        "Revise o rascunho atual (get_current_draft): corrija os erros de validação, melhore a description e aponte lacunas no corpo. Faça as correções com update_draft e me diga o que mudou.",
        "Revisar o rascunho atual");

    [RelayCommand]
    private Task DocumentWithAssistant() => SendToAssistant(AssistantPrompts.WriteDocs, "Gerar a página de documentação");

    public Task AskAssistant(string prompt, string shown) => SendToAssistant(prompt, shown);

    private async Task SendToAssistant(string prompt, string shown)
    {
        if (IsAssistantBusy) return;
        SkillAssistant assistant;
        try
        {
            assistant = EnsureAssistant();
        }
        catch (InvalidOperationException ex)
        {
            ChatMessages.Add(new ChatMessageViewModel(ChatRole.Error, ex.Message));
            return;
        }
        ChatMessages.Add(new ChatMessageViewModel(ChatRole.User, shown));
        IsAssistantBusy = true;
        AssistantStatus = $"Claude ({_settings.Model}) está trabalhando...";
        _chatCts = new CancellationTokenSource();
        try
        {
            await assistant.SendAsync(prompt, _chatCts.Token);
        }
        finally
        {
            IsAssistantBusy = false;
            AssistantStatus = "Pronto";
            TokenUsage = $"{assistant.InputTokens:N0} tokens de entrada · {assistant.OutputTokens:N0} de saída nesta sessão";
            _chatCts.Dispose();
            _chatCts = null;
        }
    }

    [RelayCommand]
    private void CancelChat() => _chatCts?.Cancel();

    [RelayCommand]
    private void ResetChat()
    {
        _assistant?.Reset();
        ChatMessages.Clear();
        TokenUsage = "";
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _settings.Save();
        _assistant = null;
        if (_repo is not null) _context = _repo.CreateValidationContext(_settings.PreferredPrefix);
        OnPropertyChanged(nameof(CredentialSource));
        OnPropertyChanged(nameof(Settings));
        Revalidate();
        StatusMessage = "Configurações salvas.";
    }

    // ------------------------------------------------------------------ IDraftHost

    SkillDraft IDraftHost.GetDraft() => OnUi(BuildDraft);

    SkillDraft IDraftHost.ApplyPatch(DraftPatch patch) => OnUi(() =>
    {
        var updated = patch.ApplyTo(BuildDraft());
        _suspend = true;
        try
        {
            if (patch.Name is not null) Name = updated.Name;
            if (patch.Description is not null) Description = updated.Description;
            if (patch.Invocation is not null) IsUserInvoked = updated.Invocation == InvocationMode.User;
            if (patch.DisplayName is not null) { DisplayName = updated.DisplayName; AutoDisplayName = false; }
            if (patch.ShortDescription is not null) { ShortDescription = updated.ShortDescription; AutoShortDescription = updated.ShortDescription.Length == 0; }
            if (patch.License is not null) License = updated.License;
            if (patch.Compatibility is not null) Compatibility = updated.Compatibility;
            if (patch.Author is not null) Author = updated.Metadata.Author;
            if (patch.Version is not null) Version = updated.Metadata.Version;
            if (patch.Phase is not null) Phase = updated.Metadata.Phase;
            if (patch.Team is not null) Team = updated.Metadata.Team;
            if (patch.Role is not null) Role = updated.Metadata.Role;
            if (patch.Stage is not null) Stage = updated.Metadata.Stage;
            if (patch.Body is not null) Body = updated.Body;
            if (patch.UpsertFiles.Count > 0 || patch.RemoveFiles.Count > 0)
            {
                Files.Clear();
                foreach (var f in updated.Files) AddFileVm(FileItemViewModel.From(f));
                SelectedFile = Files.FirstOrDefault(f => patch.UpsertFiles.Any(u => u.RelativePath == f.RelativePath)) ?? Files.FirstOrDefault();
            }
            if (patch.AutoShortDescriptionNeedsRefresh(AutoShortDescription))
                ShortDescription = SkillSerializer.SuggestShortDescription(Description);
            if (patch.Name is not null && AutoDisplayName)
                DisplayName = SkillSerializer.SuggestDisplayName(Name);
        }
        finally
        {
            _suspend = false;
        }
        Revalidate();
        return BuildDraft();
    });

    ValidationReport IDraftHost.Validate(SkillDraft draft) => OnUi(() => _validator.Validate(draft, EvalContext(draft.Name)));

    // ------------------------------------------------------------------ IEvalHost

    EvalSuite IEvalHost.GetSuite() => OnUi(() => Playground.ToSuite(Name.Trim()));

    EvalSuite IEvalHost.ApplyCases(IReadOnlyList<EvalCase> upserts, IReadOnlyList<string> removeIds) => OnUi(() =>
    {
        var suite = Playground.ApplyCases(upserts, removeIds, Name.Trim());
        Revalidate();
        return suite;
    });

    private static T OnUi<T>(Func<T> f) =>
        Application.Current.Dispatcher.CheckAccess() ? f() : Application.Current.Dispatcher.Invoke(f);
}

internal static class DraftPatchExtensions
{
    public static bool AutoShortDescriptionNeedsRefresh(this DraftPatch patch, bool auto) =>
        auto && patch.Description is not null && patch.ShortDescription is null;
}
