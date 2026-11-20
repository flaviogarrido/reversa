using System.Text.Json.Serialization;

namespace SkillStudio.Core.Evals;

/// <summary>
/// Tipo do caso de teste.
/// Gatilho: o modelo, vendo o catálogo de skills, escolhe (ou não) esta skill para o pedido.
/// Comportamento: com a skill carregada, a resposta ao pedido atende às asserções.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvalKind>))]
public enum EvalKind { Trigger, Behavior }

[JsonConverter(typeof(JsonStringEnumConverter<AssertionKind>))]
public enum AssertionKind
{
    /// <summary>A saída contém o texto (sem diferenciar maiúsculas).</summary>
    Contains,

    /// <summary>A saída não contém o texto.</summary>
    NotContains,

    /// <summary>A saída casa com a expressão regular (.NET, multilinha).</summary>
    Regex,

    /// <summary>Critério em linguagem natural, avaliado por um modelo juiz.</summary>
    Rubric,
}

public sealed class EvalAssertion
{
    public AssertionKind Kind { get; set; } = AssertionKind.Rubric;
    public string Value { get; set; } = "";

    public EvalAssertion Clone() => new() { Kind = Kind, Value = Value };
}

public sealed class EvalCase
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public EvalKind Kind { get; set; } = EvalKind.Behavior;

    /// <summary>O pedido do usuário, como seria digitado. Pode trazer trechos de arquivos como contexto.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Só para gatilho: true = a skill deve ser escolhida; false = não deve.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShouldTrigger { get; set; }

    /// <summary>Só para comportamento.</summary>
    public List<EvalAssertion> Assertions { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }

    public EvalCase Clone() => new()
    {
        Id = Id,
        Title = Title,
        Kind = Kind,
        Prompt = Prompt,
        ShouldTrigger = ShouldTrigger,
        Assertions = Assertions.Select(a => a.Clone()).ToList(),
        Notes = Notes,
    };
}

/// <summary>Conteúdo de evals/&lt;skill&gt;/cases.json.</summary>
public sealed class EvalSuite
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Skill { get; set; } = "";
    public List<EvalCase> Cases { get; set; } = [];

    public EvalSuite Clone() => new() { Version = Version, Skill = Skill, Cases = Cases.Select(c => c.Clone()).ToList() };

    /// <summary>Gera um id curto e estável que não colide com os existentes (c01, c02...).</summary>
    public string NextId()
    {
        for (var n = Cases.Count + 1; ; n++)
        {
            var id = $"c{n:00}";
            if (Cases.All(c => c.Id != id)) return id;
        }
    }
}

// ---------------------------------------------------------------------- resultados

public sealed class AssertionResult
{
    public AssertionKind Kind { get; set; }
    public string Value { get; set; } = "";
    public bool Passed { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class RunResult
{
    public bool Passed { get; set; }

    /// <summary>Gatilho: skill escolhida (null = nenhuma). Comportamento: não usado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChosenSkill { get; set; }

    /// <summary>Resposta do modelo (truncada ao gravar).</summary>
    public string Output { get; set; } = "";

    public List<AssertionResult> Assertions { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public sealed class CaseResult
{
    public string CaseId { get; set; } = "";
    public string Title { get; set; } = "";
    public EvalKind Kind { get; set; }

    /// <summary>Hash do caso quando rodou; diferente do atual = o caso foi editado depois.</summary>
    public string CaseHash { get; set; } = "";

    public List<RunResult> Runs { get; set; } = [];

    /// <summary>Execuções sem a skill (linha de base), quando pedidas.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RunResult>? BaselineRuns { get; set; }

    public int PassCount => Runs.Count(r => r.Passed);

    /// <summary>Passa só se todas as repetições passaram: flakiness conta como falha.</summary>
    public bool Passed => Runs.Count > 0 && Runs.All(r => r.Passed);

    [JsonIgnore]
    public bool IsFlaky => PassCount > 0 && PassCount < Runs.Count;

    [JsonIgnore]
    public bool HasErrors => Runs.Any(r => r.Error is not null);
}

/// <summary>Conteúdo de evals/&lt;skill&gt;/last-run.json: a evidência de confiança da skill.</summary>
public sealed class EvalReport
{
    public DateTimeOffset RanAt { get; set; }
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public int Repetitions { get; set; }
    public bool IncludedBaseline { get; set; }

    /// <summary>Hash dos arquivos da skill no momento da execução; diferente do atual = resultado desatualizado.</summary>
    public string SkillHash { get; set; } = "";

    /// <summary>Hash dos casos; diferente do atual = os casos mudaram depois da execução.</summary>
    public string SuiteHash { get; set; } = "";

    public List<CaseResult> Cases { get; set; } = [];
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}
