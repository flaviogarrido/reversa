using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;

namespace SkillStudio.Core.Assistant;

/// <summary>
/// O SDK C# não tem ToParam(): o bloco de resposta precisa virar bloco de requisição para ecoar o
/// turno do assistente. A API aceita os blocos de resposta como vieram (thinking com assinatura,
/// server_tool_use, web_search_tool_result, fallback), então a conversão passa pelo JSON de fio.
/// </summary>
public static class ContentConversion
{
    public static List<BetaContentBlockParam> ToParams(IReadOnlyList<BetaContentBlock> blocks)
    {
        var list = new List<BetaContentBlockParam>(blocks.Count);
        foreach (var block in blocks)
        {
            var json = JsonSerializer.Serialize(block);
            var param = JsonSerializer.Deserialize<BetaContentBlockParam>(json)
                        ?? throw new InvalidOperationException("Bloco de conteúdo vazio na resposta.");
            list.Add(param);
        }
        return list;
    }

    public static JsonObject ToJsonObject(IReadOnlyDictionary<string, JsonElement> input)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in input)
            obj[key] = JsonNode.Parse(value.GetRawText());
        return obj;
    }

    public static string StopReasonOf(BetaMessage message)
    {
        var json = JsonSerializer.SerializeToElement(message);
        return json.TryGetProperty("stop_reason", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
    }

    public static Effort ParseEffort(string value) => value switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
