namespace OCRIntake.Web;

public class ComplianceService
{
    private readonly ComplianceRule[] rules;

    public ComplianceService(IConfiguration configuration)
    {
        rules = configuration.GetSection("Compliance:Rules").Get<ComplianceRule[]>() ?? [];
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Reference) ||
                string.IsNullOrWhiteSpace(rule.Explanation) ||
                !Uri.TryCreate(rule.ReferenceUrl, UriKind.Absolute, out var url) || url.Scheme != "https" ||
                !new[] { "required", "requiredAny", "missingText", "prohibitedText", "coa" }.Contains(rule.Kind) ||
                (rule.Kind is not ("coa" or "requiredAny") && !ProfileStore.ValidKey(rule.Field)) ||
                (rule.Kind == "requiredAny" && (rule.Fields.Length == 0 || rule.Fields.Any(field => !ProfileStore.ValidKey(field)))) ||
                (rule.Kind is "missingText" or "prohibitedText" && string.IsNullOrWhiteSpace(rule.Text)))
                throw new InvalidOperationException("Invalid compliance rule configuration.");
        }
    }

    public Screening Screen(Dictionary<string, string?> values, ReviewContext context)
    {
        var contextual = rules.Where(r =>
            (r.Category.Length == 0 || r.Category.Equals(context.Category, StringComparison.OrdinalIgnoreCase)) &&
            (r.State.Length == 0 || r.State.Equals(context.State, StringComparison.OrdinalIgnoreCase))).ToArray();
        var applicable = contextual.Where(rule => rule.Kind == "coa" ||
            (rule.Kind == "requiredAny" ? rule.Fields.All(values.ContainsKey) : values.ContainsKey(rule.Field))).ToArray();
        var skipped = contextual.Except(applicable).Select(rule => rule.Id).ToArray();
        var findings = new List<Finding>();
        foreach (var rule in applicable)
        {
            var value = values.GetValueOrDefault(rule.Field) ?? "";
            var flagged = rule.Kind switch
            {
                "required" => string.IsNullOrWhiteSpace(value),
                "requiredAny" => rule.Fields.All(field => string.IsNullOrWhiteSpace(values.GetValueOrDefault(field))),
                "missingText" => !value.Contains(rule.Text, StringComparison.OrdinalIgnoreCase),
                "prohibitedText" => value.Contains(rule.Text, StringComparison.OrdinalIgnoreCase),
                "coa" => context.CoaRequired && !context.CoaProvided,
                _ => false
            };
            if (flagged)
                findings.Add(new(rule.Id, rule.Severity, rule.Explanation, rule.Reference, rule.ReferenceUrl));
        }
        return new(findings, applicable.Select(r => r.Id).ToArray(),
            (skipped.Length == 0 ? "" : $"Not assessed because fields are outside this profile: {string.Join(", ", skipped)}. ") +
            "Screening is advisory, not a legal determination. Only the listed configured rules were evaluated. " +
            "Missing data may reflect incomplete photography or extraction errors. Warnings, disclosures, product " +
            "restrictions, state requirements and COA obligations are unassessed unless applicable rules are configured. " +
            "An inspector must review the source image and applicable regulations; no findings does not mean compliance.");
    }
}
