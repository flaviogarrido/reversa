using SkillStudio.Core.Model;
using SkillStudio.Core.Validation;

namespace SkillStudio.Core.Assistant;

/// <summary>Alteração parcial do rascunho proposta pelo assistente. Campos nulos não mudam.</summary>
public sealed class DraftPatch
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public InvocationMode? Invocation { get; set; }
    public string? DisplayName { get; set; }
    public string? ShortDescription { get; set; }
    public string? License { get; set; }
    public string? Compatibility { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Phase { get; set; }
    public string? Team { get; set; }
    public string? Role { get; set; }
    public string? Stage { get; set; }
    public string? Body { get; set; }
    public List<SkillFile> UpsertFiles { get; set; } = [];
    public List<string> RemoveFiles { get; set; } = [];

    public IEnumerable<string> ChangedFields()
    {
        if (Name is not null) yield return "nome";
        if (Description is not null) yield return "description";
        if (Invocation is not null) yield return "invocação";
        if (DisplayName is not null) yield return "display_name";
        if (ShortDescription is not null) yield return "short_description";
        if (License is not null || Compatibility is not null) yield return "licença/compatibilidade";
        if (Author is not null || Version is not null || Phase is not null || Team is not null || Role is not null || Stage is not null)
            yield return "metadata";
        if (Body is not null) yield return "instruções";
        if (UpsertFiles.Count > 0) yield return $"{UpsertFiles.Count} referência(s)";
        if (RemoveFiles.Count > 0) yield return $"{RemoveFiles.Count} remoção(ões)";
    }

    /// <summary>Aplica o patch em uma cópia do rascunho (usado pelo host e pelos testes).</summary>
    public SkillDraft ApplyTo(SkillDraft source)
    {
        var d = source.Clone();
        if (Name is not null) d.Name = Name.Trim();
        if (Description is not null) d.Description = Description.Trim();
        if (Invocation is not null) d.Invocation = Invocation.Value;
        if (DisplayName is not null) d.DisplayName = DisplayName.Trim();
        if (ShortDescription is not null) d.ShortDescription = ShortDescription.Trim();
        if (License is not null) d.License = License.Trim();
        if (Compatibility is not null) d.Compatibility = Compatibility.Trim();
        if (Author is not null) d.Metadata.Author = Author.Trim();
        if (Version is not null) d.Metadata.Version = Version.Trim();
        if (Phase is not null) d.Metadata.Phase = Phase.Trim();
        if (Team is not null) d.Metadata.Team = Team.Trim();
        if (Role is not null) d.Metadata.Role = Role.Trim();
        if (Stage is not null) d.Metadata.Stage = Stage.Trim();
        if (Body is not null) d.Body = Body;
        foreach (var path in RemoveFiles)
            d.Files.RemoveAll(f => f.RelativePath == path && !f.IsBinary);
        foreach (var file in UpsertFiles)
        {
            var existing = d.Files.FirstOrDefault(f => f.RelativePath == file.RelativePath);
            if (existing is null) d.Files.Add(file.Clone());
            else if (!existing.IsBinary) existing.Content = file.Content;
        }
        return d;
    }
}

/// <summary>Ponte entre o assistente e o editor (a UI implementa e faz o marshalling de thread).</summary>
public interface IDraftHost
{
    SkillDraft GetDraft();

    /// <summary>Aplica o patch no editor e devolve o rascunho resultante.</summary>
    SkillDraft ApplyPatch(DraftPatch patch);

    ValidationReport Validate(SkillDraft draft);
}
