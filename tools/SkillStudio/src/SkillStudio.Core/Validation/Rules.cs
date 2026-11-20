using System.Text.RegularExpressions;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;

namespace SkillStudio.Core.Validation;

// Catálogo completo em RuleCatalog.cs. Cada regra é pequena e isolada para ser testável.

/// <summary>RS001–RS009: nome da skill (pasta, comando e chave do manifesto).</summary>
public sealed partial class NameRules : IValidationRule
{
    public const int MaxLength = 64;

    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        var name = d.Name ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return new("RS001", Severity.Error, SkillField.Name, "O nome é obrigatório.",
                $"Use kebab-case com o seu prefixo, ex.: {(ctx.PreferredPrefix.Length > 0 ? ctx.PreferredPrefix : "fg-")}minha-skill");
            yield break;
        }

        if (!KebabRegex().IsMatch(name))
            yield return new("RS002", Severity.Error, SkillField.Name,
                "O nome deve ter só letras minúsculas, números e hífens, sem hífen no início, no fim ou duplicado.",
                $"Sugestão: {Slugify(name)}");

        if (name.Length > MaxLength)
            yield return new("RS003", Severity.Error, SkillField.Name, $"O nome tem {name.Length} caracteres; o limite do padrão Agent Skills é {MaxLength}.");

        var renamed = !d.IsNew && !string.Equals(d.OriginalName, name, StringComparison.Ordinal);
        if ((d.IsNew || renamed) && ctx.ExistingSkillNames.Contains(name))
            yield return new("RS004", Severity.Error, SkillField.Name, $"Já existe uma skill chamada \"{name}\" em agents/.",
                "Escolha outro nome ou abra a skill existente para editar.");

        if (renamed)
            yield return new("RS009", Severity.Warning, SkillField.Name,
                $"O nome mudou de \"{d.OriginalName}\" para \"{name}\": será criada uma pasta nova e a antiga continuará no repositório.",
                "Apague a pasta antiga manualmente se a intenção for renomear.");

        // Bug do updater (lib/commands/update.js): startsWith(relDir) sem a "/" final.
        var collisions = ctx.ExistingSkillNames
            .Where(e => e != name && e != d.OriginalName && (name.StartsWith(e, StringComparison.Ordinal) || e.StartsWith(name, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (collisions.Count > 0)
            yield return new("RS005", Severity.Warning, SkillField.Name,
                $"O nome é prefixo de (ou tem como prefixo) outra skill: {string.Join(", ", collisions.Take(5))}{(collisions.Count > 5 ? $" e mais {collisions.Count - 5}" : "")}. " +
                "No `reversa update`, editar uma delas bloqueia a atualização da outra.",
                "Use um prefixo próprio que não seja nome de outra skill (ex.: fg-).");

        if (!string.IsNullOrEmpty(ctx.UpstreamPrefix) && d.IsNew &&
            (name == ctx.UpstreamPrefix || name.StartsWith(ctx.UpstreamPrefix + "-", StringComparison.Ordinal)))
            yield return new("RS006", Severity.Warning, SkillField.Name,
                $"Skills customizadas não devem usar o prefixo \"{ctx.UpstreamPrefix}-\" do projeto original: geram conflito de merge com o upstream.",
                ctx.PreferredPrefix.Length > 0 ? $"Use \"{ctx.PreferredPrefix}{name[(name.IndexOf('-') + 1)..]}\"." : null);
        else if (ctx.PreferredPrefix.Length > 0 && d.IsNew && !name.StartsWith(ctx.PreferredPrefix, StringComparison.Ordinal))
            yield return new("RS008", Severity.Info, SkillField.Name,
                $"O prefixo configurado para o fork é \"{ctx.PreferredPrefix}\".", $"Ex.: {ctx.PreferredPrefix}{name}");

        foreach (var reserved in new[] { "anthropic", "claude" })
            if (name.Contains(reserved, StringComparison.Ordinal))
                yield return new("RS007", Severity.Error, SkillField.Name,
                    $"O nome não pode conter a palavra reservada \"{reserved}\" (regra das Agent Skills da Anthropic).");
    }

    public static string Slugify(string s)
    {
        var normalized = TextUtil.RemoveDiacritics(s).ToLowerInvariant();
        normalized = NonSlugRegex().Replace(normalized, "-");
        normalized = MultiHyphenRegex().Replace(normalized, "-").Trim('-');
        return normalized.Length > MaxLength ? normalized[..MaxLength].TrimEnd('-') : normalized;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiHyphenRegex();
}

/// <summary>RS010–RS019: description, que é o que a engine lê para decidir e listar a skill.</summary>
public sealed partial class DescriptionRules : IValidationRule
{
    public const int MaxLength = 1024;
    public const int SoftMaxLength = 600;
    public const int MinLength = 40;

    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        var raw = d.Description ?? "";
        var desc = SkillSerializer.OneLine(raw);
        if (desc.Length == 0)
        {
            yield return new("RS010", Severity.Error, SkillField.Description, "A description é obrigatória.",
                "Uma ou duas frases: o que a skill faz e em que momento do fluxo ela entra.");
            yield break;
        }

        if (desc.Length > MaxLength)
            yield return new("RS011", Severity.Error, SkillField.Description,
                $"A description tem {desc.Length} caracteres; o limite do padrão Agent Skills é {MaxLength}.");
        else if (desc.Length > SoftMaxLength)
            yield return new("RS011", Severity.Warning, SkillField.Description,
                $"A description tem {desc.Length} caracteres; a maior do repositório tem ~590. Descrições longas ocupam contexto em toda sessão.",
                "Mova detalhes para o corpo da skill.");

        if (desc.Length < MinLength)
            yield return new("RS012", Severity.Warning, SkillField.Description,
                $"A description tem só {desc.Length} caracteres; fica vaga demais para orientar a engine.");

        if (d.Invocation == InvocationMode.User)
        {
            var sig = InvocationAxis.FindTriggerSignature(desc);
            if (sig is not null)
                yield return new("RS013", Severity.Error, SkillField.Description,
                    $"Skill user-invoked com gatilho de modelo na description (assinatura '{sig}'). O verificador do CI reprova.",
                    "Escreva um resumo humano, sem lista de frases ou comandos de ativação.");
        }

        if (XmlTagRegex().IsMatch(desc))
            yield return new("RS014", Severity.Warning, SkillField.Description,
                "A description tem algo parecido com tag XML/HTML. A API de Skills da Anthropic recusa; Claude Code e Codex aceitam.",
                "Troque <valor> por {valor} ou VALOR.");

        if (d.Invocation == InvocationMode.Model && !UsageHintRegex().IsMatch(desc))
            yield return new("RS015", Severity.Warning, SkillField.Description,
                "Skill model-invoked sem indicação de quando usar. É a description que faz o modelo acioná-la.",
                "Inclua algo como \"Use quando o usuário pedir...\".");

        if (raw.Contains('\n'))
            yield return new("RS016", Severity.Info, SkillField.Description,
                "A description tem quebras de linha; elas serão convertidas em espaços.");

        if (d.Name.Length > 0 && desc.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase))
            yield return new("RS017", Severity.Info, SkillField.Description,
                "A description começa repetindo o nome. Comece pelo que a skill faz.");
    }

    [GeneratedRegex(@"<\s*/?\s*[A-Za-z][^>]*>")]
    private static partial Regex XmlTagRegex();

    [GeneratedRegex(@"(?i)\b(use|usar|usa|quando|when|ative|acione|chame|invoque|sempre que|ao )")]
    private static partial Regex UsageHintRegex();
}

/// <summary>RS020–RS029: eixo de invocação e Codex (agents/openai.yaml).</summary>
public sealed class InterfaceRules : IValidationRule
{
    public const int ShortDescriptionSoftMax = 90;

    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        if (d.LoadedWithInvocationMismatch)
            yield return new("RS020", Severity.Warning, SkillField.Invocation,
                "No disco, SKILL.md e agents/openai.yaml discordam sobre o eixo de invocação. Ao salvar, os dois serão alinhados.");

        if (string.IsNullOrWhiteSpace(d.DisplayName))
            yield return new("RS021", Severity.Info, SkillField.DisplayName,
                $"display_name vazio: será gerado como \"{SkillSerializer.SuggestDisplayName(d.Name)}\".");
        else if (d.DisplayName.Trim().Length > 40)
            yield return new("RS022", Severity.Warning, SkillField.DisplayName,
                "display_name longo demais para menus (mais de 40 caracteres).");

        var shortDesc = SkillSerializer.OneLine(d.ShortDescription);
        if (shortDesc.Length == 0)
            yield return new("RS023", Severity.Info, SkillField.ShortDescription,
                "short_description vazio: será gerado a partir da description.");
        else if (shortDesc.Length > ShortDescriptionSoftMax)
            yield return new("RS024", Severity.Warning, SkillField.ShortDescription,
                $"short_description com {shortDesc.Length} caracteres; no repositório o padrão é ~60 terminando em \"…\".");
    }
}

/// <summary>RS040–RS049: licença, compatibilidade e metadata.</summary>
public sealed partial class MetadataRules : IValidationRule
{
    public const int CompatibilityMaxLength = 500;

    public static readonly string[] KnownTeams =
        ["discovery", "migration", "forward", "ideation", "newproject", "documentation", "bugs", "refactor", "pricing", "shared-skills", "frontend"];

    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        var m = d.Metadata;
        if (string.IsNullOrWhiteSpace(m.Author))
            yield return new("RS040", Severity.Warning, SkillField.Metadata, "metadata.author vazio.", "Informe quem mantém a skill.");

        if (string.IsNullOrWhiteSpace(m.Version))
            yield return new("RS041", Severity.Warning, SkillField.Metadata, "metadata.version vazio.", "Use \"1.0.0\".");
        else if (!SemVerRegex().IsMatch(m.Version.Trim()))
            yield return new("RS041", Severity.Error, SkillField.Metadata,
                $"metadata.version \"{m.Version}\" não é SemVer (MAJOR.MINOR.PATCH).", "Ex.: 1.0.0");

        if (!string.Equals(m.Framework.Trim(), "reversa", StringComparison.Ordinal))
            yield return new("RS042", Severity.Warning, SkillField.Metadata,
                "metadata.framework deveria ser \"reversa\", como nas demais skills.");

        if (string.IsNullOrWhiteSpace(d.License))
            yield return new("RS043", Severity.Warning, SkillField.License, "license vazio.", "O pacote é MIT; use MIT, salvo se a skill vier de outra fonte.");

        if (d.Compatibility.Length > CompatibilityMaxLength)
            yield return new("RS044", Severity.Error, SkillField.Compatibility,
                $"compatibility tem {d.Compatibility.Length} caracteres; o limite do padrão Agent Skills é {CompatibilityMaxLength}.");

        if (!string.IsNullOrWhiteSpace(m.Team) && !KnownTeams.Contains(m.Team.Trim()))
            yield return new("RS045", Severity.Info, SkillField.Metadata,
                $"metadata.team \"{m.Team}\" não é um time conhecido ({string.Join(", ", KnownTeams)}).",
                "Times novos são aceitos, mas não aparecem no resumo do instalador sem ajuste em lib/installer/prompts.js.");

        foreach (var (key, value) in Fields(m))
            if (value.Contains('\n') || value.Contains('\r'))
                yield return new("RS046", Severity.Error, SkillField.Metadata, $"metadata.{key} não pode ter quebra de linha.");

        foreach (var (key, value) in d.ExtraFrontmatter)
            if (!FrontmatterKeyRegex().IsMatch(key) || value.Contains('\n'))
                yield return new("RS047", Severity.Error, SkillField.Metadata, $"Chave de frontmatter extra inválida: \"{key}\".");
    }

    private static IEnumerable<(string, string)> Fields(SkillMetadata m) =>
        [("author", m.Author), ("version", m.Version), ("framework", m.Framework), ("phase", m.Phase),
         ("team", m.Team), ("role", m.Role), ("stage", m.Stage), ("mode", m.Mode), .. m.Extra.Select(e => (e.Key, e.Value))];

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVerRegex();

    [GeneratedRegex("^[a-z][a-z0-9_-]*$")]
    private static partial Regex FrontmatterKeyRegex();
}

/// <summary>RS050–RS059: corpo do SKILL.md (as instruções do agente).</summary>
public sealed partial class BodyRules : IValidationRule
{
    public const int MinChars = 300;
    public const int MaxLines = 500;

    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        var body = SkillSerializer.NormalizeNewlines(d.Body ?? "").Trim('\n');
        if (body.Trim().Length == 0)
        {
            yield return new("RS050", Severity.Error, SkillField.Body, "O corpo da skill (instruções) está vazio.");
            yield break;
        }

        if (body.Trim().Length < MinChars)
            yield return new("RS050", Severity.Warning, SkillField.Body,
                $"Corpo curto ({body.Trim().Length} caracteres). As skills do Reversa costumam descrever papel, entradas, passos, saídas e limites.");

        if (body.TrimStart().StartsWith("---", StringComparison.Ordinal))
            yield return new("RS052", Severity.Error, SkillField.Body,
                "O corpo começa com \"---\", o que é lido como um segundo frontmatter.", "Remova a linha ou use outro separador.");

        var lines = body.Split('\n');
        if (lines.Length > MaxLines)
            yield return new("RS051", Severity.Warning, SkillField.Body,
                $"Corpo com {lines.Length} linhas (recomendado até {MaxLines}). Todo o SKILL.md entra no contexto quando a skill roda.",
                "Mova material de consulta para references/ e cite o arquivo no corpo.");

        var fences = lines.Count(l => FenceRegex().IsMatch(l));
        if (fences % 2 != 0)
            yield return new("RS059", Severity.Error, SkillField.Body,
                "Bloco de código sem fechamento (número ímpar de ``` ou ~~~). O resto do arquivo vira código.");

        if (!lines.Any(l => HeadingRegex().IsMatch(l)))
            yield return new("RS058", Severity.Info, SkillField.Body,
                "O corpo não tem títulos (##). Seções ajudam o agente a navegar: papel, antes de começar, passos, saída, limites.");

        foreach (Match m in PlaceholderRegex().Matches(body))
        {
            yield return new("RS055", Severity.Warning, SkillField.Body, $"Marcador pendente no corpo: \"{m.Value}\".");
            break;
        }

        if (AbsolutePathRegex().Match(body) is { Success: true } abs)
            yield return new("RS056", Severity.Warning, SkillField.Body,
                $"Caminho absoluto da sua máquina no corpo (\"{abs.Value.Trim()}\"). A skill roda em outros projetos e máquinas.",
                "Use caminhos relativos à raiz do projeto, como .reversa/ ou _reversa_sdd/.");

        // Referências citadas no corpo precisam existir na skill.
        var files = d.Files.Select(f => SkillSerializer.NormalizeRelativePath(f.RelativePath)).ToList();
        var cited = CitedPathRegex().Matches(body).Select(m => m.Groups[1].Value.TrimEnd('.', ',', ')', ':', ';')).Distinct().ToList();
        foreach (var path in cited)
        {
            var exists = files.Contains(path) || files.Any(f => f.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal));
            if (exists) continue;
            if (path.Contains("/../", StringComparison.Ordinal))
                yield return new("RS053", Severity.Warning, SkillField.Body,
                    $"O corpo cita \"{path}\", que sai da pasta da skill. Só funciona se a outra skill estiver instalada ao lado.", FilePath: path);
            else if (ctx.RepoSkillFilePaths.Contains(path))
                yield return new("RS053", Severity.Warning, SkillField.Body,
                    $"O corpo cita \"{path}\", que existe em outra skill, não nesta. O agente procura relativo à skill que está rodando.",
                    "Copie o arquivo para esta skill ou cite o caminho da outra skill explicitamente.", path);
            else
                yield return new("RS053", Severity.Error, SkillField.Body,
                    $"O corpo cita \"{path}\", mas esse arquivo não existe na skill.", "Crie o arquivo na aba Referências ou corrija o caminho.", path);
        }
    }

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^#{1,6}\s+\S")]
    private static partial Regex HeadingRegex();

    // Maiúsculas de propósito: "todo" é palavra comum em português.
    [GeneratedRegex(@"\b(TODO|TBD|FIXME|XXX)\b|(?i:lorem ipsum)")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"(?<![\w/])([A-Za-z]:\\[^\s`'""]+|/(?:Users|home)/[^\s`'""]+)")]
    private static partial Regex AbsolutePathRegex();

    // Só references/: scripts/ e assets/ costumam ser caminhos do projeto ou do site gerado, não da skill.
    [GeneratedRegex(@"(?<![\w./-])(references/[A-Za-z0-9_./-]*[A-Za-z0-9_/])")]
    private static partial Regex CitedPathRegex();
}

/// <summary>RS060–RS069: arquivos auxiliares (references/, scripts/, assets/).</summary>
public sealed partial class FileRules : IValidationRule
{
    public static readonly string[] AllowedTopDirs = ["references", "scripts", "assets", "agents", "templates", "examples"];
    public static readonly string[] AllowedRootFiles = ["LICENSE", "LICENSE.md", "NOTICE.md", "README.md"];

    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var body = d.Body ?? "";
        var allText = body + "\n" + string.Join("\n", d.Files.Where(f => !f.IsBinary).Select(f => f.Content));

        foreach (var f in d.Files)
        {
            var path = SkillSerializer.NormalizeRelativePath(f.RelativePath);
            if (path.Length == 0)
            {
                yield return new("RS060", Severity.Error, SkillField.Files, "Arquivo auxiliar sem caminho.");
                continue;
            }
            if (Path.IsPathRooted(f.RelativePath) || path.Split('/').Any(seg => seg is ".." or "." or "") || InvalidCharsRegex().IsMatch(path))
            {
                yield return new("RS060", Severity.Error, SkillField.Files,
                    $"Caminho inválido: \"{f.RelativePath}\". Use caminho relativo, sem \"..\" e sem caracteres especiais.", FilePath: path);
                continue;
            }
            if (path is SkillSerializer.SkillFileName or SkillSerializer.OpenAiYamlPath)
            {
                yield return new("RS061", Severity.Error, SkillField.Files,
                    $"\"{path}\" é gerado pelo editor e não pode ser um arquivo auxiliar.", FilePath: path);
                continue;
            }
            if (!seen.Add(path))
                yield return new("RS064", Severity.Error, SkillField.Files,
                    $"Arquivo duplicado (diferença só de maiúsculas conta em Windows/macOS): \"{path}\".", FilePath: path);

            var segments = path.Split('/');
            if (segments.Length == 1)
            {
                if (!AllowedRootFiles.Contains(path))
                    yield return new("RS062", Severity.Warning, SkillField.Files,
                        $"\"{path}\" está na raiz da skill. O padrão é references/, scripts/ ou assets/.", FilePath: path);
            }
            else if (!AllowedTopDirs.Contains(segments[0]))
            {
                yield return new("RS062", Severity.Warning, SkillField.Files,
                    $"Pasta \"{segments[0]}/\" fora do padrão ({string.Join(", ", AllowedTopDirs.Take(3))}).", FilePath: path);
            }

            if (path.Contains(' '))
                yield return new("RS063", Severity.Warning, SkillField.Files,
                    $"\"{path}\" tem espaços no nome; dificulta citar o arquivo e rodar scripts.", FilePath: path);

            if (segments.Length > 3)
                yield return new("RS063", Severity.Info, SkillField.Files,
                    $"\"{path}\" está aninhado em muitos níveis. Mantenha references a um ou dois níveis.", FilePath: path);

            if (!f.IsBinary && string.IsNullOrWhiteSpace(f.Content))
                yield return new("RS065", Severity.Warning, SkillField.Files, $"\"{path}\" está vazio.", FilePath: path);

            if (segments[0] == "scripts" && !Path.HasExtension(path))
                yield return new("RS066", Severity.Info, SkillField.Files,
                    $"\"{path}\" é um launcher sem extensão: o instalador restaura o bit de execução no macOS/Linux.", FilePath: path);

            if (segments[0] == "references" && !f.IsBinary && !allText.Contains(path, StringComparison.Ordinal) &&
                !allText.Contains(Path.GetFileName(path), StringComparison.Ordinal))
                yield return new("RS054", Severity.Info, SkillField.Files,
                    $"\"{path}\" não é citado no corpo nem em outro arquivo; o agente pode nunca abri-lo.", FilePath: path);
        }
    }

    [GeneratedRegex(@"[<>:""|?*\x00-\x1F]")]
    private static partial Regex InvalidCharsRegex();
}

/// <summary>RS070–RS079: segurança e higiene do conteúdo (corpo e arquivos).</summary>
public sealed partial class SecurityRules : IValidationRule
{
    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        var sources = new List<(string Path, string Text)> { ("SKILL.md", d.Body ?? ""), ("SKILL.md", d.Description ?? "") };
        sources.AddRange(d.Files.Where(f => !f.IsBinary).Select(f => (SkillSerializer.NormalizeRelativePath(f.RelativePath), f.Content!)));

        foreach (var (path, text) in sources)
        {
            var secret = SecretRegex().Match(text);
            if (secret.Success)
                yield return new("RS070", Severity.Error, path == "SKILL.md" ? SkillField.Body : SkillField.Files,
                    $"Possível segredo em {path} (\"{Mask(secret.Value)}\"). Skills são publicadas no npm e no GitHub.",
                    "Remova a credencial e use variável de ambiente.", path);
        }

        var body = d.Body ?? "";
        if (DestructiveRegex().Match(body) is { Success: true } destructive)
            yield return new("RS071", Severity.Warning, SkillField.Body,
                $"O corpo manda executar um comando destrutivo (\"{destructive.Value}\"). O Reversa promete nunca apagar arquivos do projeto.",
                "Restrinja a escrita a .reversa/ e à pasta de saída, ou peça confirmação explícita.");

        if (LegacyWriteRegex().IsMatch(body) && !body.Contains("reversa-config.json", StringComparison.Ordinal) &&
            !body.Contains("allowLegacyEdits", StringComparison.Ordinal))
            yield return new("RS072", Severity.Info, SkillField.Body,
                "O corpo fala em alterar código do projeto, mas não cita a política de edição do legado (.reversa/reversa-config.json, allowLegacyEdits).",
                "Diga ao agente para consultar a política antes de escrever fora de .reversa/ e _reversa_sdd/.");
    }

    private static string Mask(string s) => s.Length <= 8 ? "****" : s[..6] + "…";

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]{16,}|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"\brm\s+-rf\s+[^\s`]+|\bgit\s+(reset\s+--hard|clean\s+-[a-z]*f|push\s+(-f|--force))\b|\bRemove-Item\b[^\n`]*-Recurse", RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveRegex();

    [GeneratedRegex(@"(?i)\b(edit(e|ar|a)|alter(e|ar|a)|modifi(que|car|ca)|refator(e|ar|a))\b[^.\n]{0,40}\b(código|codigo|arquivos? do projeto|legado|source)")]
    private static partial Regex LegacyWriteRegex();
}

/// <summary>
/// RS090–RS099: verifica o que vai para o disco, não o formulário. Renderiza SKILL.md e openai.yaml
/// e aplica o porte do verificador oficial, garantindo paridade com o gate de CI.
/// </summary>
public sealed class PackageRules : IValidationRule
{
    public const long MaxTotalBytes = 512 * 1024;

    public IEnumerable<ValidationIssue> Validate(SkillDraft d, ValidationContext ctx)
    {
        var issues = new List<ValidationIssue>();
        string skillMd, yaml;
        try
        {
            skillMd = SkillSerializer.RenderSkillMd(d);
            yaml = SkillSerializer.RenderOpenAiYaml(d);
        }
        catch (Exception ex)
        {
            issues.Add(new("RS090", Severity.Error, SkillField.Package, $"Falha ao gerar os arquivos: {ex.Message}"));
            return issues;
        }

        var (frontmatter, _) = SkillSerializer.SplitFrontmatter(skillMd);
        if (frontmatter is null)
        {
            issues.Add(new("RS090", Severity.Error, SkillField.Package, "SKILL.md gerado sem frontmatter."));
            return issues;
        }

        var claudeUser = InvocationAxis.IsClaudeUserInvoked(frontmatter);
        var codexUser = InvocationAxis.IsCodexUserInvoked(yaml);
        if (claudeUser != codexUser)
            issues.Add(new("RS090", Severity.Error, SkillField.Invocation,
                $"DESCASAMENTO no eixo de invocação: Claude={(claudeUser ? "user" : "model")}, Codex={(codexUser ? "user" : "model")}."));
        if (!InvocationAxis.HasDisplayName(yaml))
            issues.Add(new("RS090", Severity.Error, SkillField.DisplayName, "openai.yaml sem interface.display_name."));
        if (!InvocationAxis.HasShortDescription(yaml))
            issues.Add(new("RS090", Severity.Error, SkillField.ShortDescription, "openai.yaml sem interface.short_description."));

        // Ida e volta: o que foi escrito precisa ser lido de volta igual (pega erros de aspas e escape).
        try
        {
            var back = SkillSerializer.Parse(skillMd, yaml);
            if (back.Name != d.Name.Trim())
                issues.Add(new("RS091", Severity.Error, SkillField.Name, "O nome não sobrevive à ida e volta pelo YAML."));
            if (back.Description != SkillSerializer.OneLine(d.Description))
                issues.Add(new("RS091", Severity.Error, SkillField.Description,
                    "A description não sobrevive à ida e volta pelo YAML (caracteres especiais).", "Evite aspas e dois-pontos soltos."));
            if (back.Invocation != d.Invocation)
                issues.Add(new("RS091", Severity.Error, SkillField.Invocation, "O eixo de invocação não sobrevive à ida e volta."));
        }
        catch (Exception ex)
        {
            issues.Add(new("RS091", Severity.Error, SkillField.Package, $"O frontmatter gerado não é YAML válido: {ex.Message}"));
        }

        var total = SkillSerializer.RenderAll(d).Sum(f => (long)SkillSerializer.Utf8NoBom.GetByteCount(f.Content));
        if (total > MaxTotalBytes)
            issues.Add(new("RS092", Severity.Warning, SkillField.Files,
                $"A skill soma {total / 1024} KB de texto. Ela é copiada para cada engine em cada projeto instalado."));

        return issues;
    }
}
