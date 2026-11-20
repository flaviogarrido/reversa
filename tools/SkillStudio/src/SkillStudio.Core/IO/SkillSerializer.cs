using System.Text;
using System.Text.RegularExpressions;
using SkillStudio.Core.Model;
using YamlDotNet.RepresentationModel;

namespace SkillStudio.Core.IO;

/// <summary>
/// Converte <see cref="SkillDraft"/> de/para os arquivos do Reversa, seguindo as convenções
/// das 78 skills existentes: LF, UTF-8 sem BOM, ordem fixa de chaves no frontmatter.
/// </summary>
public static partial class SkillSerializer
{
    public const string SkillFileName = "SKILL.md";
    public const string OpenAiYamlPath = "agents/openai.yaml";
    public const int ShortDescriptionTarget = 60;

    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // ---------------------------------------------------------------- render

    public static string RenderSkillMd(SkillDraft d)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(YamlScalar(d.Name.Trim())).Append('\n');
        sb.Append("description: ").Append(YamlScalar(OneLine(d.Description))).Append('\n');
        if (d.Invocation == InvocationMode.User)
            sb.Append("disable-model-invocation: true\n");
        foreach (var (key, value) in d.ExtraFrontmatter)
            sb.Append(key).Append(": ").Append(value).Append('\n');
        if (!string.IsNullOrWhiteSpace(d.License))
            sb.Append("license: ").Append(YamlScalar(d.License.Trim())).Append('\n');
        if (!string.IsNullOrWhiteSpace(d.Compatibility))
            sb.Append("compatibility: ").Append(YamlScalar(OneLine(d.Compatibility))).Append('\n');

        var m = d.Metadata;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["author"] = m.Author, ["version"] = m.Version, ["framework"] = m.Framework, ["phase"] = m.Phase,
            ["team"] = m.Team, ["role"] = m.Role, ["stage"] = m.Stage, ["mode"] = m.Mode,
        };
        foreach (var (key, value) in m.Extra) values.TryAdd(key, value);
        var order = m.KeyOrder.Where(values.ContainsKey)
            .Concat(new[] { "author", "version", "framework", "phase", "team", "role", "stage", "mode" })
            .Concat(m.Extra.Select(e => e.Key))
            .Distinct(StringComparer.Ordinal);
        var metaLines = new List<string>();
        foreach (var key in order)
        {
            var value = values[key];
            if (string.IsNullOrWhiteSpace(value)) continue;
            metaLines.Add($"  {key}: {(key == "version" ? DoubleQuoted(value.Trim()) : YamlScalar(value.Trim()))}");
        }
        if (metaLines.Count > 0)
        {
            sb.Append("metadata:\n");
            foreach (var line in metaLines) sb.Append(line).Append('\n');
        }
        sb.Append("---\n\n");
        sb.Append(NormalizeNewlines(d.Body).Trim('\n'));
        sb.Append('\n');
        return sb.ToString();
    }

    public static string RenderOpenAiYaml(SkillDraft d)
    {
        var display = string.IsNullOrWhiteSpace(d.DisplayName) ? SuggestDisplayName(d.Name) : d.DisplayName.Trim();
        var shortDesc = string.IsNullOrWhiteSpace(d.ShortDescription)
            ? SuggestShortDescription(d.Description)
            : OneLine(d.ShortDescription);

        var sb = new StringBuilder();
        sb.Append("interface:\n");
        sb.Append("  display_name: ").Append(DoubleQuoted(display)).Append('\n');
        sb.Append("  short_description: ").Append(DoubleQuoted(shortDesc)).Append('\n');
        if (!string.IsNullOrWhiteSpace(d.DefaultPrompt))
            sb.Append("  default_prompt: ").Append(DoubleQuoted(OneLine(d.DefaultPrompt))).Append('\n');
        if (d.Invocation == InvocationMode.User)
        {
            sb.Append("policy:\n");
            sb.Append("  allow_implicit_invocation: false\n");
        }
        return sb.ToString();
    }

    /// <summary>Todos os arquivos que a skill terá em disco (caminho relativo → conteúdo), sem os binários.</summary>
    public static IReadOnlyList<(string RelativePath, string Content)> RenderAll(SkillDraft d)
    {
        var list = new List<(string, string)>
        {
            (SkillFileName, RenderSkillMd(d)),
            (OpenAiYamlPath, RenderOpenAiYaml(d)),
        };
        foreach (var f in d.Files.Where(f => !f.IsBinary))
            list.Add((NormalizeRelativePath(f.RelativePath), EnsureTrailingNewline(NormalizeNewlines(f.Content!))));
        return list;
    }

    // ---------------------------------------------------------------- parse

    public static SkillDraft ParseFolder(string folder)
    {
        var skillPath = Path.Combine(folder, SkillFileName);
        var skillText = File.ReadAllText(skillPath, Encoding.UTF8);
        var yamlPath = Path.Combine(folder, "agents", "openai.yaml");
        var yamlText = File.Exists(yamlPath) ? File.ReadAllText(yamlPath, Encoding.UTF8) : null;

        var draft = Parse(skillText, yamlText);
        draft.OriginalName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = NormalizeRelativePath(Path.GetRelativePath(folder, file));
            if (rel is SkillFileName or OpenAiYamlPath) continue;
            draft.Files.Add(new SkillFile { RelativePath = rel, Content = TryReadText(file) });
        }
        return draft;
    }

    public static SkillDraft Parse(string skillMd, string? openAiYaml)
    {
        var text = NormalizeNewlines(skillMd).TrimStart('﻿');
        var (frontmatter, body) = SplitFrontmatter(text);
        var draft = new SkillDraft { Body = body.Trim('\n'), License = "", Compatibility = "" };
        draft.Metadata = new SkillMetadata { Version = "", Framework = "" };

        if (frontmatter is not null)
        {
            var root = LoadYamlMapping(frontmatter);
            foreach (var pair in root?.Children ?? Enumerable.Empty<KeyValuePair<YamlNode, YamlNode>>())
            {
                var (keyNode, valueNode) = (pair.Key, pair.Value);
                var key = ((YamlScalarNode)keyNode).Value ?? "";
                switch (key)
                {
                    case "name": draft.Name = ScalarText(valueNode); break;
                    case "description":
                        draft.Description = ScalarText(valueNode);
                        draft.DescriptionQuote = valueNode is YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.DoubleQuoted } ? QuoteStyle.Double
                            : valueNode is YamlScalarNode { Style: YamlDotNet.Core.ScalarStyle.SingleQuoted } ? QuoteStyle.Single
                            : QuoteStyle.Auto;
                        break;
                    case "disable-model-invocation": break; // derivado abaixo pela mesma regra do verificador
                    case "license": draft.License = ScalarText(valueNode); break;
                    case "compatibility": draft.Compatibility = ScalarText(valueNode); break;
                    case "metadata" when valueNode is YamlMappingNode meta:
                        ReadMetadata(meta, draft.Metadata);
                        break;
                    default:
                        draft.ExtraFrontmatter.Add(new(key, ExtractRawValue(frontmatter, key)));
                        break;
                }
            }
            draft.Invocation = InvocationAxis.IsClaudeUserInvoked(frontmatter) ? InvocationMode.User : InvocationMode.Model;
        }
        else
        {
            draft.Invocation = InvocationMode.Model;
        }

        if (openAiYaml is not null)
        {
            var y = NormalizeNewlines(openAiYaml).TrimStart('﻿');
            var root = LoadYamlMapping(y);
            if (root is not null && TryGetMapping(root, "interface", out var iface))
            {
                draft.DisplayName = GetScalar(iface, "display_name");
                draft.ShortDescription = GetScalar(iface, "short_description");
                draft.DefaultPrompt = GetScalar(iface, "default_prompt");
            }
            var codexUser = InvocationAxis.IsCodexUserInvoked(y);
            draft.LoadedWithInvocationMismatch = codexUser != (draft.Invocation == InvocationMode.User);
        }
        return draft;
    }

    public static (string? Frontmatter, string Body) SplitFrontmatter(string normalizedText)
    {
        var m = FrontmatterRegex().Match(normalizedText);
        if (!m.Success) return (null, normalizedText);
        return (m.Groups[1].Value, normalizedText[m.Length..]);
    }

    // ---------------------------------------------------------------- helpers públicos

    public static string SuggestDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var parts = name.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => p.Length <= 2 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    /// Mesma forma das skills existentes: a description truncada em ~60 caracteres,
    /// cortando no último espaço e terminando em "…".
    /// </summary>
    public static string SuggestShortDescription(string description, int target = ShortDescriptionTarget)
    {
        var d = OneLine(description);
        if (d.Length <= target) return d;
        var cut = d.LastIndexOf(' ', target);
        if (cut < target / 2) cut = target;
        return d[..cut].TrimEnd(' ', ',', ';', ':', '.', '—', '-') + "…";
    }

    public static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string NormalizeRelativePath(string p) => p.Replace('\\', '/').Trim().TrimStart('/');

    public static string OneLine(string s) => string.Join(' ', (s ?? "").Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Escalar YAML: simples quando seguro, senão entre aspas simples (estilo das skills existentes).</summary>
    public static string YamlScalar(string value)
    {
        if (value.Length == 0) return "''";
        if (IsPlainSafe(value)) return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>Respeita o estilo de aspas original quando ele representa o valor com segurança.</summary>
    public static string QuotedAs(string value, QuoteStyle style) => style switch
    {
        QuoteStyle.Double => DoubleQuoted(value),
        QuoteStyle.Single => "'" + value.Replace("'", "''") + "'",
        _ => YamlScalar(value),
    };

    public static string DoubleQuoted(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ---------------------------------------------------------------- internos

    private static bool IsPlainSafe(string v)
    {
        if (v != v.Trim()) return false;
        if ("-?:,[]{}#&*!|>'\"%@`".Contains(v[0])) return false;
        if (v.Contains(": ") || v.Contains(" #") || v.EndsWith(':')) return false;
        if (v.Contains('\n') || v.Contains('\t')) return false;
        var lower = v.ToLowerInvariant();
        if (lower is "true" or "false" or "yes" or "no" or "on" or "off" or "null" or "~") return false;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        return true;
    }

    private static string EnsureTrailingNewline(string s) => s.EndsWith('\n') ? s : s + "\n";

    private static string? TryReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0)) return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static YamlMappingNode? LoadYamlMapping(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
    }

    private static bool TryGetMapping(YamlMappingNode node, string key, out YamlMappingNode mapping)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var child) && child is YamlMappingNode m)
        {
            mapping = m;
            return true;
        }
        mapping = null!;
        return false;
    }

    private static string GetScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? ScalarText(child) : "";

    private static string ScalarText(YamlNode node) => node is YamlScalarNode s ? s.Value ?? "" : node.ToString();

    private static void ReadMetadata(YamlMappingNode meta, SkillMetadata target)
    {
        foreach (var (k, v) in meta.Children)
        {
            var key = ((YamlScalarNode)k).Value ?? "";
            var value = ScalarText(v);
            target.KeyOrder.Add(key);
            switch (key)
            {
                case "author": target.Author = value; break;
                case "version": target.Version = value; break;
                case "framework": target.Framework = value; break;
                case "phase": target.Phase = value; break;
                case "team": target.Team = value; break;
                case "role": target.Role = value; break;
                case "stage": target.Stage = value; break;
                case "mode": target.Mode = value; break;
                default: target.Extra.Add(new(key, value)); break;
            }
        }
    }

    /// <summary>Preserva o valor original (com aspas) de uma chave de nível superior.</summary>
    private static string ExtractRawValue(string frontmatter, string key)
    {
        var m = Regex.Match(frontmatter, "^" + Regex.Escape(key) + @":[ \t]*(.*)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.TrimEnd() : "";
    }

    [GeneratedRegex(@"^---\n(.*?)\n---[ \t]*\n?", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();
}
