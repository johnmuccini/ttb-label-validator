using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ttb.LabelValidator;

internal static class Cli
{
    static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static int Run(string[] args)
    {
        try
        {
            string? application = null, response = null, manifest = null, output = null, rulesPath = null;
            for (int index = 0; index < args.Length; index++)
                switch (args[index])
                {
                    case "--application": application = Next(args, ref index); break;
                    case "--gemini-response": response = Next(args, ref index); break;
                    case "--gemini-manifest": manifest = Next(args, ref index); break;
                    case "--rules": rulesPath = Next(args, ref index); break;
                    case "--output":
                    case "--out": output = Next(args, ref index); break;
                    case "--help":
                    case "-h": Help(); return 0;
                    default: throw new ArgumentException("Unknown argument: " + args[index]);
                }
            if (application is null) throw new ArgumentException("--application is required.");
            if (output is null) throw new ArgumentException("--output is required.");
            application = Path.GetFullPath(application);
            output = Path.GetFullPath(output);
            rulesPath = Path.GetFullPath(rulesPath ?? Path.Combine(AppContext.BaseDirectory, "validation-rules.json"));
            if (response is not null) response = Path.GetFullPath(response);
            if (manifest is not null) manifest = Path.GetFullPath(manifest);

            RuleSet rules = RuleSet.Load(rulesPath);
            ValidationResult result = new ValidationEngine(rules, rulesPath).Validate(application, response, manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(result, OutputOptions) + Environment.NewLine);
            Console.WriteLine(output);
            Console.WriteLine(JsonSerializer.Serialize(result.Status, OutputOptions));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or RulesConfigurationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("ERROR: " + exception.Message);
            return 1;
        }
    }

    static string Next(string[] args, ref int index) => ++index < args.Length ? args[index] : throw new ArgumentException("Missing option value.");
    static void Help() => Console.WriteLine("Ttb.LabelValidator --application application.json --output result.json [--gemini-response response.json] [--gemini-manifest manifest.json] [--rules validation-rules.json]");
}
