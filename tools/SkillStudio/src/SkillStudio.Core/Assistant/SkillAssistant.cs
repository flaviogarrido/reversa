using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace SkillStudio.Core.Assistant;

public sealed class AssistantOptions
{
    public const string DefaultModel = "claude-opus-5-5";

    public string Model { get; set; } = DefaultModel;

    /// <summary>low | medium | high | xhigh | max. Em Claude Opus 5.5 o padrão da API é medium.</summary>
    public string Effort { get; set; } = "medium";

    public bool EnableWebSearch { get; set; } = true;

    /// <summary>Chave explícita. Nula: o SDK resolve sozinho (ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN ou perfil do `ant auth login`).</summary>
    public string? ApiKey { get; set; }

    public string PreferredPrefix { get; set; } = "";
    public string Author { get; set; } = "";
    public int MaxToolRounds { get; set; } = 16;
    public int MaxTokens { get; set; } = 16000;
}

public enum AssistantEventKind { Text, Tool, WebSearch, Notice, Error }

public sealed record AssistantEvent(AssistantEventKind Kind, string Text);

/// <summary>
/// Entrevistador baseado na API do Claude. Loop manual de tool use sobre o endpoint beta
/// (necessário para o fallback de recusa no servidor), sem streaming: cada rodada usa até 16k tokens.
/// </summary>
public sealed class SkillAssistant
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly AnthropicClient _client;
    private readonly AssistantOptions _options;
    private readonly AssistantTools _tools;
    private readonly int _skillCount;
    private readonly List<BetaMessageParam> _messages = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SkillAssistant(AssistantOptions options, AssistantTools tools, int skillCount)
    {
        _options = options;
        _tools = tools;
        _skillCount = skillCount;
        _client = string.IsNullOrWhiteSpace(options.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.ApiKey };
    }

    public event Action<AssistantEvent>? EventRaised;

    public bool HasConversation => _messages.Count > 0;

    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }

    public void Reset() => _messages.Clear();

    public async Task SendAsync(string userText, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        var checkpoint = _messages.Count;
        try
        {
            _messages.Add(new BetaUserMessageParam { Content = userText });
            await RunLoopAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Rollback(checkpoint);
            Raise(AssistantEventKind.Notice, "Pedido cancelado.");
        }
        catch (AnthropicUnauthorizedException)
        {
            Rollback(checkpoint);
            Raise(AssistantEventKind.Error, "A API recusou a credencial. Configure a chave em Configurações, defina ANTHROPIC_API_KEY ou rode `ant auth login`.");
        }
        catch (AnthropicRateLimitException)
        {
            Rollback(checkpoint);
            Raise(AssistantEventKind.Error, "Limite de uso da API atingido. Aguarde um pouco e envie de novo.");
        }
        catch (AnthropicBadRequestException ex)
        {
            Rollback(checkpoint);
            Raise(AssistantEventKind.Error, $"A API rejeitou o pedido: {ex.Message}");
        }
        catch (AnthropicApiException ex)
        {
            Rollback(checkpoint);
            Raise(AssistantEventKind.Error, $"Erro da API do Claude: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or AnthropicException)
        {
            Rollback(checkpoint);
            Raise(AssistantEventKind.Error, $"Não foi possível falar com a API do Claude: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        for (var round = 0; round < _options.MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await _client.Beta.Messages.Create(BuildParams(), ct);
            InputTokens += response.Usage.InputTokens;
            OutputTokens += response.Usage.OutputTokens;

            _messages.Add(new BetaAssistantMessageParam { Content = ContentConversion.ToParams(response.Content) });

            var toolResults = new List<BetaContentBlockParam>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text) && !string.IsNullOrWhiteSpace(text.Text))
                    Raise(AssistantEventKind.Text, text.Text.Trim());
                else if (block.TryPickServerToolUse(out var server))
                    Raise(AssistantEventKind.WebSearch, DescribeServerTool(server));
                else if (block.TryPickToolUse(out var toolUse))
                {
                    var input = ContentConversion.ToJsonObject(toolUse.Input);
                    var outcome = _tools.Execute(toolUse.Name, input);
                    Raise(AssistantEventKind.Tool, outcome.Activity);
                    toolResults.Add(new BetaToolResultBlockParam
                    {
                        ToolUseID = toolUse.ID,
                        Content = outcome.Content,
                        IsError = outcome.IsError,
                    });
                }
            }

            var stop = ContentConversion.StopReasonOf(response);
            switch (stop)
            {
                case "tool_use" when toolResults.Count > 0:
                    _messages.Add(new BetaUserMessageParam { Content = toolResults });
                    continue;
                case "pause_turn":
                    continue; // o servidor pausou uma ferramenta própria (busca web); reenviar retoma.
                case "max_tokens":
                    if (toolResults.Count > 0) _messages.Add(new BetaUserMessageParam { Content = toolResults });
                    Raise(AssistantEventKind.Notice, "A resposta atingiu o limite de tamanho. Peça para continuar ou para dividir o conteúdo em partes.");
                    return;
                case "refusal":
                    Raise(AssistantEventKind.Error, "O modelo recusou este pedido. Reformule a solicitação.");
                    return;
                default:
                    if (toolResults.Count > 0) _messages.Add(new BetaUserMessageParam { Content = toolResults });
                    return;
            }
        }
        Raise(AssistantEventKind.Notice, "Muitas rodadas de ferramentas seguidas; parei para você revisar. Envie \"continue\" para prosseguir.");
    }

    internal MessageCreateParams BuildParams()
    {
        List<BetaToolUnion> tools = [];
        foreach (var (name, description, schema) in AssistantTools.Definitions)
        {
            using var doc = JsonDocument.Parse(schema);
            var root = doc.RootElement;
            var properties = root.GetProperty("properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var required = root.TryGetProperty("required", out var req) ? req.EnumerateArray().Select(e => e.GetString()!).ToList() : [];
            tools.Add(new BetaTool
            {
                Name = name,
                Description = description,
                InputSchema = new() { Properties = properties, Required = required },
            });
        }
        if (_options.EnableWebSearch)
            tools.Add(new BetaWebSearchTool20260209 { MaxUses = 5 });

        return new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
            System = new List<BetaTextBlockParam>
            {
                new() { Text = AssistantPrompts.System, CacheControl = new BetaCacheControlEphemeral() },
                new() { Text = AssistantPrompts.SessionContext(_options.PreferredPrefix, _options.Author, _skillCount) },
            },
            OutputConfig = new BetaOutputConfig { Effort = ContentConversion.ParseEffort(_options.Effort) },
            Tools = tools,
            Messages = [.. _messages],
        };
    }

    private static string DescribeServerTool(BetaServerToolUseBlock block)
    {
        var input = ContentConversion.ToJsonObject(block.Input);
        var query = input["query"]?.ToString();
        return query is null ? "usou uma ferramenta do servidor" : $"pesquisou na web: \"{query}\"";
    }

    private void Rollback(int checkpoint)
    {
        if (_messages.Count > checkpoint) _messages.RemoveRange(checkpoint, _messages.Count - checkpoint);
    }

    private void Raise(AssistantEventKind kind, string text) => EventRaised?.Invoke(new AssistantEvent(kind, text));
}
