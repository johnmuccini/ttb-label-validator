using System.Text.Json.Nodes;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
namespace Ttb.LabelParser;

public sealed class FormReader(PdfDocument document)
{
    public static readonly Dictionary<string, string> TextFields = new()
    {
        ["brandName"] = "6. BRAND NAME (Required)", ["fancifulName"] = "7. FANCIFUL NAME (If any)",
        ["permitNumber"] = "2.  PLANT REGISTRY/BASIC PERMIT/BREWER'S NO. (Required)",
        ["applicantNameAndAddress"] = "8. NAME AND ADDRESS OF APPLICANT AS SHOWN ON PLANT REGISTRY, BASIC",
        ["mailingAddress"] = "8a. MAILING ADDRESS, IF DIFFERENT", ["formula"] = "9.  FORMULA",
        ["grapeVarietals"] = "10. GRAPE VARIETAL(S) Wine only", ["wineAppellation"] = "11.  WINE APPELLATION (If on label)",
        ["applicationDate"] = "16.  DATE OF APPLICATION", ["signerName"] = "18.  PRINT NAME OF APPLICANT OR AUTHORIZED AGENT"
    };
    readonly Dictionary<string, DictionaryToken> fields = new();
    readonly HashSet<string> wanted = [];
    IToken? Resolve(IToken? token)
    {
        for (int i = 0; token is IndirectReferenceToken reference; i++)
        {
            if (i > 100) throw new InvalidDataException("Cyclic PDF reference.");
            token = document.Structure.GetObject(reference.Data).Data;
        }
        return token;
    }
    IToken? Get(DictionaryToken d, string key) => d.TryGet(NameToken.Create(key), out IToken token) ? Resolve(token) : null;
    static string Text(IToken? t) => t switch { StringToken s => s.Data, HexToken h => h.Data, NameToken n => "/" + n.Data, _ => "" };
    IToken? Inherited(DictionaryToken d, string key)
    {
        for (int i = 0; i < 100; i++)
        {
            var value = Get(d, key); if (value is not null) return value;
            if (Get(d, "Parent") is not DictionaryToken parent) return null;
            d = parent;
        }
        throw new InvalidDataException("Cyclic field hierarchy.");
    }
    string FullName(DictionaryToken d)
    {
        var parts = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            if (Get(d, "T") is IToken name) parts.Insert(0, Text(name));
            if (Get(d, "Parent") is not DictionaryToken parent) return string.Join('.', parts);
            d = parent;
        }
        throw new InvalidDataException("Cyclic field hierarchy.");
    }
    void Walk(IToken token, string prefix, int depth = 0)
    {
        if (depth > 100) throw new InvalidDataException("Field nesting is too deep.");
        if (Resolve(token) is not DictionaryToken d) throw new InvalidDataException("Invalid field dictionary.");
        string local = Text(Get(d, "T"));
        string name = local.Length == 0 ? prefix : prefix.Length == 0 ? local : prefix + "." + local;
        if (local.Length > 0 && !fields.TryAdd(name, d)) throw new InvalidDataException("Duplicate field: " + name);
        if (Get(d, "Kids") is ArrayToken kids) foreach (var kid in kids.Data) Walk(kid, name, depth + 1);
    }
    JsonObject Value(string name)
    {
        wanted.Add(name);
        if (!fields.TryGetValue(name, out var field)) return new() { ["raw"] = null, ["status"] = "unresolved", ["sourceField"] = name };
        string raw = Text(Inherited(field, "V")).Replace("\r\n", "\n").Replace('\r', '\n');
        return new() { ["raw"] = string.IsNullOrWhiteSpace(raw) ? null : raw, ["status"] = string.IsNullOrWhiteSpace(raw) ? "empty" : "extracted", ["sourceField"] = name };
    }
    void Choice(JsonObject result, string key, string name, Dictionary<string, string> choices)
    {
        var field = Value(name); string? raw = field["raw"]?.GetValue<string>();
        field["value"] = raw is not null && choices.TryGetValue(raw, out var selected) ? selected : null;
        if (raw is null or "/Off") field["status"] = fields.ContainsKey(name) ? "empty" : "unresolved";
        else if (!choices.ContainsKey(raw)) field["status"] = "unresolved";
        result[key] = field;
    }
    public (JsonObject Fields, JsonObject Evidence) Extract()
    {
        var root = document.Structure.Catalog.CatalogDictionary;
        if (Get(root, "AcroForm") is not DictionaryToken form || Get(form, "Fields") is not ArrayToken list)
            throw new InvalidDataException("Unsupported input: expected a fillable 04/2023 TTB form.");
        foreach (var token in list.Data) Walk(token, "");
        if (!document.GetPage(1).Text.Contains("04/2023") || TextFields.Values.Any(n => !fields.ContainsKey(n)))
            throw new InvalidDataException("Unsupported input: expected a fillable 04/2023 TTB form.");
        var result = new JsonObject();
        foreach (var pair in TextFields) result[pair.Key] = Value(pair.Value);
        Choice(result, "productCategory", "Check Box22", new() { ["/Wine"] = "Wine", ["/Spirits"] = "Distilled spirits", ["/Malt"] = "Malt beverages" });
        Choice(result, "sourceOfProduct", "Check Box34", new() { ["/Domes"] = "Domestic", ["/Import"] = "Imported" });
        var digits = new[] { "YEAR 1", "YEAR 2", "SERIAL NUMBER 1", "SERIAL NUMBER 2", "SERIAL NUMBER 3", "SERIAL NUMBER 4" }.Select(Value).ToArray();
        bool valid = digits.All(d => d["raw"]?.GetValue<string>() is string s && s.Length == 1 && char.IsAsciiDigit(s[0]));
        result["serialNumber"] = new JsonObject { ["raw"] = valid ? string.Concat(digits.Select(d => d["raw"]!.GetValue<string>())) : null,
            ["status"] = valid ? "extracted" : "unresolved", ["components"] = new JsonArray(digits.Cast<JsonNode>().ToArray()) };
        var types = new JsonObject();
        foreach (var pair in new Dictionary<string, string> {
            ["labelApproval"] = "14a. CERTIFICATE OF LABEL APPROVAL", ["exemption"] = "14b. CERTIFICATE OF EXEMPTION FROM LABEL APPROVAL",
            ["distinctiveBottle"] = "14c. DISTINCTIVE LIQUOR BOTTLE APPROVAL", ["resubmission"] = "14d. RESUBMISSION AFTER REJECTION" })
        {
            var v = Value(pair.Value); string? raw = v["raw"]?.GetValue<string>();
            bool? selected = raw == (pair.Key == "resubmission" ? "/yse" : "/yes") ? true : (raw is null or "/Off") && fields.ContainsKey(pair.Value) ? false : null;
            v["selected"] = selected; types[pair.Key] = v;
        }
        result["applicationTypes"] = types;
        return (result, WidgetEvidence());
    }
    JsonObject WidgetEvidence()
    {
        var evidence = new JsonObject();
        foreach (var page in document.GetPages())
        {
            if (Get(page.Dictionary, "Annots") is not ArrayToken annots) continue;
            foreach (var token in annots.Data)
            {
                if (Resolve(token) is not DictionaryToken widget || Text(Get(widget, "Subtype")) != "/Widget") continue;
                string name = FullName(widget); if (!wanted.Contains(name)) continue;
                if (!fields.TryGetValue(name, out var field)) throw new InvalidDataException("Orphan widget: " + name);
                string expected = Text(Inherited(field, "V"));
                if (Text(Inherited(widget, "V")) != expected) throw new InvalidDataException("Widget and field value disagree: " + name);
                if (Text(Inherited(widget, "FT")) == "/Btn")
                {
                    var ap = Get(widget, "AP") as DictionaryToken;
                    var states = ap is null ? null : Get(ap, "N") as DictionaryToken;
                    string desired = states is not null && expected.StartsWith('/') && Get(states, expected[1..]) is not null ? expected : "/Off";
                    string actual = Text(Get(widget, "AS"));
                    if ((actual.Length == 0 ? "/Off" : actual) != desired) throw new InvalidDataException("Checkbox appearance disagrees: " + name);
                }
                if (Get(widget, "Rect") is not ArrayToken rect) throw new InvalidDataException("Widget rectangle missing.");
                var box = rect.Data.Select(t => ((NumericToken)Resolve(t)!).Double).ToArray();
                evidence[name] ??= new JsonArray();
                evidence[name]!.AsArray().Add(new JsonObject { ["page"] = page.Number, ["pdfRectangle"] = Pipeline.Node(box) });
            }
        }
        return evidence;
    }
}
