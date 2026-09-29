using System.Text.Json;

namespace Ttb.LabelValidator;

public sealed class RuleSet
{
    public string SchemaVersion { get; set; } = "";
    public string RulesVersion { get; set; } = "";
    public List<string> SupportedFormRevisions { get; set; } = [];
    public NormalizationRules Normalization { get; set; } = new();
    public List<RequiredFieldRule> RequiredApplicationFields { get; set; } = [];
    public List<RequiredFieldRule> RequiredLabelFields { get; set; } = [];
    public List<ComparisonRule> Comparisons { get; set; } = [];
    public Dictionary<string, List<string>> ProductCategoryClassTypes { get; set; } = new();
    public List<AbvRule> AbvRules { get; set; } = [];
    public GovernmentWarningRule GovernmentWarning { get; set; } = new();

    public static RuleSet Load(string path)
    {
        if (!File.Exists(path)) throw new RulesConfigurationException($"Rules file was not found: {path}");
        RuleSet rules;
        try
        {
            rules = JsonSerializer.Deserialize<RuleSet>(File.ReadAllText(path), JsonOptions())
                ?? throw new RulesConfigurationException("Rules file is empty.");
        }
        catch (JsonException exception)
        {
            throw new RulesConfigurationException("Rules file is not valid JSON: " + exception.Message);
        }
        rules.Validate();
        return rules;
    }

    static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };

    void Validate()
    {
        if (SchemaVersion != "1.0") throw new RulesConfigurationException("Unsupported rules schemaVersion.");
        if (string.IsNullOrWhiteSpace(RulesVersion)) throw new RulesConfigurationException("rulesVersion is required.");
        if (SupportedFormRevisions.Count == 0) throw new RulesConfigurationException("supportedFormRevisions cannot be empty.");
        if (RequiredApplicationFields.Count == 0 || RequiredLabelFields.Count == 0)
            throw new RulesConfigurationException("Required-field rules cannot be empty.");
        if (string.IsNullOrWhiteSpace(GovernmentWarning.Heading) || string.IsNullOrWhiteSpace(GovernmentWarning.Body))
            throw new RulesConfigurationException("Government-warning heading and body are required.");
        foreach (RequiredFieldRule rule in RequiredApplicationFields.Concat(RequiredLabelFields))
            if (string.IsNullOrWhiteSpace(rule.Field) || string.IsNullOrWhiteSpace(rule.Code) || string.IsNullOrWhiteSpace(rule.Message))
                throw new RulesConfigurationException("Every required-field rule needs field, code, and message.");
        foreach (AbvRule rule in AbvRules)
            if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.ClassTypePattern))
                throw new RulesConfigurationException("Every ABV rule needs id and classTypePattern.");
    }
}

public sealed class NormalizationRules
{
    public bool IgnoreCase { get; set; } = true;
    public bool CollapseWhitespace { get; set; } = true;
    public bool IgnoreNonSubstantivePunctuation { get; set; } = true;
    public bool AllowBrandWithFancifulSuffix { get; set; } = true;
    public List<string> ProducerRolePrefixes { get; set; } = [];
}

public sealed class RequiredFieldRule
{
    public string Id { get; set; } = "";
    public string Field { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> WhenSourceOfProduct { get; set; } = [];
    public List<string> WhenProductCategories { get; set; } = [];
}

public sealed class ComparisonRule
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string ApplicationField { get; set; } = "";
    public string LabelField { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> WhenSourceOfProduct { get; set; } = [];
}

public sealed class AbvRule
{
    public string Id { get; set; } = "";
    public string ClassTypePattern { get; set; } = "";
    public decimal? Minimum { get; set; }
    public bool MinimumInclusive { get; set; } = true;
    public decimal? Maximum { get; set; }
    public bool MaximumInclusive { get; set; } = true;
    public string BelowMinimumCode { get; set; } = "ALCOHOL_CONTENT_BELOW_CLASS_MINIMUM";
    public string AboveMaximumCode { get; set; } = "ALCOHOL_CONTENT_ABOVE_CLASS_MAXIMUM";
    public string MissingCode { get; set; } = "LABEL_ALCOHOL_CONTENT_MISSING";
    public string Description { get; set; } = "";
}

public sealed class GovernmentWarningRule
{
    public string Heading { get; set; } = "";
    public string Body { get; set; } = "";
    public bool RequireHeadingAllCaps { get; set; } = true;
    public bool RequireHeadingBold { get; set; } = true;
    public List<string> RejectRelativeSizes { get; set; } = [];
    public List<string> ManualReviewRelativeSizes { get; set; } = [];
}

public sealed class RulesConfigurationException(string message) : Exception(message);
