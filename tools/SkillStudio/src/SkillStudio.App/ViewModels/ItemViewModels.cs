using CommunityToolkit.Mvvm.ComponentModel;
using SkillStudio.Core.Model;
using SkillStudio.Core.Validation;
using Wpf.Ui.Controls;

namespace SkillStudio.App.ViewModels;

public sealed class SkillListItem(SkillSummary summary, bool hasTests = false)
{
    public SkillSummary Summary { get; } = summary;
    public bool HasTests { get; } = hasTests;
    public string Name => Summary.Name;
    public string Description => Summary.Description;
    public bool IsUserInvoked => Summary.IsUserInvoked;
    public string InvocationLabel => Summary.IsUserInvoked ? "user" : "model";
    public string Team => string.IsNullOrEmpty(Summary.Team) ? "" : Summary.Team;
    public bool HasError => Summary.LoadError is not null;
    public bool IsUpstream => Name == "impeccable" || Name == "reversa" || Name.StartsWith("reversa-", StringComparison.Ordinal);
    public string Origin => IsUpstream ? "upstream" : "fork";
}

public sealed partial class FileItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _relativePath = "";

    [ObservableProperty]
    private string _content = "";

    public bool IsBinary { get; init; }

    public string Kind => IsBinary ? "binário (preservado)" : $"{Content.Split('\n').Length} linhas";

    partial void OnContentChanged(string value) => OnPropertyChanged(nameof(Kind));

    public SkillFile ToModel() => new() { RelativePath = RelativePath, Content = IsBinary ? null : Content };

    public static FileItemViewModel From(SkillFile f) =>
        new() { RelativePath = f.RelativePath, Content = f.Content ?? "", IsBinary = f.IsBinary };
}

public enum ChatRole { User, Assistant, Tool, Web, Notice, Error }

public sealed class ChatMessageViewModel(ChatRole role, string text)
{
    public ChatRole Role { get; } = role;
    public string Text { get; } = text;
    public DateTime Time { get; } = DateTime.Now;
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsActivity => Role is ChatRole.Tool or ChatRole.Web or ChatRole.Notice or ChatRole.Error;

    public SymbolRegular Icon => Role switch
    {
        ChatRole.Tool => SymbolRegular.Wrench24,
        ChatRole.Web => SymbolRegular.Globe24,
        ChatRole.Error => SymbolRegular.ErrorCircle24,
        ChatRole.Notice => SymbolRegular.Info24,
        ChatRole.User => SymbolRegular.Person24,
        _ => SymbolRegular.Sparkle24,
    };
}

public sealed class IssueViewModel(ValidationIssue issue)
{
    public ValidationIssue Issue { get; } = issue;
    public string RuleId => Issue.RuleId;
    public Severity Severity => Issue.Severity;
    public string Message => Issue.Message;
    public string? Suggestion => Issue.Suggestion;
    public string FieldLabel => Issue.Field switch
    {
        SkillField.Name => "Nome",
        SkillField.Description => "Description",
        SkillField.Invocation => "Invocação",
        SkillField.DisplayName => "Display name",
        SkillField.ShortDescription => "Short description",
        SkillField.License => "Licença",
        SkillField.Compatibility => "Compatibilidade",
        SkillField.Metadata => "Metadata",
        SkillField.Body => "Instruções",
        SkillField.Files => Issue.FilePath ?? "Referências",
        SkillField.Tests => "Testes",
        SkillField.Docs => "Documentação",
        _ => "Pacote",
    };
}
