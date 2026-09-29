using System.Text.Json.Serialization;

namespace Ttb.LabelValidator;

public enum ValidationStatus
{
    [JsonStringEnumMemberName("approve")]
    Approve,
    [JsonStringEnumMemberName("reject")]
    Reject,
    [JsonStringEnumMemberName("manual_review")]
    ManualReview,
    [JsonStringEnumMemberName("malformed_input")]
    MalformedInput,
    [JsonStringEnumMemberName("external_service_unavailable")]
    ExternalServiceUnavailable
}

public sealed record Evidence(string ImageId, string Text);

public sealed record Finding(
    string Code,
    string Severity,
    string Field,
    string Message,
    string? ApplicationValue = null,
    string? LabelValue = null,
    IReadOnlyList<Evidence>? Evidence = null);

public sealed record RuleEvaluation(
    string RuleId,
    string Outcome,
    string Field,
    string Message);

public sealed class ValidationResult
{
    public string SchemaVersion { get; init; } = "1.0";
    public string ApplicationVersion { get; init; } = typeof(ValidationResult).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public required ValidationStatus Status { get; init; }
    public string? SourcePdf { get; init; }
    public string? SourceSha256 { get; init; }
    public required string RulesVersion { get; init; }
    public required string RulesFile { get; init; }
    public DateTimeOffset EvaluatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<RuleEvaluation> EvaluatedRules { get; init; }
    public string? ExternalServiceError { get; init; }
    public string? MalformedInputError { get; init; }
    public ValidationSummary Summary => new(
        Findings.Count(f => f.Severity == "reject"),
        Findings.Count(f => f.Severity == "manual_review"),
        EvaluatedRules.Count);
}

public sealed record ValidationSummary(int RejectionFindings, int ManualReviewFindings, int EvaluatedRules);

public sealed record LabelField(
    string? Raw,
    string Status,
    IReadOnlyList<Evidence> Evidence,
    string Notes);

public sealed record LabelExtraction(
    IReadOnlyDictionary<string, LabelField> Fields,
    WarningFormatting WarningFormatting);

public sealed record WarningFormatting(
    string? HeadingText,
    bool? HeadingAllCaps,
    bool? HeadingBold,
    string RelativeSize,
    IReadOnlyList<Evidence> Evidence,
    string Notes);
