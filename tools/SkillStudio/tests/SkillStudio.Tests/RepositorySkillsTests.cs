using SkillStudio.Core.IO;
using SkillStudio.Core.Model;
using SkillStudio.Core.Validation;
using Xunit.Abstractions;

namespace SkillStudio.Tests;

/// <summary>
/// Regressão contra o repositório real: as 78 skills que já passam no CI do Reversa
/// não podem gerar nenhum erro, e a ida e volta pelo serializador precisa preservá-las.
/// </summary>
public sealed class RepositorySkillsTests(ITestOutputHelper output)
{
    private static readonly string Root = SkillRepository.FindRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Checkout do Reversa não encontrado acima da pasta de testes.");

    private static readonly SkillRepository Repo = new(Root);

    public static TheoryData<string> SkillNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Repo.ListNames().Order(StringComparer.Ordinal)) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void Existing_skill_has_no_validation_errors(string name)
    {
        var draft = Repo.Load(name);
        var report = new SkillValidator().Validate(draft, Repo.CreateValidationContext());

        foreach (var issue in report.Issues) output.WriteLine(issue.ToString());
        Assert.True(report.ErrorCount == 0,
            string.Join(Environment.NewLine, report.Issues.Where(i => i.Severity == Severity.Error)));
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void Render_then_parse_preserves_the_skill(string name)
    {
        var original = Repo.Load(name);
        var back = SkillSerializer.Parse(SkillSerializer.RenderSkillMd(original), SkillSerializer.RenderOpenAiYaml(original));

        Assert.Equal(original.Name, back.Name);
        Assert.Equal(SkillSerializer.OneLine(original.Description), back.Description);
        Assert.Equal(original.Invocation, back.Invocation);
        Assert.Equal(original.License, back.License);
        Assert.Equal(original.Metadata.Version, back.Metadata.Version);
        Assert.Equal(original.Metadata.Team, back.Metadata.Team);
        Assert.Equal(original.Body.Trim(), back.Body.Trim());
        Assert.False(back.LoadedWithInvocationMismatch);
    }

    [Theory]
    [MemberData(nameof(SkillNames))]
    public void Rendered_files_pass_the_invocation_axis_like_the_python_verifier(string name)
    {
        var draft = Repo.Load(name);
        var (frontmatter, _) = SkillSerializer.SplitFrontmatter(SkillSerializer.RenderSkillMd(draft));
        var yaml = SkillSerializer.RenderOpenAiYaml(draft);

        Assert.NotNull(frontmatter);
        Assert.Equal(InvocationAxis.IsClaudeUserInvoked(frontmatter!), InvocationAxis.IsCodexUserInvoked(yaml));
        Assert.True(InvocationAxis.HasDisplayName(yaml));
        Assert.True(InvocationAxis.HasShortDescription(yaml));
        if (draft.Invocation == InvocationMode.User)
            Assert.Null(InvocationAxis.FindTriggerSignature(InvocationAxis.ExtractDescription(frontmatter!)!));
    }

    [Fact]
    public void Unchanged_skill_md_is_rendered_byte_for_byte_for_most_skills()
    {
        // O editor não deve gerar diff à toa ao salvar uma skill sem mudanças.
        var identical = 0;
        var names = Repo.ListNames();
        foreach (var name in names)
        {
            var onDisk = SkillSerializer.NormalizeNewlines(File.ReadAllText(Path.Combine(Repo.SkillFolder(name), "SKILL.md")));
            if (onDisk == SkillSerializer.RenderSkillMd(Repo.Load(name))) identical++;
            else output.WriteLine($"diff: {name}");
        }
        output.WriteLine($"{identical}/{names.Count} idênticos");
        Assert.True(identical >= names.Count * 0.8, $"Só {identical} de {names.Count} SKILL.md saíram idênticos.");
    }
}
