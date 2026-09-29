using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ttb.LabelWeb;

public sealed class JobService(ApiKeyStore keys)
{
    const int MaximumFiles = 300;
    const long MaximumFileBytes = 30 * 1024 * 1024;
    const int MaximumConcurrency = 2;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    readonly ConcurrentDictionary<string, JobState> jobs = new(StringComparer.Ordinal);
    readonly string root = Path.Combine(Path.GetTempPath(), "TtbLabelValidator", "jobs");
    readonly string baseDirectory = AppContext.BaseDirectory;

    string ParserPath => Environment.GetEnvironmentVariable("TTB_PARSER_PATH") ?? Path.Combine(baseDirectory, "components", "parser", "Ttb.LabelParser.exe");
    string ValidatorPath => Environment.GetEnvironmentVariable("TTB_VALIDATOR_PATH") ?? Path.Combine(baseDirectory, "components", "validator", "Ttb.LabelValidator.exe");
    string RulesPath => Environment.GetEnvironmentVariable("TTB_RULES_PATH") ?? Path.Combine(baseDirectory, "rules", "validation-rules.json");
    string DemoRoot => Environment.GetEnvironmentVariable("TTB_DEMO_DATA_PATH") ?? Path.Combine(baseDirectory, "demo-data");

    public static bool DemoAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "demo-data", "run-manifest.json"))
        || (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TTB_DEMO_DATA_PATH")) && File.Exists(Path.Combine(Environment.GetEnvironmentVariable("TTB_DEMO_DATA_PATH")!, "run-manifest.json")));

    public static string ReadRulesVersion()
    {
        string path = Environment.GetEnvironmentVariable("TTB_RULES_PATH") ?? Path.Combine(AppContext.BaseDirectory, "rules", "validation-rules.json");
        try { return JsonNode.Parse(File.ReadAllText(path))?["rulesVersion"]?.GetValue<string>() ?? "unknown"; }
        catch { return "unknown"; }
    }

    public async Task<object> CreateLiveJobAsync(IFormFileCollection files, CancellationToken cancellation)
    {
        if (files.Count is < 1 or > MaximumFiles) throw new ArgumentException($"Choose between 1 and {MaximumFiles} PDF files.");
        EnsureComponents();
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        var items = new List<JobItem>();
        for (int index = 0; index < files.Count; index++)
        {
            IFormFile file = files[index];
            if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{file.FileName} is not a PDF file.");
            if (file.Length is <= 0 or > MaximumFileBytes)
                throw new ArgumentException($"{file.FileName} must be between 1 byte and {MaximumFileBytes / 1024 / 1024} MB.");
            string itemDirectory = Path.Combine(directory, $"{index + 1:000}");
            Directory.CreateDirectory(itemDirectory);
            string saved = Path.Combine(itemDirectory, "source.pdf");
            await using (FileStream destination = File.Create(saved)) await file.CopyToAsync(destination, cancellation);
            await using (FileStream check = File.OpenRead(saved))
            {
                byte[] signature = new byte[5];
                if (await check.ReadAsync(signature, cancellation) != 5 || System.Text.Encoding.ASCII.GetString(signature) != "%PDF-")
                    throw new ArgumentException($"{file.FileName} does not have a valid PDF signature.");
            }
            items.Add(new JobItem(index + 1, Path.GetFileName(file.FileName), saved, itemDirectory, false, null));
        }
        var job = new JobState(id, "live", items);
        jobs[id] = job;
        _ = RunJobAsync(job, keys.Get());
        return Snapshot(job);
    }

    public object CreateSyntheticDemoJob()
    {
        EnsureComponents();
        string manifestPath = Path.Combine(DemoRoot, "run-manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("The bundled synthetic demonstration data is unavailable.");
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        var items = new List<JobItem>();
        int index = 0;
        foreach (JsonNode? node in manifest["cases"]!.AsArray())
        {
            JsonObject entry = node!.AsObject();
            index++;
            string pdf = Path.Combine(DemoRoot, entry["pdf"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
            string response = Path.Combine(DemoRoot, entry["geminiResponse"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
            string itemDirectory = Path.Combine(directory, $"{index:000}");
            Directory.CreateDirectory(itemDirectory);
            items.Add(new JobItem(index, Path.GetFileName(pdf), pdf, itemDirectory, true, response));
        }
        var job = new JobState(id, "synthetic_demo", items);
        jobs[id] = job;
        _ = RunJobAsync(job, null);
        return Snapshot(job);
    }

    public bool TrySnapshot(string id, out object? snapshot)
    {
        if (!jobs.TryGetValue(id, out JobState? job)) { snapshot = null; return false; }
        snapshot = Snapshot(job); return true;
    }

    public bool Cancel(string id)
    {
        if (!jobs.TryGetValue(id, out JobState? job)) return false;
        job.Cancellation.Cancel();
        return true;
    }

    public bool TryExport(string id, out byte[]? content)
    {
        if (!jobs.TryGetValue(id, out JobState? job)) { content = null; return false; }
        content = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Snapshot(job), JsonOptions) + Environment.NewLine);
        return true;
    }

    async Task RunJobAsync(JobState job, string? apiKey)
    {
        using var gate = new SemaphoreSlim(MaximumConcurrency);
        try
        {
            Task[] tasks = job.Items.Select(async item =>
            {
                await gate.WaitAsync(job.Cancellation.Token);
                try { await ProcessItemAsync(job, item, apiKey, job.Cancellation.Token); }
                finally { gate.Release(); }
            }).ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            foreach (JobItem item in job.Items.Where(item => item.Status == "queued" || item.Phase != "complete"))
            { item.Status = "cancelled"; item.Phase = "complete"; }
        }
        finally { job.CompletedUtc = DateTimeOffset.UtcNow; }
    }

    async Task ProcessItemAsync(JobState job, JobItem item, string? apiKey, CancellationToken cancellation)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            item.Status = "processing";
            item.Phase = "parsing";
            var parserArguments = new List<string> { "--input", item.SourcePath, "--output", item.Directory, "--name", "application" };
            if (!item.Synthetic) parserArguments.Add("--send-gemini");
            ProcessResult parser = await ProcessRunner.RunAsync(ParserPath, parserArguments, apiKey, cancellation);
            item.ParserMilliseconds = parser.ElapsedMilliseconds;
            if (parser.ExitCode != 0)
            {
                item.Status = "malformed_input";
                item.Error = CleanError(parser.StandardError);
                return;
            }

            string application = Path.Combine(item.Directory, "application.json");
            string requestManifest = Path.Combine(item.Directory, "application-gemini-request-manifest.json");
            item.GeminiMilliseconds = ReadLong(requestManifest, "elapsedMilliseconds") ?? 0;
            item.GeminiAttempts = (int)(ReadLong(requestManifest, "attemptCount") ?? 0);
            item.Phase = "validating";
            string resultPath = Path.Combine(item.Directory, "application-validation-result.json");
            var validatorArguments = new List<string>
            {
                "--application", application, "--gemini-manifest", requestManifest,
                "--rules", RulesPath, "--output", resultPath
            };
            string response = item.Synthetic ? item.SyntheticResponse! : Path.Combine(item.Directory, "application-gemini-response.json");
            if (File.Exists(response)) validatorArguments.AddRange(["--gemini-response", response]);
            ProcessResult validator = await ProcessRunner.RunAsync(ValidatorPath, validatorArguments, null, cancellation);
            item.ValidatorMilliseconds = validator.ElapsedMilliseconds;
            if (validator.ExitCode != 0 || !File.Exists(resultPath))
            {
                item.Status = "malformed_input";
                item.Error = CleanError(validator.StandardError);
                return;
            }
            JsonObject result = JsonNode.Parse(File.ReadAllText(resultPath))!.AsObject();
            item.Status = result["status"]!.GetValue<string>();
            item.RulesVersion = result["rulesVersion"]?.GetValue<string>();
            item.ApplicationVersion = typeof(JobService).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            item.Findings = result["findings"]?.DeepClone();
            item.Error = result["externalServiceError"]?.GetValue<string>() ?? result["malformedInputError"]?.GetValue<string>();
        }
        catch (OperationCanceledException) { item.Status = "cancelled"; throw; }
        catch (Exception exception) { item.Status = "malformed_input"; item.Error = exception.Message; }
        finally { total.Stop(); item.TotalMilliseconds = total.ElapsedMilliseconds; item.Phase = "complete"; }
    }

    void EnsureComponents()
    {
        foreach (string path in new[] { ParserPath, ValidatorPath, RulesPath })
            if (!File.Exists(path)) throw new InvalidOperationException("Required deployed component is missing: " + path);
    }

    static long? ReadLong(string path, string property)
    {
        try { return JsonNode.Parse(File.ReadAllText(path))?[property]?.GetValue<long?>(); }
        catch { return null; }
    }

    static string CleanError(string value)
    {
        string clean = value.Trim();
        return clean.StartsWith("ERROR: ", StringComparison.Ordinal) ? clean[7..] : clean;
    }

    static object Snapshot(JobState job)
    {
        var counts = job.Items.GroupBy(item => item.Status).ToDictionary(group => group.Key, group => group.Count());
        return new
        {
            job.Id, job.Mode, job.CreatedUtc, job.CompletedUtc,
            isComplete = job.CompletedUtc is not null,
            isCancelled = job.Cancellation.IsCancellationRequested,
            total = job.Items.Count,
            completed = job.Items.Count(item => item.Phase == "complete"),
            summary = new
            {
                approved = counts.GetValueOrDefault("approve"),
                rejected = counts.GetValueOrDefault("reject"),
                manualReview = counts.GetValueOrDefault("manual_review"),
                malformedInput = counts.GetValueOrDefault("malformed_input"),
                serviceUnavailable = counts.GetValueOrDefault("external_service_unavailable"),
                cancelled = counts.GetValueOrDefault("cancelled")
            },
            items = job.Items.Select(item => new
            {
                item.Index, item.FileName, item.Phase, item.Status, item.Synthetic,
                item.TotalMilliseconds, item.GeminiMilliseconds, item.GeminiAttempts,
                item.RulesVersion, item.ApplicationVersion, item.Findings, item.Error
            })
        };
    }
}

public sealed class JobState(string id, string mode, List<JobItem> items)
{
    public string Id { get; } = id;
    public string Mode { get; } = mode;
    public List<JobItem> Items { get; } = items;
    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
}

public sealed class JobItem(int index, string fileName, string sourcePath, string directory, bool synthetic, string? syntheticResponse)
{
    public int Index { get; } = index;
    public string FileName { get; } = fileName;
    public string SourcePath { get; } = sourcePath;
    public string Directory { get; } = directory;
    public bool Synthetic { get; } = synthetic;
    public string? SyntheticResponse { get; } = syntheticResponse;
    public string Phase { get; set; } = "queued";
    public string Status { get; set; } = "queued";
    public long? TotalMilliseconds { get; set; }
    public long? GeminiMilliseconds { get; set; }
    public int GeminiAttempts { get; set; }
    public long? ParserMilliseconds { get; set; }
    public long? ValidatorMilliseconds { get; set; }
    public string? RulesVersion { get; set; }
    public string? ApplicationVersion { get; set; }
    public JsonNode? Findings { get; set; }
    public string? Error { get; set; }
}
