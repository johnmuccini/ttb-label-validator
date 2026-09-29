using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ttb.LabelValidator;

public sealed class ValidationEngine(RuleSet rules, string rulesPath)
{
    readonly Normalizer normalizer = new(rules.Normalization);
    readonly List<Finding> findings = [];
    readonly List<RuleEvaluation> evaluations = [];

    public ValidationResult Validate(string applicationPath, string? responsePath, string? manifestPath)
    {
        findings.Clear();
        evaluations.Clear();
        JsonDocument application;
        try { application = JsonDocument.Parse(File.ReadAllText(applicationPath)); }
        catch (Exception exception) when (exception is IOException or JsonException)
        { return Malformed("Application input is not readable JSON: " + exception.Message); }

        using (application)
        {
            JsonElement root = application.RootElement;
            string? sourcePdf = null, sha = null;
            try
            {
                RequireObject(root, "application root");
                JsonElement source = RequireProperty(root, "sourcePdf", JsonValueKind.Object);
                sourcePdf = ReadOptionalString(source, "originalFileName") ?? ReadOptionalString(source, "fileName");
                sha = ReadOptionalString(source, "sha256");
                string formRevision = ReadOptionalString(source, "formRevision") ?? throw new InputStructureException("sourcePdf.formRevision is required.");
                if (!rules.SupportedFormRevisions.Contains(formRevision, StringComparer.Ordinal))
                    return Malformed($"Unsupported Form 5100.31 revision: {formRevision}", sourcePdf, sha);
                JsonElement applicationFields = RequireProperty(root, "applicationFields", JsonValueKind.Object);
                JsonElement labelImages = RequireProperty(root, "labelImages", JsonValueKind.Array);
                ValidateApplicationStructure(applicationFields);
                EvaluateRequiredApplicationFields(applicationFields);

                if (labelImages.GetArrayLength() == 0)
                {
                    Reject("LABEL_MISSING", "label", "No label images were attached to the application.");
                    return Complete(sourcePdf, sha);
                }

                LabelExtraction extraction;
                try
                {
                    string? resolvedResponse = ResolveResponsePath(applicationPath, responsePath);
                    if (resolvedResponse is not null)
                        extraction = GeminiResponseReader.Read(resolvedResponse);
                    else if (root.TryGetProperty("labelExtraction", out JsonElement embedded) && embedded.ValueKind == JsonValueKind.Object && embedded.TryGetProperty("warningFormatting", out _))
                        extraction = GeminiResponseReader.ReadEmbedded(embedded);
                    else
                        return External(ServiceError(manifestPath) ?? "Gemini response is unavailable.", sourcePdf, sha);
                }
                catch (ProviderResponseException exception)
                { return External(exception.Message, sourcePdf, sha); }
                catch (IOException exception)
                { return External("Gemini response could not be read: " + exception.Message, sourcePdf, sha); }

                ValidateExtractionFields(extraction);
                string sourceOfProduct = ApplicationValue(applicationFields, "sourceOfProduct") ?? "";
                string applicationCategory = ApplicationValue(applicationFields, "productCategory") ?? "";
                string? inferredCategory = InferCategory(extraction);

                EvaluateRequiredLabelFields(extraction, sourceOfProduct, inferredCategory);
                EvaluateComparisons(applicationFields, extraction, sourceOfProduct, applicationCategory, inferredCategory);
                EvaluateAbv(extraction);
                EvaluateGovernmentWarning(extraction);
                return Complete(sourcePdf, sha);
            }
            catch (InputStructureException exception)
            { return Malformed(exception.Message, sourcePdf, sha); }
            catch (ProviderResponseException exception)
            { return External(exception.Message, sourcePdf, sha); }
        }
    }

    void ValidateApplicationStructure(JsonElement fields)
    {
        var names = rules.RequiredApplicationFields.Select(rule => rule.Field)
            .Concat(rules.Comparisons.Select(rule => rule.ApplicationField)).Distinct(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!fields.TryGetProperty(name, out JsonElement field) || field.ValueKind != JsonValueKind.Object)
                throw new InputStructureException($"applicationFields.{name} is missing or invalid.");
            string status = ReadOptionalString(field, "status") ?? throw new InputStructureException($"applicationFields.{name}.status is required.");
            if (status is not ("extracted" or "empty")) throw new InputStructureException($"applicationFields.{name}.status is invalid.");
        }
    }

    void ValidateExtractionFields(LabelExtraction extraction)
    {
        var required = rules.RequiredLabelFields.Select(rule => rule.Field)
            .Concat(rules.Comparisons.Select(rule => rule.LabelField))
            .Concat(["classTypeDesignation", "alcoholContent", "governmentWarning"])
            .Distinct(StringComparer.Ordinal);
        foreach (string name in required)
            if (!extraction.Fields.ContainsKey(name)) throw new ProviderResponseException($"Gemini response is missing fields.{name}.");
    }

    void EvaluateRequiredApplicationFields(JsonElement fields)
    {
        foreach (RequiredFieldRule rule in rules.RequiredApplicationFields)
        {
            string? value = ApplicationValue(fields, rule.Field);
            if (string.IsNullOrWhiteSpace(value)) Reject(rule.Code, rule.Field, rule.Message, applicationValue: value);
            else Pass(rule.Id, rule.Field, "Required application field is present.");
        }
    }

    void EvaluateRequiredLabelFields(LabelExtraction extraction, string sourceOfProduct, string? category)
    {
        foreach (RequiredFieldRule rule in rules.RequiredLabelFields)
        {
            if (rule.WhenSourceOfProduct.Count > 0 && !rule.WhenSourceOfProduct.Contains(sourceOfProduct, StringComparer.OrdinalIgnoreCase)) continue;
            if (rule.WhenProductCategories.Count > 0 && (category is null || !rule.WhenProductCategories.Contains(category, StringComparer.OrdinalIgnoreCase))) continue;
            LabelField field = extraction.Fields[rule.Field];
            switch (field.Status)
            {
                case "observed": Pass(rule.Id, rule.Field, "Required label field is present."); break;
                case "not_found": Reject(rule.Code, rule.Field, rule.Message, labelValue: null, evidence: field.Evidence); break;
                case "unreadable": Manual("LABEL_FIELD_UNREADABLE", rule.Field, $"The label {Display(rule.Field)} could not be read.", field); break;
                case "conflicting": Manual("LABEL_FIELD_CONFLICTING", rule.Field, $"The label contains conflicting values for {Display(rule.Field)}.", field); break;
            }
        }
    }

    void EvaluateComparisons(JsonElement applicationFields, LabelExtraction extraction, string sourceOfProduct, string applicationCategory, string? inferredCategory)
    {
        foreach (ComparisonRule rule in rules.Comparisons)
        {
            if (rule.WhenSourceOfProduct.Count > 0 && !rule.WhenSourceOfProduct.Contains(sourceOfProduct, StringComparer.OrdinalIgnoreCase)) continue;
            string? applicationValue = ApplicationValue(applicationFields, rule.ApplicationField);
            if (string.IsNullOrWhiteSpace(applicationValue)) continue;

            if (rule.Kind == "category")
            {
                if (inferredCategory is null)
                {
                    LabelField classType = extraction.Fields["classTypeDesignation"];
                    if (classType.Status == "observed")
                        Manual("PRODUCT_CATEGORY_UNDETERMINED", "productCategory", "The product category could not be determined from the observed label class/type.", classType);
                    continue;
                }
                if (!string.Equals(applicationCategory, inferredCategory, StringComparison.OrdinalIgnoreCase))
                    Reject(rule.Code, "productCategory", rule.Message, applicationCategory, inferredCategory, extraction.Fields["classTypeDesignation"].Evidence);
                else Pass(rule.Id, "productCategory", "Application and label product categories match.");
                continue;
            }

            LabelField label = extraction.Fields[rule.LabelField];
            if (label.Status != "observed") continue;
            bool matches = rule.Kind switch
            {
                "brand" => normalizer.BrandMatches(applicationValue, ApplicationValue(applicationFields, "fancifulName"), label.Raw),
                "producer" => normalizer.Producer(applicationValue) == normalizer.Producer(label.Raw),
                _ => normalizer.Basic(applicationValue) == normalizer.Basic(label.Raw)
            };
            if (!matches) Reject(rule.Code, rule.ApplicationField, rule.Message, applicationValue, label.Raw, label.Evidence);
            else Pass(rule.Id, rule.ApplicationField, "Application and label values match after configured normalization.");
        }
    }

    string? InferCategory(LabelExtraction extraction)
    {
        LabelField classType = extraction.Fields["classTypeDesignation"];
        if (classType.Status != "observed") return null;
        string normalized = normalizer.Basic(classType.Raw);
        foreach ((string category, List<string> designations) in rules.ProductCategoryClassTypes)
            if (designations.Any(value => normalizer.Basic(value) == normalized)) return category;
        return null;
    }

    void EvaluateAbv(LabelExtraction extraction)
    {
        LabelField classType = extraction.Fields["classTypeDesignation"];
        if (classType.Status != "observed") return;
        AbvRule? rule = rules.AbvRules.FirstOrDefault(item => Regex.IsMatch(classType.Raw!, item.ClassTypePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if (rule is null) return;
        LabelField alcohol = extraction.Fields["alcoholContent"];
        if (alcohol.Status == "not_found") { Reject(rule.MissingCode, "alcoholContent", $"Alcohol content is required to evaluate {classType.Raw}."); return; }
        if (alcohol.Status == "unreadable") { Manual("ALCOHOL_CONTENT_UNREADABLE", "alcoholContent", "Alcohol content could not be read.", alcohol); return; }
        if (alcohol.Status == "conflicting") { Manual("ALCOHOL_CONTENT_CONFLICTING", "alcoholContent", "Conflicting alcohol-content values were found.", alcohol); return; }
        Match match = Regex.Match(alcohol.Raw!, @"(?<!\d)(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal abv))
        { Manual("ALCOHOL_CONTENT_UNPARSEABLE", "alcoholContent", "The observed alcohol content could not be converted to a percentage.", alcohol); return; }

        bool below = rule.Minimum is decimal minimum && (abv < minimum || (!rule.MinimumInclusive && abv == minimum));
        bool above = rule.Maximum is decimal maximum && (abv > maximum || (!rule.MaximumInclusive && abv == maximum));
        if (below) Reject(rule.BelowMinimumCode, "alcoholContent", $"{rule.Description} The label states {abv}% ABV.", labelValue: alcohol.Raw, evidence: alcohol.Evidence);
        if (above) Reject(rule.AboveMaximumCode, "alcoholContent", $"{rule.Description} The label states {abv}% ABV.", labelValue: alcohol.Raw, evidence: alcohol.Evidence);
        if (!below && !above) Pass(rule.Id, "alcoholContent", $"The label ABV of {abv}% is within the configured range.");
    }

    void EvaluateGovernmentWarning(LabelExtraction extraction)
    {
        LabelField warning = extraction.Fields["governmentWarning"];
        if (warning.Status == "not_found") { Reject("GOVERNMENT_WARNING_MISSING", "governmentWarning", "The government warning is missing."); return; }
        if (warning.Status == "unreadable") { Manual("GOVERNMENT_WARNING_UNREADABLE", "governmentWarning", "The government warning could not be read.", warning); return; }
        if (warning.Status == "conflicting") { Manual("GOVERNMENT_WARNING_CONFLICTING", "governmentWarning", "Conflicting government-warning text was found.", warning); return; }

        string body = WarningBody(warning.Raw!);
        if (normalizer.ExactText(body) != normalizer.ExactText(rules.GovernmentWarning.Body))
            Reject("GOVERNMENT_WARNING_TEXT_INCORRECT", "governmentWarning", "The government warning text does not exactly match the configured required text.", labelValue: warning.Raw, evidence: warning.Evidence);
        else Pass("government-warning-text", "governmentWarning", "Government-warning wording matches.");

        WarningFormatting formatting = extraction.WarningFormatting;
        if (formatting.HeadingText is null)
            Manual("GOVERNMENT_WARNING_HEADING_UNDETERMINED", "governmentWarning", "The warning heading could not be read.", warning);
        else if (!string.Equals(normalizer.Basic(formatting.HeadingText), normalizer.Basic(rules.GovernmentWarning.Heading), StringComparison.Ordinal))
            Reject("GOVERNMENT_WARNING_HEADING_INCORRECT", "governmentWarning", "The government-warning heading text is incorrect.", labelValue: formatting.HeadingText, evidence: formatting.Evidence);

        if (rules.GovernmentWarning.RequireHeadingAllCaps)
        {
            if (formatting.HeadingAllCaps is false) Reject("GOVERNMENT_WARNING_NOT_ALL_CAPS", "governmentWarning", "The GOVERNMENT WARNING heading is not entirely uppercase.", evidence: formatting.Evidence);
            else if (formatting.HeadingAllCaps is null) Manual("GOVERNMENT_WARNING_CAPITALIZATION_UNDETERMINED", "governmentWarning", "The warning heading capitalization could not be determined.", warning);
            else Pass("government-warning-capitalization", "governmentWarning", "Government-warning heading is uppercase.");
        }
        if (rules.GovernmentWarning.RequireHeadingBold)
        {
            if (formatting.HeadingBold is false) Reject("GOVERNMENT_WARNING_NOT_BOLD", "governmentWarning", "The GOVERNMENT WARNING heading is not bold.", evidence: formatting.Evidence);
            else if (formatting.HeadingBold is null) Manual("GOVERNMENT_WARNING_BOLDING_UNDETERMINED", "governmentWarning", "The warning heading bolding could not be determined.", warning);
            else Pass("government-warning-bolding", "governmentWarning", "Government-warning heading is bold.");
        }
        if (rules.GovernmentWarning.RejectRelativeSizes.Contains(formatting.RelativeSize, StringComparer.OrdinalIgnoreCase))
            Reject("GOVERNMENT_WARNING_RELATIVELY_SMALL", "governmentWarning", "The government-warning body text is visibly smaller than ordinary informational text on the label.", evidence: formatting.Evidence);
        else if (rules.GovernmentWarning.ManualReviewRelativeSizes.Contains(formatting.RelativeSize, StringComparer.OrdinalIgnoreCase))
            Manual("GOVERNMENT_WARNING_RELATIVE_SIZE_UNDETERMINED", "governmentWarning", "The warning's apparent relative size could not be determined.", warning);
        else Pass("government-warning-relative-size", "governmentWarning", $"Government-warning relative size was reported as {formatting.RelativeSize}.");
    }

    ValidationResult Complete(string? sourcePdf, string? sha)
    {
        ValidationStatus status = findings.Any(item => item.Severity == "reject") ? ValidationStatus.Reject
            : findings.Any(item => item.Severity == "manual_review") ? ValidationStatus.ManualReview
            : ValidationStatus.Approve;
        return Result(status, sourcePdf, sha);
    }

    ValidationResult Malformed(string message, string? sourcePdf = null, string? sha = null) =>
        Result(ValidationStatus.MalformedInput, sourcePdf, sha, malformed: message);
    ValidationResult External(string message, string? sourcePdf, string? sha) =>
        Result(ValidationStatus.ExternalServiceUnavailable, sourcePdf, sha, external: message);
    ValidationResult Result(ValidationStatus status, string? sourcePdf, string? sha, string? external = null, string? malformed = null) => new()
    {
        Status = status,
        SourcePdf = sourcePdf,
        SourceSha256 = sha,
        RulesVersion = rules.RulesVersion,
        RulesFile = Path.GetFileName(rulesPath),
        Findings = findings.ToArray(),
        EvaluatedRules = evaluations.ToArray(),
        ExternalServiceError = external,
        MalformedInputError = malformed
    };

    void Reject(string code, string field, string message, string? applicationValue = null, string? labelValue = null, IReadOnlyList<Evidence>? evidence = null) =>
        findings.Add(new Finding(code, "reject", field, message, applicationValue, labelValue, evidence ?? []));
    void Manual(string code, string field, string message, LabelField fieldValue) =>
        findings.Add(new Finding(code, "manual_review", field, message, LabelValue: fieldValue.Raw, Evidence: fieldValue.Evidence));
    void Pass(string ruleId, string field, string message) => evaluations.Add(new RuleEvaluation(ruleId, "pass", field, message));

    static string WarningBody(string raw)
    {
        int colon = raw.IndexOf(':');
        return colon >= 0 ? raw[(colon + 1)..].Trim() : raw;
    }
    static string Display(string value) => Regex.Replace(value, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();

    static string? ResolveResponsePath(string applicationPath, string? responsePath)
    {
        if (!string.IsNullOrWhiteSpace(responsePath))
        {
            string full = Path.GetFullPath(responsePath);
            if (!File.Exists(full)) throw new IOException("Gemini response file was not found: " + full);
            return full;
        }
        string derived = Path.Combine(Path.GetDirectoryName(applicationPath)!, Path.GetFileNameWithoutExtension(applicationPath) + "-gemini-response.json");
        return File.Exists(derived) ? derived : null;
    }

    static string? ServiceError(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            string? status = ReadOptionalString(document.RootElement, "status");
            int? http = document.RootElement.TryGetProperty("httpStatus", out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : null;
            return $"Gemini request status is {status ?? "unknown"}" + (http is null ? "." : $" (HTTP {http}).");
        }
        catch { return "Gemini request failed and its manifest could not be read."; }
    }

    static string? ApplicationValue(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out JsonElement field) || field.ValueKind != JsonValueKind.Object)
            return null;
        string? value = ReadOptionalString(field, "value");
        return value ?? ReadOptionalString(field, "raw");
    }

    static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InputStructureException(name + " must be an object.");
    }
    static JsonElement RequireProperty(JsonElement parent, string name, JsonValueKind kind)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != kind)
            throw new InputStructureException(name + " is missing or invalid.");
        return value;
    }
    static string? ReadOptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InputStructureException(name + " must be a string or null.");
        return value.GetString();
    }
}

public sealed class InputStructureException(string message) : Exception(message);
