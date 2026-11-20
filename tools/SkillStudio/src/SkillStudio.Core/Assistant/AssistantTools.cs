using System.Text.Json;
using System.Text.Json.Nodes;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;
using SkillStudio.Core.Search;
using SkillStudio.Core.Validation;

namespace SkillStudio.Core.Assistant;

/// <summary>
/// Implementação local das ferramentas do assistente. Independente do SDK para ser testável:
/// recebe o input como JSON e devolve o texto do tool_result.
/// </summary>
public sealed class AssistantTools(SkillRepository repository, Func<SkillSearchIndex> index, IDraftHost host)
{
    public const string SearchSimilar = "search_similar_skills";
    public const string ReadSkill = "read_skill";
    public const string GetCurrentDraft = "get_current_draft";
    public const string UpdateDraft = "update_draft";

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
                _ => new ToolOutcome($"Ferramenta desconhecida: {name}", true, $"ferramenta desconhecida {name}"),
            };
        }
        catch (ToolInputException ex)
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
    ];
}
