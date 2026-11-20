using System.Text.Json.Nodes;
using SkillStudio.Core.Assistant;
using SkillStudio.Core.Docs;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;
using SkillStudio.Core.Validation;

namespace SkillStudio.Tests;

/// <summary>Páginas do site (docs/agentes/), regras RS110–RS112 e ferramentas de documentação do assistente.</summary>
public sealed class DocsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillstudio-docs-" + Guid.NewGuid().ToString("N"));

    private const string MkDocs = """
        site_name: Reversa
        plugins:
          - i18n:
              languages:
                - locale: en
                  default: true
                - locale: pt
                  nav_translations:
                    Agents: Agentes
                - locale: es
                  nav_translations:
                    Agents: Agentes

        nav:
          - Home: index.md
          - Agents:
            - Overview: agentes/index.md
            - Translators:
              - N8N Translator: agentes/n8n.md
          - Contributing: contribuindo.md

        """;

    public DocsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "agentes"));
        File.WriteAllText(Path.Combine(_root, "mkdocs.yml"), MkDocs.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(_root, "docs", "agentes", "index.md"),
            "# Agents\n\n## Fork agents\n\n| Agent | Analogy | When to use |\n|---|---|---|\n\n---\n\n## Recommended sequence\n");
        File.WriteAllText(Path.Combine(_root, "docs", "agentes", "index.pt.md"), "# Agentes\n\n## Sequência recomendada\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static Dictionary<string, string> Pages(string title = "Teste") => new()
    {
        ["en"] = $"# {title}\n\nEnglish.\n",
        ["pt"] = $"# {title}\n\nPortuguês.\n",
        ["es"] = $"# {title}\n\nEspañol.\n",
    };

    private string Mk => File.ReadAllText(Path.Combine(_root, "mkdocs.yml"));

    [Fact]
    public void Save_writes_pages_and_creates_the_fork_group_once()
    {
        var store = new DocsStore(_root);
        Assert.True(store.IsAvailable);
        Assert.False(store.Status("fg-teste").HasAnyPage);

        var result = store.Save("fg-teste", Pages(), "Test (fg-teste)");
        Assert.True(result.NavAdded);
        Assert.Contains("docs/agentes/fg-teste.pt.md", result.WrittenFiles);
        Assert.True(store.Status("fg-teste").IsComplete);
        Assert.Equal("# Teste\n\nEspañol.\n", File.ReadAllText(Path.Combine(_root, "docs", "agentes", "fg-teste.es.md")));

        // grupo novo dentro de Agents, antes da próxima seção de topo, com as traduções
        var mk = Mk;
        Assert.Contains("      - N8N Translator: agentes/n8n.md\n    - Fork Agents:\n      - Test (fg-teste): agentes/fg-teste.md\n  - Contributing:", mk);
        Assert.Contains("            Fork Agents: Agentes do fork\n", mk);
        Assert.Contains("            Fork Agents: Agentes del fork\n", mk);

        // segunda skill entra no grupo existente, sem duplicar grupo nem traduções
        store.Save("fg-outra", Pages(), "Other: second");
        mk = Mk;
        Assert.Contains("      - Test (fg-teste): agentes/fg-teste.md\n      - \"Other: second\": agentes/fg-outra.md\n", mk);
        Assert.Single(mk.Split('\n'), l => l == "    - Fork Agents:");
        Assert.Equal(2, mk.Split('\n').Count(l => l.Trim().StartsWith("Fork Agents: ", StringComparison.Ordinal)));

        // regravar com overwrite não mexe no nav
        var again = store.Save("fg-teste", Pages("Novo"), null, overwrite: true);
        Assert.False(again.NavAdded);
        Assert.Equal(3, again.WrittenFiles.Count);
        Assert.Equal(mk, Mk);
    }

    [Fact]
    public void Save_guards_existing_pages_and_bad_input()
    {
        var store = new DocsStore(_root);
        store.Save("fg-teste", new Dictionary<string, string> { ["pt"] = "# T\n" }, "T");

        Assert.Throws<InvalidOperationException>(() => store.Save("fg-teste", new Dictionary<string, string> { ["pt"] = "# X\n" }, null));
        Assert.Throws<ArgumentException>(() => store.Save("fg-teste", new Dictionary<string, string> { ["fr"] = "# X\n" }, null));
        Assert.Throws<ArgumentException>(() => store.Save("fg-teste", new Dictionary<string, string> { ["en"] = "sem título" }, null));
        Assert.Throws<ArgumentException>(() => store.Save("fg-nova", new Dictionary<string, string> { ["en"] = "# X\n" }, " "));
        Assert.Throws<ArgumentException>(() => store.Status("../fora"));

        var status = store.Status("fg-teste");
        Assert.Equal(["pt"], status.PresentLanguages);
        Assert.Equal(["en", "es"], status.MissingLanguages);
        Assert.True(status.InNav);
    }

    [Fact]
    public void Index_row_is_added_only_where_the_fork_section_exists()
    {
        var store = new DocsStore(_root);
        var rows = new Dictionary<string, DocsIndexRow>
        {
            ["en"] = new("The tester | QA", "When testing"),
            ["pt"] = new("O testador", "Ao testar"),
        };
        var result = store.Save("fg-teste", Pages(), "Test", rows);
        Assert.Equal(["en"], result.IndexRowsAdded);

        var index = File.ReadAllText(Path.Combine(_root, "docs", "agentes", "index.md"));
        Assert.Contains("|---|---|---|\n| [Test](fg-teste.md) | The tester \\| QA | When testing |\n\n---", index);

        Assert.Empty(store.Save("fg-teste", Pages(), null, rows, overwrite: true).IndexRowsAdded); // não duplica
        Assert.Equal("# Agentes\n\n## Sequência recomendada\n", File.ReadAllText(Path.Combine(_root, "docs", "agentes", "index.pt.md")));
    }

    // ------------------------------------------------------------------ regras

    private static IReadOnlyList<ValidationIssue> DocIssues(string name, DocsStatus? docs, string prefix = "fg-") =>
        new SkillValidator().Validate(new SkillDraft { Name = name },
            new ValidationContext { PreferredPrefix = prefix }.WithDocs(docs)).Issues.Where(i => i.Field == SkillField.Docs).ToList();

    [Fact]
    public void Docs_rules_cover_missing_partial_and_unlisted_pages()
    {
        Assert.Empty(DocIssues("fg-teste", null));
        Assert.Equal(["RS110"], DocIssues("fg-teste", new("fg-teste", [], ["en", "pt", "es"], false)).Select(i => i.RuleId));
        Assert.Equal(["RS111", "RS112"], DocIssues("fg-teste", new("fg-teste", ["pt"], ["en", "es"], false)).Select(i => i.RuleId).Order());
        Assert.Empty(DocIssues("fg-teste", new("fg-teste", ["en", "pt", "es"], [], true)));

        // fora do prefixo do fork (upstream, ou prefixo não configurado): silêncio
        Assert.Empty(DocIssues("reversa-scout", new("reversa-scout", [], ["en", "pt", "es"], false)));
        Assert.Empty(DocIssues("impeccable", new("impeccable", [], ["en", "pt", "es"], false)));
        Assert.Empty(DocIssues("fg-teste", new("fg-teste", [], ["en", "pt", "es"], false), prefix: ""));
        Assert.All(DocIssues("fg-teste", new("fg-teste", ["en"], ["pt", "es"], false)), i => Assert.NotEqual(Severity.Error, i.Severity));
    }

    // ------------------------------------------------------------------ ferramentas do assistente

    private sealed class Host(SkillDraft draft) : IDraftHost
    {
        public SkillDraft GetDraft() => draft;
        public SkillDraft ApplyPatch(DraftPatch patch) => draft;
        public ValidationReport Validate(SkillDraft d) => new SkillValidator().Validate(d);
    }

    private static SkillDraft Saved(string name, string? original) => new() { Name = name, OriginalName = original };

    [Fact]
    public void Assistant_reads_and_writes_the_docs_pages()
    {
        var store = new DocsStore(_root);
        store.Save("fg-backlog", Pages("Exemplo"), "Example"); // primeiro modelo da lista de exemplos
        var changed = 0;
        var tools = new AssistantTools(null!, null!, new Host(Saved("fg-teste", "fg-teste")), null, store, () => changed++);

        var read = tools.Execute(AssistantTools.GetDocsPages, []);
        Assert.False(read.IsError);
        Assert.Contains("\"faltando\":[\"en\",\"pt\",\"es\"]", read.Content);
        Assert.Contains("\"exemplo\":{\"skill\":\"fg-backlog\"", read.Content);
        Assert.StartsWith("<referencia_somente_leitura>", read.Content);

        var input = new JsonObject
        {
            ["pages"] = new JsonObject { ["en"] = "# Test\n", ["pt"] = "# Teste\n", ["es"] = "# Prueba\n" },
            ["nav_title"] = "Test (fg-teste)",
            ["index_rows"] = new JsonObject { ["en"] = new JsonObject { ["analogy"] = "The tester", ["when_to_use"] = "Always" } },
        };
        var write = tools.Execute(AssistantTools.UpdateDocsPages, input);
        Assert.False(write.IsError, write.Content);
        Assert.Equal(1, changed);
        Assert.Contains("\"menu_atualizado\":true", write.Content);
        Assert.True(store.Status("fg-teste").IsComplete);

        // página existente sem overwrite: erro legível, nada gravado
        var again = tools.Execute(AssistantTools.UpdateDocsPages, new JsonObject { ["pages"] = new JsonObject { ["pt"] = "# Outro\n" } });
        Assert.True(again.IsError);
        Assert.Contains("overwrite", again.Content);
        Assert.Equal("# Teste\n", File.ReadAllText(Path.Combine(_root, "docs", "agentes", "fg-teste.pt.md")));
    }

    [Fact]
    public void Assistant_refuses_docs_for_unsaved_or_upstream_skills()
    {
        var store = new DocsStore(_root);
        var input = new JsonObject { ["pages"] = new JsonObject { ["en"] = "# X\n" }, ["nav_title"] = "X" };

        Assert.True(new AssistantTools(null!, null!, new Host(Saved("fg-nova", null)), null, store).Execute(AssistantTools.UpdateDocsPages, input).IsError);
        Assert.True(new AssistantTools(null!, null!, new Host(Saved("fg-novo-nome", "fg-velho")), null, store).Execute(AssistantTools.GetDocsPages, []).IsError);
        Assert.True(new AssistantTools(null!, null!, new Host(Saved("reversa-x", "reversa-x")), null, store).Execute(AssistantTools.GetDocsPages, []).IsError);
        Assert.True(new AssistantTools(null!, null!, new Host(Saved("fg-teste", "fg-teste"))).Execute(AssistantTools.GetDocsPages, []).IsError);
        Assert.False(store.Status("fg-nova").HasAnyPage);
    }

    // ------------------------------------------------------------------ repositório real

    [Fact]
    public void Every_fork_skill_has_a_complete_docs_page()
    {
        var root = SkillRepository.FindRoot(AppContext.BaseDirectory)!;
        var store = new DocsStore(root);
        foreach (var skill in new SkillRepository(root).ListNames().Where(n => n.StartsWith("fg-", StringComparison.Ordinal)))
        {
            var status = store.Status(skill);
            Assert.True(status.IsComplete, $"{skill}: faltando {string.Join(", ", status.MissingLanguages)}; no menu: {status.InNav}");
        }
    }
}
