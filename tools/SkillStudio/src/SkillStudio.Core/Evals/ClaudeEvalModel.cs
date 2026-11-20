using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using SkillStudio.Core.Assistant;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;

namespace SkillStudio.Core.Evals;

public sealed class ClaudeEvalOptions
{
    public string Model { get; set; } = AssistantOptions.DefaultModel;

    /// <summary>Esforço da execução da skill e do teste de gatilho (o juiz sempre usa low).</summary>
    public string Effort { get; set; } = "medium";

    public string? ApiKey { get; set; }
    public int MaxTokens { get; set; } = 8000;
    public int MaxToolRounds { get; set; } = 6;
}

/// <summary>Implementação do <see cref="IEvalModel"/> sobre a API do Claude (endpoint beta, sem streaming).</summary>
public sealed class ClaudeEvalModel : IEvalModel
{
    public const string SkillToolName = "Skill";
    public const string ReadFileToolName = "read_skill_file";

    private readonly AnthropicClient _client;
    private readonly ClaudeEvalOptions _options;

    public ClaudeEvalModel(ClaudeEvalOptions options)
    {
        _options = options;
        _client = string.IsNullOrWhiteSpace(options.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.ApiKey };
    }

    // ------------------------------------------------------------------ gatilho

    internal static string TriggerSystem(IReadOnlyList<CatalogEntry> catalog)
    {
        var sb = new StringBuilder();
        sb.Append("""
            Você é um agente de programação trabalhando no repositório do usuário, com skills instaladas.
            Cada skill tem um nome e uma description que diz o que ela faz e quando usar.
            Quando o pedido do usuário corresponder a uma skill, chame a ferramenta Skill com o nome dela antes de qualquer outra coisa.
            Se nenhuma skill se aplicar, responda normalmente, sem chamar a ferramenta.

            Skills disponíveis:

            """);
        foreach (var e in catalog) sb.Append("- ").Append(e.Name).Append(": ").Append(e.Description).Append('\n');
        return sb.ToString();
    }

    public async Task<ModelCall<string?>> ChooseSkillAsync(IReadOnlyList<CatalogEntry> catalog, string prompt, CancellationToken ct)
    {
        var names = catalog.Select(c => c.Name).ToList();
        var schema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new { skill = new { type = "string", @enum = names, description = "Nome exato da skill." } },
            required = new[] { "skill" },
        });
        var p = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = 2000,
            System = new List<BetaTextBlockParam> { new() { Text = TriggerSystem(catalog), CacheControl = new BetaCacheControlEphemeral() } },
            OutputConfig = new BetaOutputConfig { Effort = ContentConversion.ParseEffort(_options.Effort) },
            Tools = [Tool(SkillToolName, "Carrega uma skill instalada para executar o pedido do usuário.", schema)],
            Messages = [new BetaUserMessageParam { Content = prompt }],
        };
        var response = await Call(p, ct);
        string? chosen = null;
        foreach (var block in response.Content)
            if (block.TryPickToolUse(out var use) && use.Name == SkillToolName)
            {
                chosen = ContentConversion.ToJsonObject(use.Input)["skill"]?.ToString();
                break;
            }
        return new(chosen, response.Usage.InputTokens, response.Usage.OutputTokens);
    }

    // ------------------------------------------------------------------ execução

    internal static string RunSystem(SkillDraft? skill)
    {
        const string harness = """
            Você é um agente de programação atendendo o usuário dentro do projeto dele.
            Este é um ambiente de teste: você não tem acesso ao sistema de arquivos do projeto nem a comandos.
            Quando precisar de arquivos do projeto, use o que o usuário colocou na mensagem; se faltar algo, declare a suposição e siga.
            Entregue o resultado completo na própria resposta (em vez de dizer que gravou um arquivo, mostre o conteúdo que gravaria).
            """;
        if (skill is null) return harness;

        var sb = new StringBuilder(harness);
        sb.Append("\nO usuário acionou a skill abaixo. Siga as instruções dela.\n\n");
        sb.Append("<skill name=\"").Append(skill.Name).Append("\">\n").Append(skill.Body.Trim()).Append("\n</skill>\n");
        var files = skill.Files.Where(f => !f.IsBinary).Select(f => f.RelativePath).ToList();
        if (files.Count > 0)
            sb.Append("\nArquivos da skill disponíveis pela ferramenta read_skill_file: ").Append(string.Join(", ", files)).Append('\n');
        return sb.ToString();
    }

    public async Task<ModelCall<string>> RunAsync(SkillDraft? skill, string prompt, CancellationToken ct)
    {
        var hasFiles = skill?.Files.Any(f => !f.IsBinary) == true;
        List<BetaToolUnion>? tools = hasFiles
            ?
            [
                Tool(ReadFileToolName, "Lê um arquivo auxiliar da skill (ex.: references/template.md).",
                    """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
            ]
            : null;
        var messages = new List<BetaMessageParam> { new BetaUserMessageParam { Content = prompt } };
        var output = new StringBuilder();
        long input = 0, outputTokens = 0;

        for (var round = 0; round < _options.MaxToolRounds; round++)
        {
            var p = new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                System = new List<BetaTextBlockParam> { new() { Text = RunSystem(skill), CacheControl = new BetaCacheControlEphemeral() } },
                OutputConfig = new BetaOutputConfig { Effort = ContentConversion.ParseEffort(_options.Effort) },
                Tools = tools,
                Messages = [.. messages],
            };
            var response = await Call(p, ct);
            input += response.Usage.InputTokens;
            outputTokens += response.Usage.OutputTokens;
            messages.Add(new BetaAssistantMessageParam { Content = ContentConversion.ToParams(response.Content) });

            var results = new List<BetaContentBlockParam>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text) && !string.IsNullOrWhiteSpace(text.Text))
                    output.Append(text.Text.Trim()).Append("\n\n");
                else if (block.TryPickToolUse(out var use))
                    results.Add(new BetaToolResultBlockParam
                    {
                        ToolUseID = use.ID,
                        Content = ReadSkillFile(skill!, ContentConversion.ToJsonObject(use.Input)["path"]?.ToString() ?? "", out var isError),
                        IsError = isError,
                    });
            }

            var stop = ContentConversion.StopReasonOf(response);
            if (stop == "tool_use" && results.Count > 0)
            {
                messages.Add(new BetaUserMessageParam { Content = results });
                continue;
            }
            if (stop == "max_tokens") output.Append("\n[resposta cortada no limite de tokens]");
            if (stop == "refusal") output.Append("\n[o modelo recusou o pedido]");
            break;
        }
        return new(output.ToString().Trim(), input, outputTokens);
    }

    internal static string ReadSkillFile(SkillDraft skill, string path, out bool isError)
    {
        var normalized = SkillSerializer.NormalizeRelativePath(path);
        var file = skill.Files.FirstOrDefault(f => !f.IsBinary && f.RelativePath == normalized);
        isError = file is null;
        return file?.Content ?? $"Arquivo não encontrado na skill: {path}";
    }

    // ------------------------------------------------------------------ juiz

    internal const string JudgeSystem = """
        Você avalia respostas de um agente de programação contra critérios objetivos.
        Para cada critério, decida se a resposta o atende de forma clara e completa. Na dúvida, reprove.
        Julgue só o que está escrito na resposta; não presuma trabalho que não aparece nela.
        A razão deve ter uma frase e citar o trecho ou a ausência que decidiu.
        """;

    private static readonly IReadOnlyDictionary<string, JsonElement> GradeSchema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
        {"type":"object","properties":{"grades":{"type":"array","items":{"type":"object","properties":{
          "index":{"type":"integer"},"passed":{"type":"boolean"},"reason":{"type":"string"}},
          "required":["index","passed","reason"],"additionalProperties":false}}},
         "required":["grades"],"additionalProperties":false}
        """)!;

    internal static string JudgePrompt(string prompt, string output, IReadOnlyList<string> criteria)
    {
        var sb = new StringBuilder();
        sb.Append("<pedido_do_usuario>\n").Append(prompt).Append("\n</pedido_do_usuario>\n\n");
        sb.Append("<resposta_do_agente>\n").Append(output).Append("\n</resposta_do_agente>\n\n");
        sb.Append("Critérios (avalie todos, usando o número como index):\n");
        for (var i = 0; i < criteria.Count; i++) sb.Append(i + 1).Append(". ").Append(criteria[i]).Append('\n');
        return sb.ToString();
    }

    public async Task<ModelCall<IReadOnlyList<RubricGrade>>> GradeAsync(string prompt, string output, IReadOnlyList<string> criteria, CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = 4000,
            System = new List<BetaTextBlockParam> { new() { Text = JudgeSystem, CacheControl = new BetaCacheControlEphemeral() } },
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                Format = new BetaJsonOutputFormat { Schema = GradeSchema },
            },
            Messages = [new BetaUserMessageParam { Content = JudgePrompt(prompt, output, criteria) }],
        };
        var response = await Call(p, ct);
        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return new(ParseGrades(text), response.Usage.InputTokens, response.Usage.OutputTokens);
    }

    internal static IReadOnlyList<RubricGrade> ParseGrades(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("grades").EnumerateArray()
                .Select(g => new RubricGrade(g.GetProperty("index").GetInt32(), g.GetProperty("passed").GetBoolean(),
                    g.GetProperty("reason").GetString() ?? ""))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ infraestrutura

    private static BetaTool Tool(string name, string description, string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        var root = doc.RootElement;
        return new BetaTool
        {
            Name = name,
            Description = description,
            InputSchema = new()
            {
                Properties = root.GetProperty("properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
                Required = root.TryGetProperty("required", out var req) ? req.EnumerateArray().Select(e => e.GetString()!).ToList() : [],
            },
        };
    }

    private async Task<BetaMessage> Call(MessageCreateParams p, CancellationToken ct)
    {
        try
        {
            return await _client.Beta.Messages.Create(p, ct);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new EvalFatalException("A API recusou a credencial. Configure a chave em Configurações, defina ANTHROPIC_API_KEY ou rode `ant auth login`.", ex);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new EvalFatalException($"Sem permissão para usar a API: {ex.Message}", ex);
        }
        catch (AnthropicPaymentRequiredException ex)
        {
            throw new EvalFatalException($"A conta da API está sem crédito: {ex.Message}", ex);
        }
    }
}
