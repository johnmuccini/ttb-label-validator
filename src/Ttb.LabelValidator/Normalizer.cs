using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ttb.LabelValidator;

public sealed partial class Normalizer(NormalizationRules rules)
{
    public string Basic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string normalized = value.Normalize(NormalizationForm.FormKC);
        if (rules.IgnoreCase) normalized = normalized.ToUpperInvariant();
        if (rules.IgnoreNonSubstantivePunctuation)
        {
            var builder = new StringBuilder(normalized.Length);
            foreach (char character in normalized)
                builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
            normalized = builder.ToString();
        }
        return rules.CollapseWhitespace ? Whitespace().Replace(normalized, " ").Trim() : normalized.Trim();
    }

    public string ExactText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : Whitespace().Replace(value.Normalize(NormalizationForm.FormKC), " ").Trim();

    public string Producer(string? value)
    {
        string candidate = value ?? "";
        foreach (string prefix in rules.ProducerRolePrefixes.OrderByDescending(value => value.Length))
            candidate = Regex.Replace(candidate, "^\\s*" + Regex.Escape(prefix) + @"\s*[:,-]?\s*", "", RegexOptions.IgnoreCase);
        return Basic(candidate);
    }

    public bool BrandMatches(string? applicationBrand, string? fancifulName, string? labelBrand)
    {
        string application = Basic(applicationBrand);
        string label = Basic(labelBrand);
        if (application.Length == 0 || label.Length == 0) return false;
        if (application == label) return true;
        if (!rules.AllowBrandWithFancifulSuffix) return false;
        string fanciful = Basic(fancifulName);
        return fanciful.Length > 0 && label == application + " " + fanciful;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
