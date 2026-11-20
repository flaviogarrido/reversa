using SkillStudio.Core.IO;
using SkillStudio.Core.Model;

namespace SkillStudio.Tests;

public sealed class RepositorySaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillstudio-" + Guid.NewGuid().ToString("N"));

    public RepositorySaveTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        File.WriteAllText(Path.Combine(_root, "package.json"), """{"name":"reversa","version":"0.0.1","bin":{"reversa":"bin/reversa.js"}}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SkillDraft Draft() => new()
    {
        Name = "fg-teste",
        Description = "Skill de teste: grava, relê e remove arquivos sem tocar no que é binário.",
        Metadata = new SkillMetadata { Author = "teste" },
        Body = "Você é o testador.\r\n\r\n## Processo\r\n\r\n1. Leia `references/a.md`.\r\n",
        Files =
        [
            new SkillFile { RelativePath = "references/a.md", Content = "# A\r\n" },
            new SkillFile { RelativePath = "references/b.md", Content = "# B" },
        ],
    };

    [Fact]
    public void Save_writes_lf_utf8_without_bom_and_round_trips()
    {
        Assert.True(SkillRepository.IsReversaRoot(_root));
        var repo = new SkillRepository(_root);
        var result = repo.Save(Draft());

        Assert.Equal(4, result.Written.Count);
        foreach (var file in Directory.EnumerateFiles(result.Folder, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"BOM em {file}");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
        }

        var loaded = repo.Load("fg-teste");
        Assert.Equal(InvocationMode.User, loaded.Invocation);
        Assert.Equal(2, loaded.Files.Count);
        Assert.False(loaded.LoadedWithInvocationMismatch);
    }

    [Fact]
    public void Saving_again_without_changes_writes_nothing()
    {
        var repo = new SkillRepository(_root);
        repo.Save(Draft());
        var second = repo.Save(repo.Load("fg-teste"));
        Assert.Empty(second.Written);
        Assert.Empty(second.Deleted);
    }

    [Fact]
    public void Removed_text_files_are_deleted_but_binaries_are_preserved()
    {
        var repo = new SkillRepository(_root);
        repo.Save(Draft());
        var binary = Path.Combine(repo.SkillFolder("fg-teste"), "assets", "logo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllBytes(binary, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        var loaded = repo.Load("fg-teste");
        Assert.Contains(loaded.Files, f => f.IsBinary);
        loaded.Files.RemoveAll(f => f.RelativePath == "references/b.md" || f.IsBinary);
        var result = repo.Save(loaded);

        Assert.Contains("references/b.md", result.Deleted);
        Assert.True(File.Exists(binary));
    }

    [Fact]
    public void Path_escaping_the_skill_folder_is_refused()
    {
        var repo = new SkillRepository(_root);
        var d = Draft();
        d.Files.Add(new SkillFile { RelativePath = "../../fora.md", Content = "x" });
        Assert.Throws<InvalidOperationException>(() => repo.Save(d));
        Assert.False(File.Exists(Path.Combine(_root, "fora.md")));
    }
}
