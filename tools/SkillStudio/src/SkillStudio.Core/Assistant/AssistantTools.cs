using System.Text.Json;
using System.Text.Json.Nodes;
using SkillStudio.Core.Docs;
using SkillStudio.Core.Evals;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;
using SkillStudio.Core.Search;
using SkillStudio.Core.Validation;

namespace SkillStudio.Core.Assistant;

/// <summary>
/// Implementação local das ferramentas do assistente. Independente do SDK para ser testável:
/// recebe o input como JSON e devolve o texto do tool_result.
/// </summary>
public sealed class AssistantTools(SkillRepository repository, Func<SkillSearchIndex> index, IDraftHost host, IEvalHost? evals = null,
    DocsStore? docs = null, Action? docsChanged = null)
{
    public const string SearchSimilar = "search_similar_skills";
    public const string ReadSkill = "read_skill";
    public const string GetCurrentDraft = "get_current_draft";
    public const string UpdateDraft = "update_draft";
    public const string GetTestCases = "get_test_cases";
    public const string UpdateTestCases = "update_test_cases";
    public const string GetDocsPages = "get_docs_pages";
    public const string UpdateDocsPages = "update_docs_pages";

    private const int MaxReadChars = 60_000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public sealed record ToolOutcome(string Content, bool IsError, string Activity);

    public ToolOutcome Execute(string name, JsonObject input)
    {
        try
        {
            return name switch
            {
                SearchSimilar => DoSearch(input),
                ReadSkill => DoRead(input),
                GetCurrentDraft => DoGetDraft(),
                UpdateDraft => DoUpdate(input),
                GetTestCases => DoGetCases(),
                UpdateTestCases => DoUpdateCases(input),
                GetDocsPages => DoGetDocs(),
                UpdateDocsPages => DoUpdateDocs(input),
                _ => new ToolOutcome($"Ferramenta desconhecida: {name}", true, $"ferramenta desconhecida {name}"),
            };
        }
        catch (ToolInputException ex)
        {
            return new ToolOutcome($"Entrada inválida: {ex.Message}", true, $"entrada inválida em {name}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException && name is GetDocsPages or UpdateDocsPages)
        {
            return new ToolOutcome($"Entrada inválida: {ex.Message}", true, $"entrada inválida em {name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return new ToolOutcome($"Erro: {ex.Message}", true, $"erro em {name}");
        }
    }

    private ToolOutcome DoSearch(JsonObject input)
    {
        var query = RequiredString(input, "query");
        var limit = Math.Clamp(OptionalInt(input, "limit") ?? 5, 1, 10);
        var hits = index().Search(query, limit, host.GetDraft().OriginalName);
        var payload = hits.Select(h => new
        {
            name = h.Name,
            score = h.Score,
            invocation = h.Invocation == InvocationMode.User ? "user" : "model",
            team = h.Team,
            description = h.Description,
            trecho = h.Snippet,
        });
        return new ToolOutcome(
            hits.Count == 0 ? "Nenhuma skill parecida encontrada." : JsonSerializer.Serialize(payload, Json),
            false,
            $"buscou skills parecidas com \"{TextUtil.Truncate(query, 50)}\" ({hits.Count} resultado(s))");
    }

    private ToolOutcome DoRead(JsonObject input)
    {
        var name = RequiredString(input, "name");
        if (!repository.ListNames().Contains(name))
            return new ToolOutcome($"Skill \"{name}\" não existe. Use search_similar_skills para achar o nome exato.", true, $"tentou ler {name}");

        var draft = repository.Load(name);
        var includeRefs = input["include_references"]?.GetValue<bool>() ?? false;
        var text = new System.Text.StringBuilder();
        text.Append("=== SKILL.md ===\n").Append(SkillSerializer.RenderSkillMd(draft));
        text.Append("\n=== agents/openai.yaml ===\n").Append(SkillSerializer.RenderOpenAiYaml(draft));
        text.Append("\n=== arquivos auxiliares ===\n");
        foreach (var f in draft.Files) text.Append("- ").Append(f.RelativePath).Append(f.IsBinary ? " (binário)" : "").Append('\n');
        if (includeRefs)
            foreach (var f in draft.Files.Where(f => !f.IsBinary && f.RelativePath.StartsWith("references/", StringComparison.Ordinal)))
            {
                if (text.Length > MaxReadChars) { text.Append("\n[demais referências omitidas pelo limite de tamanho]\n"); break; }
                text.Append("\n=== ").Append(f.RelativePath).Append(" ===\n").Append(f.Content);
            }
        return new ToolOutcome(
            "<referencia_somente_leitura>\n" + text + "\n</referencia_somente_leitura>",
            false,
            $"leu a skill {name}{(includeRefs ? " com referências" : "")}");
    }

    private ToolOutcome DoGetDraft()
    {
        var d = host.GetDraft();
        var report = host.Validate(d);
        return new ToolOutcome(JsonSerializer.Serialize(DescribeDraft(d, report), Json), false, "consultou o rascunho atual");
    }

    private ToolOutcome DoUpdate(JsonObject input)
    {
        var patch = new DraftPatch
        {
            Name = OptionalString(input, "name"),
            Description = OptionalString(input, "description"),
            DisplayName = OptionalString(input, "display_name"),
            ShortDescription = OptionalString(input, "short_description"),
            License = OptionalString(input, "license"),
            Compatibility = OptionalString(input, "compatibility"),
            Body = OptionalString(input, "body"),
        };
        if (OptionalString(input, "invocation") is { } inv)
            patch.Invocation = inv switch
            {
                "user" => InvocationMode.User,
                "model" => InvocationMode.Model,
                _ => throw new ToolInputException("invocation deve ser \"user\" ou \"model\"."),
            };
        if (input["metadata"] is JsonObject meta)
        {
            patch.Author = OptionalString(meta, "author");
            patch.Version = OptionalString(meta, "version");
            patch.Phase = OptionalString(meta, "phase");
            patch.Team = OptionalString(meta, "team");
            patch.Role = OptionalString(meta, "role");
            patch.Stage = OptionalString(meta, "stage");
        }
        if (input["reference_files"] is JsonArray files)
            foreach (var item in files.OfType<JsonObject>())
            {
                var path = SkillSerializer.NormalizeRelativePath(RequiredString(item, "path"));
                if (path is SkillSerializer.SkillFileName or SkillSerializer.OpenAiYamlPath)
                    throw new ToolInputException($"{path} é gerado pelo editor; use os campos.");
                patch.UpsertFiles.Add(new SkillFile { RelativePath = path, Content = RequiredString(item, "content") });
            }
        if (input["remove_files"] is JsonArray removals)
            patch.RemoveFiles.AddRange(removals.Select(r => SkillSerializer.NormalizeRelativePath(r?.GetValue<string>() ?? "")).Where(p => p.Length > 0));

        var changed = patch.ChangedFields().ToList();
        if (changed.Count == 0)
            throw new ToolInputException("nenhum campo para atualizar.");

        var updated = host.ApplyPatch(patch);
        var report = host.Validate(updated);
        var result = new
        {
            atualizado = changed,
            erros = report.ErrorCount,
            avisos = report.WarningCount,
            pode_salvar = report.CanSave,
            problemas = report.Issues.Where(i => i.Severity != Severity.Info).Select(FormatIssue),
            sugestoes = report.Issues.Where(i => i.Severity == Severity.Info).Select(FormatIssue),
        };
        return new ToolOutcome(JsonSerializer.Serialize(result, Json), false,
            $"preencheu {string.Join(", ", changed)} · {report.ErrorCount} erro(s), {report.WarningCount} aviso(s)");
    }

    // ------------------------------------------------------------------ casos de teste (Playground)

    private IEvalHost RequireEvals() =>
        evals ?? throw new ToolInputException("o Playground não está disponível nesta sessão.");

    private ToolOutcome DoGetCases()
    {
        var suite = RequireEvals().GetSuite();
        var payload = suite.Cases.Select(DescribeCase);
        return new ToolOutcome(suite.Cases.Count == 0 ? "A skill ainda não tem casos de teste." : JsonSerializer.Serialize(payload, Json),
            false, $"consultou os casos de teste ({suite.Cases.Count})");
    }

    private ToolOutcome DoUpdateCases(JsonObject input)
    {
        var evalHost = RequireEvals();
        var upserts = new List<EvalCase>();
        if (input["cases"] is JsonArray cases)
            foreach (var item in cases.OfType<JsonObject>())
            {
                var kind = RequiredString(item, "kind") switch
                {
                    "trigger" => EvalKind.Trigger,
                    "behavior" => EvalKind.Behavior,
                    _ => throw new ToolInputException("kind deve ser \"trigger\" ou \"behavior\"."),
                };
                var c = new EvalCase
                {
                    Id = OptionalString(item, "id") ?? "",
                    Title = OptionalString(item, "title") ?? "",
                    Kind = kind,
                    Prompt = RequiredString(item, "prompt"),
                    Notes = OptionalString(item, "notes"),
                };
                if (kind == EvalKind.Trigger)
                    c.ShouldTrigger = item["should_trigger"] is JsonValue v && v.TryGetValue<bool>(out var b)
                        ? b
                        : throw new ToolInputException("casos trigger precisam de should_trigger (true/false).");
                if (item["assertions"] is JsonArray asserts)
                    foreach (var a in asserts.OfType<JsonObject>())
                        c.Assertions.Add(new EvalAssertion
                        {
                            Kind = RequiredString(a, "kind") switch
                            {
                                "contains" => AssertionKind.Contains,
                                "not_contains" => AssertionKind.NotContains,
                                "regex" => AssertionKind.Regex,
                                "rubric" => AssertionKind.Rubric,
                                _ => throw new ToolInputException("assertion.kind deve ser contains, not_contains, regex ou rubric."),
                            },
                            Value = RequiredString(a, "value"),
                        });
                if (kind == EvalKind.Behavior && c.Assertions.Count == 0)
                    throw new ToolInputException($"o caso \"{c.Title}\" é de comportamento e precisa de ao menos uma asserção.");
                upserts.Add(c);
            }
        var removals = input["remove_ids"] is JsonArray ids
            ? ids.Select(i => i?.GetValue<string>() ?? "").Where(i => i.Length > 0).ToList()
            : [];
        if (upserts.Count == 0 && removals.Count == 0)
            throw new ToolInputException("nenhum caso para criar, alterar ou remover.");

        var suite = evalHost.ApplyCases(upserts, removals);
        var draft = host.GetDraft();
        var report = host.Validate(draft);
        var testIssues = report.Issues.Where(i => i.Field == SkillField.Tests).Select(FormatIssue).ToList();
        var result = new { total = suite.Cases.Count, casos = suite.Cases.Select(c => new { c.Id, c.Title }), problemas = testIssues };
        return new ToolOutcome(JsonSerializer.Serialize(result, Json), false,
            $"{(upserts.Count > 0 ? $"gravou {upserts.Count} caso(s) de teste" : "")}{(upserts.Count > 0 && removals.Count > 0 ? " e " : "")}{(removals.Count > 0 ? $"removeu {removals.Count}" : "")}");
    }

    // ------------------------------------------------------------------ página do site (docs/agentes/)

    /// <summary>Páginas que servem de modelo de formato, na ordem de preferência.</summary>
    private static readonly string[] DocsExamples = ["fg-backlog", "n8n", "scout"];

    private DocsStore RequireDocs() =>
        docs is { IsAvailable: true } d ? d : throw new ToolInputException("o repositório aberto não tem o site de documentação (docs/agentes/ e mkdocs.yml).");

    /// <summary>A página é ligada à skill salva: um nome ainda não gravado criaria páginas órfãs.</summary>
    private string RequireSavedForkSkill()
    {
        var d = host.GetDraft();
        var name = d.Name.Trim();
        if (string.IsNullOrEmpty(d.OriginalName) || d.OriginalName != name)
            throw new ToolInputException("salve a skill antes de documentá-la: a página usa o nome da pasta em agents/.");
        if (name.StartsWith("reversa", StringComparison.Ordinal))
            throw new ToolInputException("skills do upstream já têm páginas com nomes próprios; o gerador é para skills do fork.");
        return name;
    }

    private ToolOutcome DoGetDocs()
    {
        var store = RequireDocs();
        var skill = RequireSavedForkSkill();
        var status = store.Status(skill);
        var example = DocsExamples.Where(e => e != skill).Select(e => (Name: e, Pages: store.LoadPages(e)))
            .FirstOrDefault(e => e.Pages.Count == DocsStore.Languages.Count);
        var payload = new
        {
            skill,
            arquivos = DocsStore.Languages.ToDictionary(l => l.Code, l => DocsStore.RelativePagePath(skill, l.Code)),
            presentes = status.PresentLanguages,
            faltando = status.MissingLanguages,
            no_menu = status.InNav,
            paginas_atuais = store.LoadPages(skill),
            exemplo = example.Name is null ? null : new { skill = example.Name, paginas = example.Pages },
        };
        return new ToolOutcome("<referencia_somente_leitura>\n" + JsonSerializer.Serialize(payload, Json) + "\n</referencia_somente_leitura>",
            false, $"consultou a documentação de {skill} ({status.PresentLanguages.Count} de {DocsStore.Languages.Count} idiomas)");
    }

    private ToolOutcome DoUpdateDocs(JsonObject input)
    {
        var store = RequireDocs();
        var skill = RequireSavedForkSkill();
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input["pages"] is JsonObject p)
            foreach (var l in DocsStore.Languages)
                if (OptionalString(p, l.Code) is { Length: > 0 } content)
                    pages[l.Code] = content;
        if (pages.Count == 0)
            throw new ToolInputException("informe ao menos uma página em pages (en, pt ou es).");

        Dictionary<string, DocsIndexRow>? rows = null;
        if (input["index_rows"] is JsonObject idx)
        {
            rows = [];
            foreach (var l in DocsStore.Languages)
                if (idx[l.Code] is JsonObject row)
                    rows[l.Code] = new DocsIndexRow(RequiredString(row, "analogy"), RequiredString(row, "when_to_use"));
        }

        var overwrite = input["overwrite"] is JsonValue ov && ov.TryGetValue<bool>(out var o) && o;
        var result = store.Save(skill, pages, OptionalString(input, "nav_title"), rows, overwrite);
        docsChanged?.Invoke();
        var status = store.Status(skill);
        var payload = new
        {
            gravados = result.WrittenFiles,
            menu_atualizado = result.NavAdded,
            indice_atualizado = result.IndexRowsAdded,
            faltando = status.MissingLanguages,
            no_menu = status.InNav,
        };
        return new ToolOutcome(JsonSerializer.Serialize(payload, Json), false,
            $"gravou a documentação de {skill} ({string.Join(", ", pages.Keys)}){(result.NavAdded ? " e a registrou no menu" : "")}");
    }

    private static object DescribeCase(EvalCase c) => new
    {
        id = c.Id,
        title = c.Title,
        kind = c.Kind == EvalKind.Trigger ? "trigger" : "behavior",
        prompt = c.Prompt,
        should_trigger = c.ShouldTrigger,
        assertions = c.Assertions.Select(a => new { kind = AssertionKindName(a.Kind), value = a.Value }),
        notes = c.Notes,
    };

    private static string AssertionKindName(AssertionKind k) => k switch
    {
        AssertionKind.Contains => "contains",
        AssertionKind.NotContains => "not_contains",
        AssertionKind.Regex => "regex",
        _ => "rubric",
    };

    public static object DescribeDraft(SkillDraft d, ValidationReport report) => new
    {
        skill_existente = d.OriginalName,
        name = d.Name,
        description = d.Description,
        invocation = d.Invocation == InvocationMode.User ? "user" : "model",
        display_name = d.DisplayName,
        short_description = d.ShortDescription,
        license = d.License,
        metadata = new { d.Metadata.Author, d.Metadata.Version, d.Metadata.Framework, d.Metadata.Phase, d.Metadata.Team, d.Metadata.Role, d.Metadata.Stage },
        body = d.Body,
        arquivos = d.Files.Select(f => new { path = f.RelativePath, binario = f.IsBinary, caracteres = f.Content?.Length ?? 0 }),
        validacao = new { erros = report.ErrorCount, avisos = report.WarningCount, problemas = report.Issues.Select(FormatIssue) },
    };

    private static string FormatIssue(ValidationIssue i) =>
        $"{i.RuleId} [{i.Severity}] ({i.Field}) {i.Message}" + (i.Suggestion is null ? "" : $" Sugestão: {i.Suggestion}");

    private static string RequiredString(JsonObject o, string key) =>
        OptionalString(o, key) is { Length: > 0 } s ? s : throw new ToolInputException($"\"{key}\" é obrigatório.");

    private static string? OptionalString(JsonObject o, string key)
    {
        var node = o[key];
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        throw new ToolInputException($"\"{key}\" deve ser texto.");
    }

    private static int? OptionalInt(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private sealed class ToolInputException(string message) : Exception(message);

    // ------------------------------------------------------------------ esquemas

    public static IReadOnlyList<(string Name, string Description, string Schema)> Definitions { get; } =
    [
        (SearchSimilar,
         "Busca, entre as skills do repositório Reversa, as mais parecidas com um texto (objetivo, tema ou description). " +
         "Use logo que entender o objetivo da skill nova, para achar referências de estrutura e convenção. " +
         "Devolve nome, score, modo de invocação, time, description e um trecho relevante.",
         """
         {"type":"object","properties":{
           "query":{"type":"string","description":"Texto livre descrevendo o que a skill faz."},
           "limit":{"type":"integer","description":"Quantidade de resultados (1 a 10). Padrão 5."}},
          "required":["query"],"additionalProperties":false}
         """),
        (ReadSkill,
         "Lê uma skill existente do repositório (SKILL.md, openai.yaml e a lista de arquivos). " +
         "Use nas uma ou duas skills mais parecidas para espelhar estrutura e estilo. include_references traz o conteúdo de references/.",
         """
         {"type":"object","properties":{
           "name":{"type":"string","description":"Nome exato da skill (pasta em agents/)."},
           "include_references":{"type":"boolean","description":"Se true, inclui o conteúdo dos arquivos em references/."}},
          "required":["name"],"additionalProperties":false}
         """),
        (GetCurrentDraft,
         "Devolve o rascunho aberto no editor (todos os campos, arquivos e o resultado da validação). " +
         "Use no início e sempre que a pessoa disser que editou algo à mão.",
         """{"type":"object","properties":{},"additionalProperties":false}"""),
        (UpdateDraft,
         "Preenche ou altera campos do rascunho no editor. Envie só os campos que mudam. " +
         "Devolve a validação completa: corrija os erros antes de encerrar. " +
         "body é o Markdown das instruções, sem frontmatter. reference_files cria ou substitui arquivos auxiliares " +
         "(ex.: references/checklist.md), que precisam ser citados no body pelo mesmo caminho.",
         """
         {"type":"object","properties":{
           "name":{"type":"string","description":"kebab-case com o prefixo do fork."},
           "description":{"type":"string","description":"Uma a três frases. User-invoked: resumo humano sem gatilhos. Model-invoked: o que faz e quando usar."},
           "invocation":{"type":"string","enum":["user","model"]},
           "display_name":{"type":"string","description":"Nome curto para menus, ex.: FG Auditoria LGPD."},
           "short_description":{"type":"string","description":"Até ~60 caracteres. Vazio = gerado da description."},
           "license":{"type":"string"},
           "compatibility":{"type":"string"},
           "metadata":{"type":"object","properties":{
               "author":{"type":"string"},"version":{"type":"string","description":"SemVer, ex.: 1.0.0"},
               "phase":{"type":"string"},"team":{"type":"string"},"role":{"type":"string"},"stage":{"type":"string"}},
             "additionalProperties":false},
           "body":{"type":"string","description":"Instruções completas em Markdown (substitui o corpo atual)."},
           "reference_files":{"type":"array","items":{"type":"object","properties":{
               "path":{"type":"string","description":"Caminho relativo, ex.: references/template.md"},
               "content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}},
           "remove_files":{"type":"array","items":{"type":"string"}}},
          "additionalProperties":false}
         """),
        (GetTestCases,
         "Devolve os casos de teste do Playground para a skill aberta (pedido, tipo e asserções).",
         """{"type":"object","properties":{},"additionalProperties":false}"""),
        (UpdateTestCases,
         "Cria, altera (mesmo id) ou remove casos de teste do Playground. Não executa os testes: quem roda é a pessoa, no Playground. " +
         "trigger: pedido realista e should_trigger (true = deve acionar a skill; false = pedido parecido que NÃO deve). Só para skills model-invoked. " +
         "behavior: pedido que exercita a skill, com o contexto necessário embutido (o teste não tem acesso a arquivos), " +
         "e asserções verificáveis só pela resposta: rubric (critério objetivo em uma frase), contains, not_contains ou regex.",
         """
         {"type":"object","properties":{
           "cases":{"type":"array","items":{"type":"object","properties":{
               "id":{"type":"string","description":"Omitir para caso novo; informar para substituir um existente."},
               "title":{"type":"string","description":"Rótulo curto, ex.: Caminho feliz com backlog vazio."},
               "kind":{"type":"string","enum":["trigger","behavior"]},
               "prompt":{"type":"string","description":"O pedido exatamente como o usuário digitaria."},
               "should_trigger":{"type":"boolean"},
               "assertions":{"type":"array","items":{"type":"object","properties":{
                   "kind":{"type":"string","enum":["rubric","contains","not_contains","regex"]},
                   "value":{"type":"string"}},"required":["kind","value"],"additionalProperties":false}},
               "notes":{"type":"string","description":"Por que o caso existe (o que ele protege)."}},
             "required":["kind","prompt"],"additionalProperties":false}},
           "remove_ids":{"type":"array","items":{"type":"string"}}},
          "additionalProperties":false}
         """),
        (GetDocsPages,
         "Devolve a situação da página da skill aberta no site de documentação (docs/agentes/<skill>.md em inglês, " +
         ".pt.md e .es.md), o conteúdo atual das páginas que existem, se ela está no menu do site e uma página de outra " +
         "skill nos três idiomas como modelo de formato. A skill precisa estar salva.",
         """{"type":"object","properties":{},"additionalProperties":false}"""),
        (UpdateDocsPages,
         "Grava a página da skill aberta no site de documentação e, na primeira vez, a registra no menu (grupo Fork Agents) " +
         "e no índice de agentes. Diferente de update_draft, grava direto no disco. Cada página segue o modelo de " +
         "get_docs_pages: título, comando e fase, uma analogia, o que faz, o que produz e quando usar. As três versões " +
         "dizem a mesma coisa, cada uma no seu idioma. Página que já existe só é substituída com overwrite=true.",
         """
         {"type":"object","properties":{
           "pages":{"type":"object","properties":{
               "en":{"type":"string","description":"Página em inglês, o idioma padrão do site (Markdown começando com # )."},
               "pt":{"type":"string","description":"Página em português do Brasil."},
               "es":{"type":"string","description":"Página em espanhol."}},
             "additionalProperties":false},
           "nav_title":{"type":"string","description":"Título no menu, em inglês, ex.: Backlog (fg-backlog). Obrigatório se a skill ainda não está no menu."},
           "index_rows":{"type":"object","description":"Linha da tabela de agentes do fork no índice de cada idioma.","properties":{
               "en":{"type":"object","properties":{"analogy":{"type":"string","description":"A analogia da página, ex.: O gerente de produto com a planilha."},"when_to_use":{"type":"string","description":"Uma frase: quando usar a skill."}},"required":["analogy","when_to_use"],"additionalProperties":false},
               "pt":{"type":"object","properties":{"analogy":{"type":"string","description":"A analogia da página, ex.: O gerente de produto com a planilha."},"when_to_use":{"type":"string","description":"Uma frase: quando usar a skill."}},"required":["analogy","when_to_use"],"additionalProperties":false},
               "es":{"type":"object","properties":{"analogy":{"type":"string","description":"A analogia da página, ex.: O gerente de produto com a planilha."},"when_to_use":{"type":"string","description":"Uma frase: quando usar a skill."}},"required":["analogy","when_to_use"],"additionalProperties":false}},
             "additionalProperties":false},
           "overwrite":{"type":"boolean","description":"true para substituir páginas que já existem."}},
          "required":["pages"],"additionalProperties":false}
         """),
    ];
}
