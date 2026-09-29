using System.Diagnostics;

namespace Ttb.LabelWeb;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, long ElapsedMilliseconds);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string? apiKey, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (apiKey is not null) start.Environment["GEMINI_API_KEY"] = apiKey;
        using var process = new Process { StartInfo = start };
        var timer = Stopwatch.StartNew();
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        timer.Stop();
        return new ProcessResult(process.ExitCode, await stdout, await stderr, timer.ElapsedMilliseconds);
    }
}
