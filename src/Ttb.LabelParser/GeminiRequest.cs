using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Ttb.LabelParser;

public sealed record GeminiArtifacts(
    string RequestPath,
    string SchemaPath,
    string InstructionsPath,
    string ManifestPath,
    string? ResponsePath,
    string Status);

public static class GeminiRequest
{
    const string Model = "gemini-3.1-flash-lite";
    const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions";
    const int InlineRequestLimit = 20_000_000;
    const int MaximumAttempts = 4;
    static readonly HashSet<int> TransientStatusCodes = [429, 500, 502, 503, 504];

    public static GeminiArtifacts Prepare(
        string outputDirectory,
        string outputName,
        JsonObject sourcePdf,
        JsonArray labelImages,
        bool send)
    {
        string promptSource = Path.Combine(AppContext.BaseDirectory, "gemini-instructions.txt");
        if (!File.Exists(promptSource))
            throw new FileNotFoundException("Gemini instructions were not published with the parser.", promptSource);
        string prompt = File.ReadAllText(promptSource);
        string prefix = Path.Combine(outputDirectory, outputName);
        string requestPath = prefix + "-gemini-request.json";
        string schemaPath = prefix + "-gemini-response.schema.json";
        string instructionsPath = prefix + "-gemini-instructions.txt";
        string manifestPath = prefix + "-gemini-request-manifest.json";
        string responsePath = prefix + "-gemini-response.json";

        var ids = labelImages.Select(asset => asset!["imageId"]!.GetValue<string>()).ToArray();
        JsonObject schema = ResponseSchema(ids);
        var input = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt } };
        foreach (JsonNode? node in labelImages)
        {
            var asset = node!.AsObject();
            string imageId = asset["imageId"]!.GetValue<string>();
            string imageFile = asset["file"]!.GetValue<string>();
            input.Add(new JsonObject { ["type"] = "text", ["text"] = "Image ID: " + imageId });
            input.Add(new JsonObject
            {
                ["type"] = "image",
                ["mime_type"] = "image/png",
                ["data"] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(outputDirectory, imageFile)))
            });
        }
        var request = new JsonObject
        {
            ["model"] = Model,
            ["input"] = input,
            ["response_format"] = new JsonObject
            {
                ["type"] = "text",
                ["mime_type"] = "application/json",
                ["schema"] = schema.DeepClone()
            }
        };
        string requestJson = request.ToJsonString(Pipeline.JsonOptions) + Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(requestJson) >= InlineRequestLimit)
            throw new InvalidOperationException("Inline Gemini request exceeds 20 MB; use the Files API.");

        File.WriteAllText(requestPath, requestJson);
        File.WriteAllText(schemaPath, schema.ToJsonString(Pipeline.JsonOptions) + Environment.NewLine);
        File.WriteAllText(instructionsPath, prompt);

        string status = "prepared_not_sent";
        string? savedResponse = null;
        int? httpStatus = null;
        string? error = null;
        long? elapsedMilliseconds = null;
        var attempts = new JsonArray();
        if (send && labelImages.Count == 0)
        {
            status = "not_sent_no_images";
        }
        else if (send)
        {
            string? apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                WriteManifest(manifestPath, sourcePdf, labelImages, requestPath, status, null, null, attempts, null, "GEMINI_API_KEY was not supplied.");
                throw new InvalidOperationException("--send-gemini requires the GEMINI_API_KEY environment variable.");
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var overall = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                if (attempt > 1)
                    Thread.Sleep(RetryDelay(attempt));
                var attemptTimer = Stopwatch.StartNew();
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                    message.Headers.Add("x-goog-api-key", apiKey);
                    message.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = client.Send(message);
                    string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    attemptTimer.Stop();
                    httpStatus = (int)response.StatusCode;
                    attempts.Add(Attempt(attempt, attemptTimer.ElapsedMilliseconds, httpStatus, response.IsSuccessStatusCode ? "succeeded" : "http_error", null));
                    File.WriteAllText(responsePath, responseBody);
                    savedResponse = responsePath;
                    if (response.IsSuccessStatusCode)
                    {
                        status = "sent";
                        error = null;
                        break;
                    }
                    error = $"Gemini returned HTTP {httpStatus}.";
                    if (!TransientStatusCodes.Contains(httpStatus.Value)) break;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    attemptTimer.Stop();
                    error = exception is TaskCanceledException ? "Gemini request timed out." : "Gemini request failed: " + exception.Message;
                    attempts.Add(Attempt(attempt, attemptTimer.ElapsedMilliseconds, null, exception is TaskCanceledException ? "timeout" : "network_error", error));
                }
            }
            overall.Stop();
            elapsedMilliseconds = overall.ElapsedMilliseconds;
            if (status != "sent") status = "send_failed";
        }

        WriteManifest(manifestPath, sourcePdf, labelImages, requestPath, status, savedResponse, httpStatus, attempts, elapsedMilliseconds, error);
        return new GeminiArtifacts(requestPath, schemaPath, instructionsPath, manifestPath, savedResponse, status);
    }

    static TimeSpan RetryDelay(int attempt)
    {
        int baseMilliseconds = 500 * (1 << (attempt - 2));
        return TimeSpan.FromMilliseconds(baseMilliseconds + Random.Shared.Next(100, 351));
    }

    static JsonObject Attempt(int number, long elapsedMilliseconds, int? httpStatus, string outcome, string? error) => new()
    {
        ["number"] = number,
        ["elapsedMilliseconds"] = elapsedMilliseconds,
        ["httpStatus"] = httpStatus,
        ["outcome"] = outcome,
        ["error"] = error
    };

    static void WriteManifest(
        string path,
        JsonObject sourcePdf,
        JsonArray images,
        string requestPath,
        string status,
        string? responsePath,
        int? httpStatus,
        JsonArray attempts,
        long? elapsedMilliseconds,
        string? error)
    {
        var manifest = new JsonObject
        {
            ["parserVersion"] = typeof(GeminiRequest).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            ["sourcePdf"] = sourcePdf.DeepClone(),
            ["status"] = status,
            ["endpoint"] = Endpoint,
            ["method"] = "POST",
            ["headers"] = new JsonObject
            {
                ["Content-Type"] = "application/json",
                ["x-goog-api-key"] = "${GEMINI_API_KEY}"
            },
            ["bodyFile"] = Path.GetFileName(requestPath),
            ["images"] = images.DeepClone(),
            ["responseFile"] = responsePath is null ? null : Path.GetFileName(responsePath),
            ["httpStatus"] = httpStatus,
            ["attemptCount"] = attempts.Count,
            ["attempts"] = attempts.DeepClone(),
            ["elapsedMilliseconds"] = elapsedMilliseconds,
            ["error"] = error,
            ["liveApiValidated"] = status == "sent"
        };
        File.WriteAllText(path, manifest.ToJsonString(Pipeline.JsonOptions) + Environment.NewLine);
    }

    static JsonObject ResponseSchema(IEnumerable<string> imageIds)
    {
        JsonObject EvidenceSchema() => ObjectSchema(new Dictionary<string, JsonNode>
        {
            ["imageId"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(imageIds) },
            ["text"] = new JsonObject { ["type"] = "string" }
        });
        JsonObject FieldSchema() => ObjectSchema(new Dictionary<string, JsonNode>
        {
            ["raw"] = new JsonObject { ["type"] = Strings(["string", "null"]) },
            ["status"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(["observed", "not_found", "unreadable", "conflicting"]) },
            ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = EvidenceSchema() },
            ["notes"] = new JsonObject { ["type"] = "string" }
        });
        var fields = new Dictionary<string, JsonNode>();
        foreach (string name in new[]
        {
            "brandName", "classTypeDesignation", "wineAppellation", "alcoholContent", "netContents",
            "bottlerProducerNameAndAddress", "countryOfOrigin", "governmentWarning", "importerNameAndAddress"
        }) fields[name] = FieldSchema();
        return ObjectSchema(new Dictionary<string, JsonNode>
        {
            ["fields"] = ObjectSchema(fields),
            ["warningFormatting"] = ObjectSchema(new Dictionary<string, JsonNode>
            {
                ["headingText"] = new JsonObject { ["type"] = Strings(["string", "null"]) },
                ["headingAllCaps"] = new JsonObject { ["type"] = Strings(["boolean", "null"]) },
                ["headingBold"] = new JsonObject { ["type"] = Strings(["boolean", "null"]) },
                ["relativeSize"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(["smaller", "similar", "larger", "undetermined"]) },
                ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = EvidenceSchema() },
                ["notes"] = new JsonObject { ["type"] = "string" }
            })
        });
    }

    static JsonObject ObjectSchema(IReadOnlyDictionary<string, JsonNode> properties)
    {
        var propertyObject = new JsonObject();
        foreach (var pair in properties) propertyObject[pair.Key] = pair.Value;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = propertyObject,
            ["required"] = Strings(properties.Keys),
            ["additionalProperties"] = false
        };
    }

    static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
