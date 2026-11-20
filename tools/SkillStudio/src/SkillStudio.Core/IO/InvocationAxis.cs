using System.Text.RegularExpressions;

namespace SkillStudio.Core.IO;

/// <summary>
/// Porte fiel de scripts/verify-invocation.py. As expressões são as mesmas do verificador
/// oficial, para que o que passa aqui passe também no gate de CI do repositório.
/// </summary>
public static partial class InvocationAxis
{
    /// <summary>Assinaturas de gatilho de MODELO, proibidas na description de skills user-invoked.</summary>
    public static readonly string[] TriggerSignatures = ["digitar \"", "Use com \"", "Ative com ", "pedir \""];

    public static bool IsClaudeUserInvoked(string frontmatter) => ClaudeUserRegex().IsMatch(frontmatter);

    public static bool IsCodexUserInvoked(string openAiYaml) => CodexUserRegex().IsMatch(openAiYaml);

    public static bool HasDisplayName(string openAiYaml) => DisplayNameRegex().IsMatch(openAiYaml);

    public static bool HasShortDescription(string openAiYaml) => ShortDescriptionRegex().IsMatch(openAiYaml);

    /// <summary>Extrai a description como o verificador faz: colapsa espaços e tira aspas das pontas.</summary>
    public static string? ExtractDescription(string frontmatter)
    {
        var m = DescriptionRegex().Match(frontmatter);
        if (!m.Success) return null;
        var collapsed = string.Join(' ', m.Groups[1].Value.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim().Trim('\'', '"');
    }

    public static string? FindTriggerSignature(string description) =>
        TriggerSignatures.FirstOrDefault(description.Contains);

    [GeneratedRegex(@"^disable-model-invocation:\s*true\s*$", RegexOptions.Multiline)]
    private static partial Regex ClaudeUserRegex();

    [GeneratedRegex(@"^\s*allow_implicit_invocation:\s*false\s*$", RegexOptions.Multiline)]
    private static partial Regex CodexUserRegex();

    [GeneratedRegex(@"^\s*display_name:", RegexOptions.Multiline)]
    private static partial Regex DisplayNameRegex();

    [GeneratedRegex(@"^\s*short_description:", RegexOptions.Multiline)]
    private static partial Regex ShortDescriptionRegex();

    [GeneratedRegex(@"^description:\s*(.+?)(?=\n[a-zA-Z_-]+:|\z)", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex DescriptionRegex();
}
