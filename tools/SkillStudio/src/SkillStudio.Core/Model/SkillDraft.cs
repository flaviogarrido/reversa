namespace SkillStudio.Core.Model;

/// <summary>Estilo de aspas da description no arquivo original (preservado ao regravar).</summary>
public enum QuoteStyle { Auto, Single, Double }

/// <summary>
/// Eixo de invocação do Reversa. Toda skill é uma das duas, sem terceiro estado
/// (ver docs/invocation-policy.md e scripts/verify-invocation.py).
/// </summary>
public enum InvocationMode
{
    /// <summary>Só roda quando o usuário chama (/nome). disable-model-invocation: true + allow_implicit_invocation: false.</summary>
    User,

    /// <summary>O modelo pode acionar sozinho, guiado pela description.</summary>
    Model,
}

public sealed class SkillMetadata
{
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Framework { get; set; } = "reversa";
    public string Phase { get; set; } = "";
    public string Team { get; set; } = "";
    public string Role { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Mode { get; set; } = "";

    /// <summary>Chaves de metadata não reconhecidas, preservadas na ordem original.</summary>
    public List<KeyValuePair<string, string>> Extra { get; set; } = [];

    /// <summary>Ordem das chaves no arquivo de origem, para regravar sem diff desnecessário.</summary>
    public List<string> KeyOrder { get; set; } = [];

    public SkillMetadata Clone() => new()
    {
        Author = Author, Version = Version, Framework = Framework, Phase = Phase,
        Team = Team, Role = Role, Stage = Stage, Mode = Mode, Extra = [.. Extra], KeyOrder = [.. KeyOrder],
    };
}

/// <summary>Arquivo auxiliar da skill (references/, scripts/, assets/...).</summary>
public sealed class SkillFile
{
    public string RelativePath { get; set; } = "";

    /// <summary>Conteúdo texto. Nulo para arquivos binários, que são preservados sem edição.</summary>
    public string? Content { get; set; }

    public bool IsBinary => Content is null;

    public SkillFile Clone() => new() { RelativePath = RelativePath, Content = Content };
}

/// <summary>Representação editável de uma skill: SKILL.md + agents/openai.yaml + arquivos auxiliares.</summary>
public sealed class SkillDraft
{
    public const string DefaultCompatibility =
        "Claude Code, Codex, Cursor, Gemini CLI e demais agentes compatíveis com Agent Skills.";

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public QuoteStyle DescriptionQuote { get; set; } = QuoteStyle.Auto;
    public InvocationMode Invocation { get; set; } = InvocationMode.User;
    public string License { get; set; } = "MIT";
    public string Compatibility { get; set; } = DefaultCompatibility;
    public SkillMetadata Metadata { get; set; } = new();

    public string DisplayName { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string DefaultPrompt { get; set; } = "";

    public string Body { get; set; } = "";
    public List<SkillFile> Files { get; set; } = [];

    /// <summary>Chaves de frontmatter fora do padrão (ex.: argument-hint), preservadas como texto YAML.</summary>
    public List<KeyValuePair<string, string>> ExtraFrontmatter { get; set; } = [];

    /// <summary>Nome da pasta de origem quando a skill foi carregada do repositório; nulo para skill nova.</summary>
    public string? OriginalName { get; set; }

    /// <summary>Quando carregada, indica que SKILL.md e openai.yaml discordavam sobre o eixo de invocação.</summary>
    public bool LoadedWithInvocationMismatch { get; set; }

    public bool IsNew => OriginalName is null;

    public SkillDraft Clone() => new()
    {
        Name = Name, Description = Description, DescriptionQuote = DescriptionQuote, Invocation = Invocation, License = License,
        Compatibility = Compatibility, Metadata = Metadata.Clone(), DisplayName = DisplayName,
        ShortDescription = ShortDescription, DefaultPrompt = DefaultPrompt, Body = Body,
        Files = Files.Select(f => f.Clone()).ToList(), ExtraFrontmatter = [.. ExtraFrontmatter],
        OriginalName = OriginalName, LoadedWithInvocationMismatch = LoadedWithInvocationMismatch,
    };
}
