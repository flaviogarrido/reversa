using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SkillStudio.Core.IO;
using SkillStudio.Core.Model;

namespace SkillStudio.Core.Evals;

/// <summary>
/// Casos de teste e última execução de cada skill, em evals/&lt;skill&gt;/ na raiz do repositório.
/// Ficam fora de agents/ de propósito: o instalador copia a pasta inteira da skill para os projetos,
/// e o pacote npm só publica agents/, bin/, lib/ e templates/.
/// </summary>
public sealed class EvalStore(string repositoryRoot)
{
    public const string FolderName = "evals";
    public const string CasesFile = "cases.json";
    public const string LastRunFile = "last-run.json";
    private const int MaxStoredOutputChars = 4000;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // acentos legíveis no diff
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string EvalsDir { get; } = Path.Combine(Path.GetFullPath(repositoryRoot), FolderName);

    public string SkillDir(string skill)
    {
        var dir = Path.GetFullPath(Path.Combine(EvalsDir, skill));
        if (!dir.StartsWith(EvalsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || skill.Contains('/') || skill.Contains('\\'))
            throw new InvalidOperationException("Nome de skill inválido para a pasta de testes.");
        return dir;
    }

    public IReadOnlySet<string> ListSkillsWithCases() =>
        Directory.Exists(EvalsDir)
            ? Directory.EnumerateDirectories(EvalsDir)
                .Where(d => File.Exists(Path.Combine(d, CasesFile)))
                .Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    public EvalSuite LoadSuite(string skill)
    {
        var path = Path.Combine(SkillDir(skill), CasesFile);
        if (!File.Exists(path)) return new EvalSuite { Skill = skill };
        var suite = JsonSerializer.Deserialize<EvalSuite>(File.ReadAllText(path), Json) ?? new EvalSuite();
        suite.Skill = skill;
        return suite;
    }

    public EvalReport? LoadLastRun(string skill)
    {
        var path = Path.Combine(SkillDir(skill), LastRunFile);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<EvalReport>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null; // relatório corrompido não deve impedir de abrir a skill
        }
    }

    /// <summary>Grava cases.json. Suite vazia apaga o arquivo (e a pasta, se ficar vazia).</summary>
    public bool SaveSuite(EvalSuite suite)
    {
        var dir = SkillDir(suite.Skill);
        var path = Path.Combine(dir, CasesFile);
        if (suite.Cases.Count == 0)
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            DeleteIfEmpty(dir);
            return true;
        }
        return WriteIfChanged(path, Serialize(suite));
    }

    public void SaveLastRun(string skill, EvalReport report)
    {
        var copy = JsonSerializer.Deserialize<EvalReport>(JsonSerializer.Serialize(report, Json), Json)!;
        foreach (var run in copy.Cases.SelectMany(c => c.Runs.Concat(c.BaselineRuns ?? [])))
            run.Output = TextUtil.Truncate(run.Output, MaxStoredOutputChars);
        WriteIfChanged(Path.Combine(SkillDir(skill), LastRunFile), Serialize(copy));
    }

    /// <summary>Acompanha o "salvar como" da skill: move os testes para o nome novo.</summary>
    public void Rename(string from, string to)
    {
        var src = SkillDir(from);
        var dst = SkillDir(to);
        if (!Directory.Exists(src) || Directory.Exists(dst)) return;
        Directory.Move(src, dst);
        var suite = LoadSuite(to);
        suite.Skill = to;
        SaveSuite(suite);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json).ReplaceLineEndings("\n") + "\n";

    public static string HashSkill(SkillDraft draft) =>
        Hash(string.Join("\u0001", SkillSerializer.RenderAll(draft).Select(f => f.RelativePath + "\u0002" + f.Content)));

    public static string HashCase(EvalCase c) => Hash(Serialize(c));

    public static string HashSuite(EvalSuite suite) => Hash(Serialize(new EvalSuite { Cases = suite.Cases }));

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, SkillSerializer.Utf8NoBom);
        return true;
    }

    private static void DeleteIfEmpty(string dir)
    {
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
