using SkillStudio.Core.IO;

namespace SkillStudio.Core.Docs;

/// <summary>Idioma do site (mkdocs-static-i18n com docs_structure: suffix).</summary>
public sealed record DocsLanguage(string Code, string Suffix, string IndexHeading, string ForkGroupLabel);

/// <summary>Situação da documentação de uma skill no site.</summary>
public sealed record DocsStatus(string Skill, IReadOnlyList<string> PresentLanguages, IReadOnlyList<string> MissingLanguages, bool InNav)
{
    public bool HasAnyPage => PresentLanguages.Count > 0;
    public bool IsComplete => MissingLanguages.Count == 0 && InNav;
}

/// <summary>Linha da tabela "Agentes do fork" no índice de agentes de um idioma.</summary>
public sealed record DocsIndexRow(string Analogy, string WhenToUse);

public sealed record DocsSaveResult(IReadOnlyList<string> WrittenFiles, bool NavAdded, IReadOnlyList<string> IndexRowsAdded);

/// <summary>
/// Páginas do site em docs/agentes/&lt;skill&gt;{,.pt,.es}.md, a entrada no nav do mkdocs.yml (grupo
/// "Fork Agents") e a linha no índice de agentes. Como evals/, fica fora de agents/ e do pacote npm.
/// As páginas do upstream usam nomes próprios (scout.md para reversa-scout), por isso o vínculo
/// skill → página só vale para skills do fork.
/// </summary>
public sealed class DocsStore(string repositoryRoot)
{
    public const string AgentsDocsFolder = "docs/agentes";
    public const string MkDocsFile = "mkdocs.yml";
    public const string ForkGroup = "Fork Agents";

    public static IReadOnlyList<DocsLanguage> Languages { get; } =
    [
        new("en", "", "## Fork agents", ""),
        new("pt", ".pt", "## Agentes do fork", "Agentes do fork"),
        new("es", ".es", "## Agentes del fork", "Agentes del fork"),
    ];

    public string Root { get; } = Path.GetFullPath(repositoryRoot);

    private string DocsDir => Path.Combine(Root, "docs", "agentes");

    public static string PageFileName(string skill, string lang) =>
        skill + (Languages.FirstOrDefault(l => l.Code == lang) ?? throw new ArgumentException($"Idioma desconhecido: {lang}")).Suffix + ".md";

    public static string RelativePagePath(string skill, string lang) => $"{AgentsDocsFolder}/{PageFileName(CheckName(skill), lang)}";

    /// <summary>Caminho usado no nav do mkdocs (sempre a página padrão, em inglês).</summary>
    public static string NavPath(string skill) => $"agentes/{skill}.md";

    public bool IsAvailable => Directory.Exists(DocsDir) && File.Exists(Path.Combine(Root, MkDocsFile));

    public DocsStatus Status(string skill)
    {
        CheckName(skill);
        var present = new List<string>();
        var missing = new List<string>();
        foreach (var l in Languages)
            (File.Exists(PagePath(skill, l.Code)) ? present : missing).Add(l.Code);
        return new DocsStatus(skill, present, missing, IsInNav(skill));
    }

    public IReadOnlyDictionary<string, string> LoadPages(string skill)
    {
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in Languages)
            if (File.Exists(PagePath(skill, l.Code)))
                pages[l.Code] = File.ReadAllText(PagePath(skill, l.Code));
        return pages;
    }

    public bool IsInNav(string skill)
    {
        var mk = Path.Combine(Root, MkDocsFile);
        return File.Exists(mk) && File.ReadAllText(mk).Contains(": " + NavPath(skill), StringComparison.Ordinal);
    }

    /// <summary>
    /// Grava as páginas informadas, registra a skill no nav (se ainda não estiver) e acrescenta as linhas
    /// do índice que faltarem. Páginas existentes só são substituídas com <paramref name="overwrite"/>.
    /// </summary>
    public DocsSaveResult Save(string skill, IReadOnlyDictionary<string, string> pages, string? navTitle,
        IReadOnlyDictionary<string, DocsIndexRow>? indexRows = null, bool overwrite = false)
    {
        CheckName(skill);
        foreach (var lang in pages.Keys)
            if (Languages.All(l => l.Code != lang)) throw new ArgumentException($"Idioma desconhecido: {lang}. Use en, pt ou es.");
        foreach (var (lang, content) in pages)
        {
            if (!content.TrimStart().StartsWith("# ", StringComparison.Ordinal))
                throw new ArgumentException($"A página {lang} precisa começar com um título \"# \".");
            if (!overwrite && File.Exists(PagePath(skill, lang)))
                throw new InvalidOperationException($"{RelativePagePath(skill, lang)} já existe. Leia a página atual e use overwrite para substituí-la.");
        }

        var written = new List<string>();
        foreach (var (lang, content) in pages)
            if (WriteIfChanged(PagePath(skill, lang), Normalize(content)))
                written.Add(RelativePagePath(skill, lang));

        var navAdded = false;
        if (!IsInNav(skill))
        {
            if (string.IsNullOrWhiteSpace(navTitle))
                throw new ArgumentException("A skill ainda não está no menu do site: informe nav_title.");
            navAdded = AddToNav(skill, navTitle.Trim());
            if (navAdded) written.Add(MkDocsFile);
        }

        var rows = new List<string>();
        if (indexRows is not null)
            foreach (var (lang, row) in indexRows)
                if (AddIndexRow(skill, lang, navTitle, row))
                    rows.Add(lang);
        return new DocsSaveResult(written, navAdded, rows);
    }

    // ------------------------------------------------------------------ mkdocs.yml

    private bool AddToNav(string skill, string title)
    {
        var path = Path.Combine(Root, MkDocsFile);
        var lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n').ToList();
        var entry = $"      - {YamlKey(title)}: {NavPath(skill)}";

        var group = lines.FindIndex(l => l == $"    - {ForkGroup}:");
        if (group >= 0)
        {
            var end = group + 1;
            while (end < lines.Count && lines[end].StartsWith("      ", StringComparison.Ordinal)) end++;
            lines.Insert(end, entry);
        }
        else
        {
            var agents = lines.FindIndex(l => l == "  - Agents:");
            if (agents < 0) throw new InvalidOperationException("mkdocs.yml não tem a seção \"  - Agents:\" no nav.");
            var end = agents + 1;
            while (end < lines.Count && lines[end].StartsWith("    ", StringComparison.Ordinal)) end++;
            lines.InsertRange(end, [$"    - {ForkGroup}:", entry]);
            AddGroupTranslations(lines);
        }
        File.WriteAllText(path, string.Join('\n', lines), SkillSerializer.Utf8NoBom);
        return true;
    }

    /// <summary>Traduz o nome do grupo novo em cada locale que tem nav_translations.</summary>
    private static void AddGroupTranslations(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var locale = lines[i].Trim() switch
            {
                "- locale: pt" => "pt",
                "- locale: es" => "es",
                _ => null,
            };
            if (locale is null) continue;
            var label = Languages.First(l => l.Code == locale).ForkGroupLabel;
            for (var j = i + 1; j < lines.Count && !lines[j].TrimStart().StartsWith("- locale:", StringComparison.Ordinal); j++)
                if (lines[j].Trim() == "nav_translations:")
                {
                    var indent = new string(' ', lines[j].Length - lines[j].TrimStart().Length + 2);
                    if (!lines.Skip(j + 1).TakeWhile(l => l.StartsWith(indent, StringComparison.Ordinal)).Any(l => l.Trim().StartsWith(ForkGroup + ":", StringComparison.Ordinal)))
                        lines.Insert(j + 1, $"{indent}{ForkGroup}: {label}");
                    break;
                }
        }
    }

    private static string YamlKey(string title) =>
        title.IndexOfAny([':', '#', '\'', '"', '{', '}', '[', ']', ',', '&', '*', '!', '|', '>', '%', '@', '`']) >= 0
            ? "\"" + title.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : title;

    // ------------------------------------------------------------------ índice de agentes

    private bool AddIndexRow(string skill, string lang, string? title, DocsIndexRow row)
    {
        var language = Languages.FirstOrDefault(l => l.Code == lang) ?? throw new ArgumentException($"Idioma desconhecido: {lang}");
        var path = Path.Combine(DocsDir, $"index{language.Suffix}.md");
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n').ToList();
        var heading = lines.FindIndex(l => l.Trim() == language.IndexHeading);
        if (heading < 0) return false; // seção do fork ainda não existe neste idioma: não inventa a estrutura
        var link = $"]({skill}.md)";
        var sectionEnd = lines.FindIndex(heading + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (sectionEnd < 0) sectionEnd = lines.Count;
        if (lines.Skip(heading).Take(sectionEnd - heading).Any(l => l.Contains(link, StringComparison.Ordinal))) return false;

        var lastRow = -1;
        for (var i = heading + 1; i < sectionEnd; i++)
            if (lines[i].StartsWith('|')) lastRow = i;
        if (lastRow < 0) return false;

        var label = string.IsNullOrWhiteSpace(title) ? skill : title.Trim();
        lines.Insert(lastRow + 1, $"| [{Cell(label)}]({skill}.md) | {Cell(row.Analogy)} | {Cell(row.WhenToUse)} |");
        File.WriteAllText(path, string.Join('\n', lines), SkillSerializer.Utf8NoBom);
        return true;
    }

    private static string Cell(string s) => s.ReplaceLineEndings(" ").Replace("|", "\\|").Trim();

    // ------------------------------------------------------------------ util

    private string PagePath(string skill, string lang) => Path.Combine(DocsDir, PageFileName(CheckName(skill), lang));

    private static string CheckName(string skill)
    {
        if (string.IsNullOrWhiteSpace(skill) || skill.IndexOfAny(['/', '\\', '.']) >= 0 || skill.Any(char.IsWhiteSpace))
            throw new ArgumentException("Nome de skill inválido para a página de documentação.");
        return skill;
    }

    private static string Normalize(string content) => content.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, SkillSerializer.Utf8NoBom);
        return true;
    }
}
