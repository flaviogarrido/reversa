using SkillStudio.Core.Model;

namespace SkillStudio.Core.Search;

public sealed record SearchHit(string Name, string Description, double Score, string Team, InvocationMode Invocation, string Snippet);

/// <summary>
/// Busca local por similaridade (TF-IDF + cosseno) sobre as skills do repositório.
/// Nome e description pesam mais que o corpo. Sem dependências externas e sem rede.
/// </summary>
public sealed class SkillSearchIndex
{
    private static readonly HashSet<string> StopWords =
    [
        // pt
        "que", "para", "com", "uma", "por", "dos", "das", "nos", "nas", "não", "nao", "mais", "como", "sem", "sua", "seu",
        "ser", "ter", "ele", "ela", "isso", "este", "esta", "esse", "essa", "quando", "onde", "qual", "cada", "pelo", "pela",
        "entre", "sobre", "depois", "antes", "também", "tambem", "use", "usa", "agente", "skill", "reversa", "voce", "você",
        "the", "and", "for", "with", "from", "this", "that", "when", "into", "your", "you", "are", "not", "use",
    ];

    private readonly List<(SkillSummary Skill, Dictionary<string, double> Vector)> _docs = [];
    private Dictionary<string, double> _idf = [];

    public int Count => _docs.Count;

    public static SkillSearchIndex Build(IEnumerable<SkillSummary> skills)
    {
        var index = new SkillSearchIndex();
        var termFreqs = new List<(SkillSummary, Dictionary<string, double>)>();
        var df = new Dictionary<string, int>();

        foreach (var s in skills.Where(s => s.LoadError is null))
        {
            var tf = new Dictionary<string, double>();
            void Add(string text, double weight)
            {
                foreach (var t in Tokenize(text))
                    tf[t] = tf.GetValueOrDefault(t) + weight;
            }
            Add(s.Name.Replace('-', ' '), 3);
            Add(s.Description, 2);
            Add(s.Team + " " + s.Phase + " " + s.Role, 1.5);
            Add(s.Body, 1);
            foreach (var t in tf.Keys) df[t] = df.GetValueOrDefault(t) + 1;
            termFreqs.Add((s, tf));
        }

        var n = Math.Max(1, termFreqs.Count);
        index._idf = df.ToDictionary(kv => kv.Key, kv => Math.Log(1.0 + n / (double)kv.Value));
        foreach (var (skill, tf) in termFreqs)
            index._docs.Add((skill, index.Weigh(tf)));
        return index;
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit = 5, string? excludeName = null)
    {
        if (string.IsNullOrWhiteSpace(query) || _docs.Count == 0) return [];
        var qtf = new Dictionary<string, double>();
        foreach (var t in Tokenize(query)) qtf[t] = qtf.GetValueOrDefault(t) + 1;
        var q = Weigh(qtf);
        if (q.Count == 0) return [];

        var terms = qtf.Keys.ToHashSet();
        return _docs
            .Where(d => d.Skill.Name != excludeName)
            .Select(d => (d.Skill, Score: Cosine(q, d.Vector)))
            .Where(x => x.Score > 0.02)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => new SearchHit(x.Skill.Name, x.Skill.Description, Math.Round(x.Score, 3), x.Skill.Team,
                x.Skill.Invocation, Snippet(x.Skill.Body, terms)))
            .ToList();
    }

    public static IEnumerable<string> Tokenize(string text)
    {
        var normalized = TextUtil.RemoveDiacritics(text ?? "").ToLowerInvariant();
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var isWord = i < normalized.Length && char.IsLetterOrDigit(normalized[i]);
            if (isWord && start < 0) start = i;
            else if (!isWord && start >= 0)
            {
                var token = normalized[start..i];
                start = -1;
                if (token.Length < 3 || StopWords.Contains(token) || token.All(char.IsDigit)) continue;
                yield return Stem(token);
            }
        }
    }

    // Stemming leve, suficiente para aproximar plural e flexões em pt/en.
    private static string Stem(string t)
    {
        foreach (var suffix in new[] { "coes", "ções", "mente", "acao", "ação", "ing", "es", "s" })
            if (t.Length > suffix.Length + 3 && t.EndsWith(suffix, StringComparison.Ordinal))
                return t[..^suffix.Length];
        return t;
    }

    private Dictionary<string, double> Weigh(Dictionary<string, double> tf)
    {
        var v = new Dictionary<string, double>();
        foreach (var (term, f) in tf)
            if (_idf.TryGetValue(term, out var idf))
                v[term] = (1 + Math.Log(f)) * idf;
        var norm = Math.Sqrt(v.Values.Sum(x => x * x));
        if (norm > 0)
            foreach (var k in v.Keys.ToList()) v[k] /= norm;
        return v;
    }

    private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        double sum = 0;
        foreach (var (k, v) in small)
            if (large.TryGetValue(k, out var w)) sum += v * w;
        return sum;
    }

    private static string Snippet(string body, HashSet<string> terms)
    {
        foreach (var line in body.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length < 30 || l.StartsWith('#') || l.StartsWith('|')) continue;
            if (Tokenize(l).Any(terms.Contains))
                return TextUtil.Truncate(l, 220);
        }
        return "";
    }
}
