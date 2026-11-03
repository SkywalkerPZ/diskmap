using System.Diagnostics;

namespace DiskMap.Core.Removal;

public sealed record ProcessOutput(int ExitCode, string StdOut);

public interface IProcessRunner
{
    Task<ProcessOutput> RunAsync(string exe, string args, CancellationToken ct);
}

/// <summary>No window, redirected stdout; the stdout encoding is left at the default.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessOutput> RunAsync(string exe, string args, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("Could not start " + exe);
        try
        {
            var text = p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new ProcessOutput(p.ExitCode, await text);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
