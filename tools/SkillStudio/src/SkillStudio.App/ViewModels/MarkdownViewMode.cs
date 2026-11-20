using CommunityToolkit.Mvvm.ComponentModel;

namespace SkillStudio.App.ViewModels;

public enum MarkdownViewKind { Edit, Split, Preview }

/// <summary>
/// Como um editor de Markdown aparece: só o texto, texto e visualização lado a lado, ou só a visualização.
/// Fica fora das propriedades do rascunho para não marcar a skill como alterada.
/// </summary>
public sealed partial class MarkdownViewMode : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdit), nameof(IsSplit), nameof(IsPreview), nameof(ShowEditor), nameof(ShowPreview))]
    private MarkdownViewKind _kind;

    /// <summary>Falso quando o conteúdo não é Markdown (script, binário): mostra só o editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditor), nameof(ShowPreview))]
    private bool _canPreview = true;

    public bool IsEdit { get => Kind == MarkdownViewKind.Edit; set { if (value) Kind = MarkdownViewKind.Edit; } }
    public bool IsSplit { get => Kind == MarkdownViewKind.Split; set { if (value) Kind = MarkdownViewKind.Split; } }
    public bool IsPreview { get => Kind == MarkdownViewKind.Preview; set { if (value) Kind = MarkdownViewKind.Preview; } }

    public bool ShowEditor => Kind != MarkdownViewKind.Preview || !CanPreview;
    public bool ShowPreview => Kind != MarkdownViewKind.Edit && CanPreview;
}
