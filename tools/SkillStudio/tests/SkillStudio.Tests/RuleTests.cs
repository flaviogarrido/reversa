using SkillStudio.Core.Assistant;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;
using SkillStudio.Core.Search;
using SkillStudio.Core.Validation;

namespace SkillStudio.Tests;

public sealed class RuleTests
{
    private static readonly ValidationContext Ctx = new()
    {
        ExistingSkillNames = new HashSet<string> { "reversa", "reversa-docs", "fg-auditoria" },
        RepoSkillFilePaths = new HashSet<string> { "references/shared.md" },
        PreferredPrefix = "fg-",
    };

    private static SkillDraft Valid() => new()
    {
        Name = "fg-checklist-lgpd",
        Description = "Revisa o legado contra um checklist de LGPD e registra as lacunas encontradas em _reversa_sdd/lgpd.md.",
        Invocation = InvocationMode.User,
        Metadata = new SkillMetadata { Author = "flavio", Version = "1.0.0", Framework = "reversa", Team = "discovery" },
        Body = "Você é o auditor de LGPD do Reversa. Sua missão é apontar onde o legado trata dados pessoais sem base legal clara.\n\n" +
               "## Processo\n\n1. Leia `references/checklist.md`.\n2. Compare cada item com o código e com as specs já extraídas.\n3. Registre a evidência de cada achado.\n\n" +
               "## Saída\n\nEscreva `_reversa_sdd/lgpd.md` com uma tabela de itens, evidência e status. Não altere arquivos fora de .reversa/ e da pasta de saída.\n",
        Files = [new SkillFile { RelativePath = "references/checklist.md", Content = "# Checklist\n\n- Base legal\n" }],
    };

    private static IReadOnlyList<ValidationIssue> Issues(SkillDraft d) => new SkillValidator().Validate(d, Ctx).Issues;

    private static void AssertRule(SkillDraft d, string rule, Severity severity) =>
        Assert.Contains(Issues(d), i => i.RuleId == rule && i.Severity == severity);

    [Fact]
    public void Valid_draft_has_no_errors_or_warnings()
    {
        var issues = Issues(Valid());
        Assert.True(!issues.Any(i => i.Severity != Severity.Info), string.Join(Environment.NewLine, issues));
    }

    [Theory]
    [InlineData("FG-Teste")]
    [InlineData("fg--teste")]
    [InlineData("-fg-teste")]
    [InlineData("fg_teste")]
    [InlineData("fg-teste-")]
    public void Name_must_be_kebab_case(string name) => AssertRule(Valid().With(d => d.Name = name), "RS002", Severity.Error);

    [Fact]
    public void Name_cannot_repeat_existing_folder() => AssertRule(Valid().With(d => d.Name = "fg-auditoria"), "RS004", Severity.Error);

    [Fact]
    public void Name_prefix_collision_is_flagged() => AssertRule(Valid().With(d => d.Name = "fg-auditoria-extra"), "RS005", Severity.Warning);

    [Fact]
    public void Upstream_prefix_is_flagged_for_new_skills() => AssertRule(Valid().With(d => d.Name = "reversa-lgpd"), "RS006", Severity.Warning);

    [Fact]
    public void Reserved_words_are_rejected() => AssertRule(Valid().With(d => d.Name = "fg-claude-helper"), "RS007", Severity.Error);

    [Theory]
    [InlineData("Faz X. Ative com /fg-x.")]
    [InlineData("Faz X. Use com \"/fg-x\".")]
    [InlineData("Faz X quando o usuário digitar \"x\" no chat.")]
    public void User_invoked_description_cannot_have_model_triggers(string description) =>
        AssertRule(Valid().With(d => d.Description = description), "RS013", Severity.Error);

    [Fact]
    public void Model_invoked_description_may_have_triggers_but_needs_usage_hint()
    {
        var d = Valid().With(x => { x.Invocation = InvocationMode.Model; x.Description = "Audita dados pessoais no legado e gera relatório de conformidade."; });
        Assert.DoesNotContain(Issues(d), i => i.RuleId == "RS013");
        AssertRule(d, "RS015", Severity.Warning);
    }

    [Fact]
    public void Description_over_limit_is_an_error() =>
        AssertRule(Valid().With(d => d.Description = new string('a', 1100)), "RS011", Severity.Error);

    [Fact]
    public void Missing_cited_reference_is_an_error() =>
        AssertRule(Valid().With(d => d.Files.Clear()), "RS053", Severity.Error);

    [Fact]
    public void Reference_from_another_skill_is_a_warning() =>
        AssertRule(Valid().With(d => d.Body += "\nVeja também references/shared.md.\n"), "RS053", Severity.Warning);

    [Fact]
    public void Unclosed_code_fence_is_an_error() =>
        AssertRule(Valid().With(d => d.Body += "\n```bash\nnpm test\n"), "RS059", Severity.Error);

    [Fact]
    public void Body_starting_with_frontmatter_marker_is_an_error() =>
        AssertRule(Valid().With(d => d.Body = "---\n" + d.Body), "RS052", Severity.Error);

    [Fact]
    public void Portuguese_word_todo_is_not_a_placeholder()
    {
        Assert.DoesNotContain(Issues(Valid().With(d => d.Body += "\nRevise todo o módulo.\n")), i => i.RuleId == "RS055");
        AssertRule(Valid().With(d => d.Body += "\nTODO: completar.\n"), "RS055", Severity.Warning);
    }

    [Fact]
    public void Secrets_are_blocked() =>
        AssertRule(Valid().With(d => d.Files[0].Content += "\nchave: sk-ant-api03-abcdefghijklmnopqrstuvwxyz\n"), "RS070", Severity.Error);

    [Fact]
    public void Destructive_commands_are_flagged() =>
        AssertRule(Valid().With(d => d.Body += "\nRode `git reset --hard` antes de começar.\n"), "RS071", Severity.Warning);

    [Fact]
    public void Path_traversal_in_files_is_an_error() =>
        AssertRule(Valid().With(d => d.Files.Add(new SkillFile { RelativePath = "../fora.md", Content = "x" })), "RS060", Severity.Error);

    [Fact]
    public void Generated_files_cannot_be_auxiliary() =>
        AssertRule(Valid().With(d => d.Files.Add(new SkillFile { RelativePath = "agents/openai.yaml", Content = "x" })), "RS061", Severity.Error);

    [Fact]
    public void Duplicate_paths_ignoring_case_are_an_error() =>
        AssertRule(Valid().With(d => d.Files.Add(new SkillFile { RelativePath = "references/Checklist.md", Content = "x" })), "RS064", Severity.Error);

    [Fact]
    public void Version_must_be_semver() =>
        AssertRule(Valid().With(d => d.Metadata.Version = "v1"), "RS041", Severity.Error);

    [Theory]
    [InlineData("Analisa: rotas, filas e jobs; gera # mapa")]
    [InlineData("'Aspas' e \"duplas\" convivem: ok")]
    [InlineData("true")]
    [InlineData("- começa com hífen")]
    public void Tricky_descriptions_survive_the_yaml_round_trip(string description)
    {
        var d = Valid().With(x => x.Description = description + " para revisar o legado com calma.");
        Assert.DoesNotContain(Issues(d), i => i.RuleId == "RS091");
        var back = SkillSerializer.Parse(SkillSerializer.RenderSkillMd(d), SkillSerializer.RenderOpenAiYaml(d));
        Assert.Equal(d.Description, back.Description);
    }

    [Fact]
    public void User_and_model_invocation_render_in_lockstep()
    {
        foreach (var mode in new[] { InvocationMode.User, InvocationMode.Model })
        {
            var d = Valid().With(x => x.Invocation = mode);
            var (fm, _) = SkillSerializer.SplitFrontmatter(SkillSerializer.RenderSkillMd(d));
            var yaml = SkillSerializer.RenderOpenAiYaml(d);
            Assert.Equal(mode == InvocationMode.User, InvocationAxis.IsClaudeUserInvoked(fm!));
            Assert.Equal(mode == InvocationMode.User, InvocationAxis.IsCodexUserInvoked(yaml));
        }
    }

    [Fact]
    public void Short_description_follows_the_repository_pattern()
    {
        var s = SkillSerializer.SuggestShortDescription("Mapeia a superfície do projeto legado — estrutura de pastas, linguagens, frameworks e dependências.");
        Assert.True(s.Length <= 61);
        Assert.EndsWith("…", s);
    }

    [Fact]
    public void Slugify_produces_valid_names() => Assert.Equal("fg-revisao-de-codigo", NameRules.Slugify("FG Revisão de Código!"));

    [Fact]
    public void Patch_applies_only_given_fields()
    {
        var d = Valid();
        var patched = new DraftPatch { Description = "Nova descrição bem detalhada do que a skill faz no fluxo.", Team = "bugs" }.ApplyTo(d);
        Assert.Equal(d.Name, patched.Name);
        Assert.Equal("bugs", patched.Metadata.Team);
        Assert.Equal(d.Body, patched.Body);
    }

    [Fact]
    public void Search_ranks_the_obvious_match_first()
    {
        var index = SkillSearchIndex.Build(
        [
            new SkillSummary("fg-bugs", "Registra e classifica bugs com rastreabilidade", InvocationMode.User, "bugs", "", "", "Triagem de defeitos.", "", 2),
            new SkillSummary("fg-precos", "Estima preço de software e de features", InvocationMode.User, "pricing", "", "", "Modelo de cobrança.", "", 2),
        ]);
        Assert.Equal("fg-bugs", index.Search("registrar defeito e bug encontrado", 2)[0].Name);
    }
}

internal static class DraftTestExtensions
{
    public static SkillDraft With(this SkillDraft d, Action<SkillDraft> change)
    {
        change(d);
        return d;
    }
}
