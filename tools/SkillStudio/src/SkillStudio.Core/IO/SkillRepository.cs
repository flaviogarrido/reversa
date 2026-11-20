using System.Text.Json;
using SkillStudio.Core.Model;

namespace SkillStudio.Core.IO;

/// <summary>Acesso à pasta agents/ de um checkout do Reversa (ou de um fork).</summary>
public sealed class SkillRepository
{
    public SkillRepository(string root)
    {
        Root = Path.GetFullPath(root);
        AgentsDir = Path.Combine(Root, "agents");
        if (!Directory.Exists(AgentsDir))
            throw new DirectoryNotFoundException($"Pasta agents/ não encontrada em {Root}.");
    }

    public string Root { get; }
    public string AgentsDir { get; }

    /// <summary>Sobe a partir de <paramref name="start"/> até achar um checkout do Reversa.</summary>
    public static string? FindRoot(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (IsReversaRoot(dir.FullName)) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static bool IsReversaRoot(string path)
    {
        var pkg = Path.Combine(path, "package.json");
        if (!File.Exists(pkg) || !Directory.Exists(Path.Combine(path, "agents"))) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("bin", out var bin)
                   && bin.ValueKind == JsonValueKind.Object
                   && bin.TryGetProperty("reversa", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Versão e nome do pacote, para mostrar na barra de status.</summary>
    public (string Name, string Version) ReadPackageInfo()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "package.json")));
            return (doc.RootElement.GetProperty("name").GetString() ?? "?", doc.RootElement.GetProperty("version").GetString() ?? "?");
        }
        catch
        {
            return ("?", "?");
        }
    }

    public IReadOnlySet<string> ListNames() =>
        Directory.EnumerateDirectories(AgentsDir).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>Caminhos relativos de todos os arquivos auxiliares de todas as skills (references/x.md...).</summary>
    public IReadOnlySet<string> ListAllSkillFilePaths()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(AgentsDir))
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                set.Add(SkillSerializer.NormalizeRelativePath(Path.GetRelativePath(dir, file)));
        return set;
    }

    public Validation.ValidationContext CreateValidationContext(string preferredPrefix = "") => new()
    {
        ExistingSkillNames = ListNames(),
        RepoSkillFilePaths = ListAllSkillFilePaths(),
        PreferredPrefix = preferredPrefix,
    };

    public IReadOnlyList<SkillSummary> ListSkills()
    {
        var list = new List<SkillSummary>();
        foreach (var dir in Directory.EnumerateDirectories(AgentsDir).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            try
            {
                if (!File.Exists(Path.Combine(dir, SkillSerializer.SkillFileName)))
                {
                    list.Add(new SkillSummary(name, "", InvocationMode.Model, "", "", "", "", dir, 0, "SKILL.md ausente"));
                    continue;
                }
                var d = SkillSerializer.ParseFolder(dir);
                list.Add(new SkillSummary(name, d.Description, d.Invocation, d.Metadata.Team, d.Metadata.Phase,
                    d.Metadata.Role, d.Body, dir, d.Files.Count + 2));
            }
            catch (Exception ex)
            {
                list.Add(new SkillSummary(name, "", InvocationMode.Model, "", "", "", "", dir, 0, ex.Message));
            }
        }
        return list;
    }

    public SkillDraft Load(string name) => SkillSerializer.ParseFolder(SkillFolder(name));

    public string SkillFolder(string name) => Path.Combine(AgentsDir, name);

    public string ReadFile(string skillName, string relativePath)
    {
        var folder = Path.GetFullPath(SkillFolder(skillName));
        var full = Path.GetFullPath(Path.Combine(folder, relativePath));
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Caminho fora da skill.");
        return File.ReadAllText(full);
    }

    /// <summary>
    /// Grava a skill em agents/&lt;nome&gt;/ com LF e UTF-8 sem BOM. Arquivos de texto que existiam
    /// na versão carregada e foram removidos no editor são apagados; binários nunca são tocados.
    /// </summary>
    public SaveResult Save(SkillDraft draft)
    {
        var name = draft.Name.Trim();
        var folder = Path.GetFullPath(SkillFolder(name));
        if (!folder.StartsWith(Path.GetFullPath(AgentsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nome de skill inválido.");

        var sameFolder = string.Equals(draft.OriginalName, name, StringComparison.Ordinal);
        var written = new List<string>();
        var deleted = new List<string>();

        // Arquivos previstos (texto) e binários a manter.
        var rendered = SkillSerializer.RenderAll(draft);
        var keep = new HashSet<string>(rendered.Select(r => r.RelativePath), StringComparer.Ordinal);
        foreach (var f in draft.Files.Where(f => f.IsBinary))
            keep.Add(SkillSerializer.NormalizeRelativePath(f.RelativePath));

        if (sameFolder && Directory.Exists(folder))
        {
            foreach (var existing in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var rel = SkillSerializer.NormalizeRelativePath(Path.GetRelativePath(folder, existing));
                if (keep.Contains(rel)) continue;
                var bytes = File.ReadAllBytes(existing);
                if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0)) continue; // binário: preserva
                File.Delete(existing);
                deleted.Add(rel);
            }
        }
        else if (!sameFolder && draft.OriginalName is not null)
        {
            // "Salvar como": copia binários da pasta original.
            var source = SkillFolder(draft.OriginalName);
            foreach (var f in draft.Files.Where(f => f.IsBinary))
            {
                var rel = SkillSerializer.NormalizeRelativePath(f.RelativePath);
                var src = Path.Combine(source, rel);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(folder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                written.Add(rel);
            }
        }

        foreach (var (rel, content) in rendered)
        {
            var full = Path.GetFullPath(Path.Combine(folder, rel));
            if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Caminho fora da skill: {rel}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var current = File.Exists(full) ? File.ReadAllText(full) : null;
            if (current == content) continue;
            File.WriteAllText(full, content, SkillSerializer.Utf8NoBom);
            written.Add(rel);
        }

        RemoveEmptyDirectories(folder);
        return new SaveResult(folder, written, deleted);
    }

    private static void RemoveEmptyDirectories(string folder)
    {
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
    }
}

public sealed record SaveResult(string Folder, IReadOnlyList<string> Written, IReadOnlyList<string> Deleted);
