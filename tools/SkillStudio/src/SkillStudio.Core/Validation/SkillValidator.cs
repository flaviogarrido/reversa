using SkillStudio.Core.Model;

namespace SkillStudio.Core.Validation;

public sealed class SkillValidator
{
    private readonly IReadOnlyList<IValidationRule> _rules;

    public SkillValidator(IEnumerable<IValidationRule>? rules = null)
    {
        _rules = rules?.ToList() ?? DefaultRules();
    }

    public static IReadOnlyList<IValidationRule> DefaultRules() =>
    [
        new NameRules(),
        new DescriptionRules(),
        new InterfaceRules(),
        new MetadataRules(),
        new BodyRules(),
        new FileRules(),
        new SecurityRules(),
        new PackageRules(),
    ];

    public ValidationReport Validate(SkillDraft draft, ValidationContext? context = null)
    {
        context ??= ValidationContext.Empty;
        var issues = _rules
            .SelectMany(r => r.Validate(draft, context))
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.RuleId, StringComparer.Ordinal)
            .ToList();
        return new ValidationReport(issues);
    }
}
