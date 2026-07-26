using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PingMeter.Network;

internal sealed record RepairStepResult(string Step, int ExitCode, bool Ok, bool RequiresRestart);

internal enum RepairOutcome
{
    Success,
    PartialFailure,
    Cancelled,
    Failed,
}

internal sealed record RepairResult(RepairOutcome Outcome, IReadOnlyList<RepairStepResult> Steps, string? Error)
{
    /// <summary>True when a step that only takes effect after a reboot (the netsh resets) succeeded.</summary>
    public bool RestartNeeded => Steps.Any(s => s.Ok && s.RequiresRestart);
}

/// <summary>
/// One-click internet repair (the classic flushdns/release/renew/winsock/tcpip sequence).
/// The main app runs unelevated by design (taskbar embedding requires it), while most of
/// these commands need admin — so the full reset relaunches this same exe elevated in a
/// headless helper mode that runs the commands, writes a JSON result, and exits.
/// </summary>
internal static class NetworkRepair
{
    public const string HelperArgument = "--network-repair";

    private static readonly string ResultFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingMeter", "repair-result.json");

    private sealed record Step(string Name, string FileName, string Arguments, bool RequiresRestart);

    // Mirrors Reset-Internet-Connection.md: run these in order, then reboot.
    private static readonly Step[] FullResetSteps =
    [
        new("Clear DNS cache", "ipconfig.exe", "/flushdns", false),
        new("Release IP address", "ipconfig.exe", "/release", false),
        new("Request new IP address", "ipconfig.exe", "/renew", false),
        new("Reset Winsock", "netsh.exe", "winsock reset", true),
        new("Reset TCP/IP stack", "netsh.exe", "int ip reset", true),
    ];

    /// <summary>Flush DNS only — safe, instant, works without elevation.</summary>
    public static async Task<RepairResult> RunQuickFixAsync()
    {
        int exitCode = await Task.Run(() => RunCommand("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(30)));
        var step = new RepairStepResult("Clear DNS cache", exitCode, exitCode == 0, RequiresRestart: false);
        return exitCode == 0
            ? new RepairResult(RepairOutcome.Success, [step], null)
            : new RepairResult(RepairOutcome.Failed, [step], $"ipconfig /flushdns exited with code {exitCode}");
    }

    /// <summary>Relaunch this exe elevated (UAC prompt) to run all five steps; await its JSON result.</summary>
    public static async Task<RepairResult> RunFullResetAsync()
    {
        try
        {
            File.Delete(ResultFile);
        }
        catch
        {
            // stale result cleanup is best-effort
        }

        Process helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, HelperArgument)
            {
                UseShellExecute = true,
                Verb = "runas",
            })!;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return new RepairResult(RepairOutcome.Cancelled, [], "Windows permission was not granted.");
        }
        catch (Exception ex)
        {
            return new RepairResult(RepairOutcome.Failed, [], ex.Message);
        }

        using (helper)
        {
            Task done = helper.WaitForExitAsync();
            if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromMinutes(2))) != done)
                return new RepairResult(RepairOutcome.Failed, [], "The repair helper did not finish within 2 minutes.");
        }

        try
        {
            var steps = JsonSerializer.Deserialize<List<RepairStepResult>>(File.ReadAllText(ResultFile)) ?? [];
            RepairOutcome outcome =
                steps.Count == 0 ? RepairOutcome.Failed :
                steps.All(s => s.Ok) ? RepairOutcome.Success :
                RepairOutcome.PartialFailure;
            return new RepairResult(outcome, steps, steps.Count == 0 ? "The repair helper produced no results." : null);
        }
        catch (Exception ex)
        {
            return new RepairResult(RepairOutcome.Failed, [], $"Couldn't read the repair result: {ex.Message}");
        }
    }

    /// <summary>
    /// Entry point for the elevated child process (see Program.cs). No UI, no mutex —
    /// just run the steps, write the JSON result, exit.
    /// </summary>
    public static void RunElevatedHelper()
    {
        var results = new List<RepairStepResult>();
        foreach (var step in FullResetSteps)
        {
            int exitCode = RunCommand(step.FileName, step.Arguments, TimeSpan.FromSeconds(60));
            results.Add(new RepairStepResult(step.Name, exitCode, exitCode == 0, step.RequiresRestart));
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultFile)!);
            File.WriteAllText(ResultFile, JsonSerializer.Serialize(results));
        }
        catch
        {
            // the parent treats a missing result file as failure
        }
    }

    private static int RunCommand(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return -1;
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                return -2; // timed out
            }
            return process.ExitCode;
        }
        catch
        {
            return -3; // failed to start
        }
    }
}
