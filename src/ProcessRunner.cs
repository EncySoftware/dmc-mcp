using System.Diagnostics;
using System.Text;

namespace DmcMcp;

public record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    /** stdout, or stderr when stdout is empty — for error surfaces. */
    public string Output => string.IsNullOrWhiteSpace(StdOut) ? StdErr : StdOut;
}

public interface IProcessRunner
{
    Task<ProcResult> Run(string fileName, string arguments, string? workingDir = null,
        IDictionary<string, string>? env = null, int timeoutSeconds = 120);
}

public class ProcessRunner : IProcessRunner
{
    /** Windows shims for npm-installed CLIs — a bare name never resolves to these. */
    private static readonly string[] WindowsShimExtensions = { ".cmd", ".bat", ".exe" };

    public async Task<ProcResult> Run(string fileName, string arguments, string? workingDir = null,
        IDictionary<string, string>? env = null, int timeoutSeconds = 120)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        var process = TryStart(psi);

        // `claude` and friends are installed by npm as claude.cmd; launching the bare name fails even
        // though the CLI is on PATH. Seen live 2026-07-25: the Claude Code probe in `setup` reported
        // "not installed" on a machine that had it.
        if (process == null && OperatingSystem.IsWindows() && !Path.HasExtension(fileName))
            foreach (var ext in WindowsShimExtensions)
            {
                psi.FileName = fileName + ext;
                process = TryStart(psi);
                if (process != null) break;
            }

        // Callers probe for optional tools, so an absent command is an ordinary failed result;
        // letting the exception out took the whole server down.
        if (process == null)
            return new ProcResult(127, "", $"{fileName} is not installed or not on PATH");

        using var p = process;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcResult(-1, await stdout, $"timed out after {timeoutSeconds}s: {fileName} {arguments}");
        }
        return new ProcResult(p.ExitCode, await stdout, await stderr);
    }

    private static Process? TryStart(ProcessStartInfo psi)
    {
        try { return Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
