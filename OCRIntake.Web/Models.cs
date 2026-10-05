namespace OCRIntake.Web;

public static class LabelSchema
{
    public static readonly Dictionary<string, string> Fields = new()
    {
        ["ProductName"] = "Product name", ["Brand"] = "Brand",
        ["Manufacturer"] = "Manufacturer", ["Distributor"] = "Distributor",
        ["LotBatchNumber"] = "Lot / batch number", ["ExpirationDate"] = "Expiration date",
        ["ProductSize"] = "Product size", ["WeightVolume"] = "Weight / volume",
        ["PackagingInformation"] = "Packaging information",
        ["RegulatoryStatements"] = "Regulatory statements", ["WarningLabels"] = "Warning labels",
        ["QrCodes"] = "QR code contents (only if readable)", ["OtherLabelInformation"] = "Other visible label information"
    };
}

public record DetectedField(string? Value, double? Confidence);
public record ReviewContext(string Category, string State, bool CoaRequired, bool CoaProvided);
public record ReviewRequest(Dictionary<string, string?> Values, ReviewContext Context, int Revision);
public record ApprovalRequest(bool Approved, string Inspector, int Revision);
public record Finding(string RuleId, string Severity, string Message, string Reference, string ReferenceUrl);
public record Screening(List<Finding> Findings, string[] EvaluatedRules, string Notice);
public record Intake(
    Guid Id, string FileName, DateTimeOffset CreatedAt, string Mode,
    Dictionary<string, DetectedField> Detected, Dictionary<string, string?> Values,
    ReviewContext Context, Screening Screening, int Revision = 0,
    string Status = "Pending review", string? Inspector = null, DateTimeOffset? ApprovedAt = null,
    string SourceSha256 = "", ExtractionProfile? Profile = null);

public record ExtractionField(string Key, string Name, string Description);
public record ExtractionProfile(Guid Id, int Version, string Name, ExtractionField[] Fields, DateTimeOffset CreatedAt)
{
    public string AnalyzerId => $"ocrintake-{Id:N}-v{Version}-" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Fields)))[..12].ToLowerInvariant();
}
public record ProfileRequest(string Name, ExtractionField[] Fields, int Version = 0);

public class ComplianceRule
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string State { get; set; } = "";
    public string Kind { get; set; } = "required";
    public string Field { get; set; } = "";
    public string[] Fields { get; set; } = [];
    public string Text { get; set; } = "";
    public string Severity { get; set; } = "Potential issue";
    public string Explanation { get; set; } = "";
    public string Reference { get; set; } = "";
    public string ReferenceUrl { get; set; } = "";
}
