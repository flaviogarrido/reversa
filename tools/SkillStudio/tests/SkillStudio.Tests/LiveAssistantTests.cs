using SkillStudio.Core.Assistant;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;
using SkillStudio.Core.Search;
using SkillStudio.Core.Validation;
using Xunit.Abstractions;

namespace SkillStudio.Tests;

/// <summary>
/// Teste de ponta a ponta contra a API real. Só roda com SKILLSTUDIO_LIVE=1 (gasta tokens).
/// </summary>
public sealed class LiveAssistantTests(ITestOutputHelper output)
{
    private sealed class StubHost(ValidationContext ctx) : IDraftHost
    {
        public SkillDraft Draft { get; private set; } = new() { Name = "fg-", Metadata = new SkillMetadata { Author = "teste" } };
        private readonly SkillValidator _validator = new();
        public SkillDraft GetDraft() => Draft.Clone();
        public SkillDraft ApplyPatch(DraftPatch patch) => Draft = patch.ApplyTo(Draft);
        public ValidationReport Validate(SkillDraft draft) => _validator.Validate(draft, ctx);
    }

    [Fact]
    public async Task Interview_searches_similar_skills_and_fills_the_draft()
    {
        if (Environment.GetEnvironmentVariable("SKILLSTUDIO_LIVE") != "1") return;

        var repo = new SkillRepository(SkillRepository.FindRoot(AppContext.BaseDirectory)!);
        var host = new StubHost(repo.CreateValidationContext("fg-"));
        var index = SkillSearchIndex.Build(repo.ListSkills());
        var assistant = new SkillAssistant(
            new AssistantOptions { Effort = "low", EnableWebSearch = false, PreferredPrefix = "fg-", Author = "teste" },
            new AssistantTools(repo, () => index, host), 78);
        var events = new List<AssistantEvent>();
        assistant.EventRaised += e => { events.Add(e); output.WriteLine($"[{e.Kind}] {e.Text}"); };

        await assistant.SendAsync(
            "Teste automatizado, sem entrevista: busque skills parecidas com 'checklist de revisão de segurança OWASP para código legado', " +
            "depois preencha o rascunho com update_draft (nome fg-revisao-owasp, invocação user, description curta e um corpo com ## Processo e ## Saída). " +
            "Corrija erros de validação se houver e responda só 'pronto'.");

        output.WriteLine($"tokens: {assistant.InputTokens} in / {assistant.OutputTokens} out");
        Assert.DoesNotContain(events, e => e.Kind == AssistantEventKind.Error);
        Assert.Contains(events, e => e.Kind == AssistantEventKind.Tool && e.Text.Contains("parecidas"));
        Assert.Equal("fg-revisao-owasp", host.Draft.Name);
        Assert.Equal(InvocationMode.User, host.Draft.Invocation);
        Assert.Equal(0, host.Validate(host.Draft).ErrorCount);
    }
}
