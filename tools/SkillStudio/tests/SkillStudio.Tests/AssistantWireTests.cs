using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using SkillStudio.Core.Assistant;

namespace SkillStudio.Tests;

/// <summary>Formato de fio do assistente, verificado sem chamar a API.</summary>
public sealed class AssistantWireTests
{
    private const string SampleResponse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":10,"output_tokens":5},
         "content":[
          {"type":"thinking","thinking":"","signature":"sig123"},
          {"type":"text","text":"Olá","citations":null},
          {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"lgpd"}},
          {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[{"type":"web_search_result","url":"https://x.com","title":"X","encrypted_content":"abc","page_age":null}]},
          {"type":"tool_use","id":"toolu_1","name":"search_similar_skills","input":{"query":"bugs"}}
         ]}
        """;

    [Fact]
    public void Response_blocks_are_echoed_back_unchanged()
    {
        var msg = JsonSerializer.Deserialize<BetaMessage>(SampleResponse)!;
        var echoed = JsonSerializer.SerializeToElement(ContentConversion.ToParams(msg.Content));
        var original = JsonDocument.Parse(SampleResponse).RootElement.GetProperty("content");

        Assert.Equal(original.GetArrayLength(), echoed.GetArrayLength());
        for (var i = 0; i < original.GetArrayLength(); i++)
            Assert.Equal(original[i].GetProperty("type").GetString(), echoed[i].GetProperty("type").GetString());
        Assert.Equal("sig123", echoed[0].GetProperty("signature").GetString()); // assinatura do thinking preservada
        Assert.Equal("tool_use", ContentConversion.StopReasonOf(msg));
    }

    [Fact]
    public void Request_uses_opus_fallbacks_effort_cache_and_all_tools()
    {
        var assistant = new SkillAssistant(new AssistantOptions { ApiKey = "test", Effort = "high" }, new AssistantTools(null!, null!, null!), 78);
        var body = JsonSerializer.SerializeToElement(assistant.BuildParams()).GetProperty("RawBodyData");
        var raw = body.GetRawText();

        Assert.Equal("claude-opus-5-5", body.GetProperty("model").GetString());
        Assert.Equal("default", body.GetProperty("fallbacks").GetString());
        Assert.Contains("\"effort\":\"high\"", raw);
        Assert.Contains("\"cache_control\"", raw);
        Assert.DoesNotContain("budget_tokens", raw);
        Assert.DoesNotContain("\"thinking\"", raw);
        var toolNames = body.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(["search_similar_skills", "read_skill", "get_current_draft", "update_draft", "web_search"], toolNames);
    }

    [Fact]
    public void Web_search_can_be_disabled()
    {
        var assistant = new SkillAssistant(new AssistantOptions { ApiKey = "test", EnableWebSearch = false }, new AssistantTools(null!, null!, null!), 1);
        var raw = JsonSerializer.SerializeToElement(assistant.BuildParams()).GetProperty("RawBodyData").GetRawText();
        Assert.DoesNotContain("web_search_20260209", raw);
    }
}
