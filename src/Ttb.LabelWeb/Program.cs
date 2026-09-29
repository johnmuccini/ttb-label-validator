using System.Diagnostics;
using System.Net.Sockets;
using Ttb.LabelWeb;

const string DefaultLocalUrl = "http://127.0.0.1:5080";
string localUrl = ReadOption(args, "--listen-url") ?? DefaultLocalUrl;
if (!Uri.TryCreate(localUrl, UriKind.Absolute, out Uri? listenUri)
    || listenUri.Scheme != Uri.UriSchemeHttp
    || listenUri.Host != "127.0.0.1"
    || listenUri.Port is < 1 or > 65535)
{
    Console.Error.WriteLine("The listen URL must use http://127.0.0.1 and a valid port.");
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot"
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.UseUrls(localUrl);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 9L * 1024 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = 9L * 1024 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddHttpClient("gemini-key-check", client => client.Timeout = TimeSpan.FromSeconds(15));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (ApiKeyStore keys) => Results.Ok(new
{
    applicationVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown",
    rulesVersion = JobService.ReadRulesVersion(),
    apiKeyValidated = keys.HasValidatedKey,
    localUrl,
    syntheticDemoAvailable = JobService.DemoAvailable
}));

app.MapPost("/api/key/validate", async (KeyRequest request, ApiKeyStore keys, IHttpClientFactory clients, CancellationToken cancellation) =>
{
    if (string.IsNullOrWhiteSpace(request.ApiKey)) return Results.BadRequest(new { message = "Enter a Gemini API key." });
    using var message = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models");
    message.Headers.Add("x-goog-api-key", request.ApiKey.Trim());
    try
    {
        using HttpResponseMessage response = await clients.CreateClient("gemini-key-check").SendAsync(message, cancellation);
        if (!response.IsSuccessStatusCode)
            return Results.BadRequest(new { message = $"Gemini rejected the key or connection (HTTP {(int)response.StatusCode})." });
        keys.Set(request.ApiKey.Trim());
        return Results.Ok(new { validated = true });
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { message = "Gemini could not be reached: " + exception.Message }, statusCode: 503);
    }
});

app.MapPost("/api/jobs/live", async (HttpRequest request, JobService jobs, ApiKeyStore keys, CancellationToken cancellation) =>
{
    if (!keys.HasValidatedKey) return Results.BadRequest(new { message = "Validate a Gemini API key before processing application files." });
    if (!request.HasFormContentType) return Results.BadRequest(new { message = "Choose one or more PDF files." });
    IFormCollection form = await request.ReadFormAsync(cancellation);
    try { return Results.Accepted(value: await jobs.CreateLiveJobAsync(form.Files, cancellation)); }
    catch (ArgumentException exception) { return Results.BadRequest(new { message = exception.Message }); }
}).DisableAntiforgery();

app.MapPost("/api/jobs/demo", (JobService jobs) =>
{
    try { return Results.Accepted(value: jobs.CreateSyntheticDemoJob()); }
    catch (Exception exception) when (exception is IOException or InvalidOperationException)
    { return Results.Problem(exception.Message, statusCode: 500); }
});

app.MapGet("/api/jobs/{id}", (string id, JobService jobs) =>
    jobs.TrySnapshot(id, out object? snapshot) ? Results.Ok(snapshot) : Results.NotFound());

app.MapDelete("/api/jobs/{id}", (string id, JobService jobs) =>
    jobs.Cancel(id) ? Results.Accepted() : Results.NotFound());

app.MapGet("/api/jobs/{id}/results", (string id, JobService jobs) =>
    jobs.TryExport(id, out byte[]? content)
        ? Results.File(content!, "application/json", $"ttb-validation-{id}.json")
        : Results.NotFound());

if (args.Contains("--open-browser", StringComparer.OrdinalIgnoreCase))
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true }); }
        catch { }
    });

try
{
    await app.RunAsync();
}
catch (Exception exception) when (AddressAlreadyInUse(exception))
{
    Console.Error.WriteLine($"TTB Label Validator could not start because {localUrl} is already in use.");
    Console.Error.WriteLine($"If the application is already running, open {localUrl} in your browser.");
    Environment.ExitCode = 2;
}

static string? ReadOption(string[] arguments, string name)
{
    int index = Array.FindIndex(arguments, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static bool AddressAlreadyInUse(Exception exception)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
        if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse
            || current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            || current.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            return true;
    return false;
}

namespace Ttb.LabelWeb
{
    public sealed record KeyRequest(string ApiKey);
}
