using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

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

/// <summary>Live progress of a full reset: how many steps finished, what's running now.</summary>
internal sealed record RepairProgress(int Completed, int Total, string? CurrentStep, RepairStepResult? LastResult);

/// <summary>
/// What one DNS server should be set to. <paramref name="Doh"/> null means "leave the
/// existing encryption setting alone" — the classic dialog has no DoH UI and must not
/// silently clear what the user configured elsewhere.
/// </summary>
internal sealed record DnsServerRequest(string Address, DohMode? Doh, string? Template);

/// <summary>
/// The whole DNS change, handed to the elevated helper as base64 JSON — a single argument,
/// so no quoting or ordering games on the command line.
/// </summary>
/// <param name="Ipv6InterfaceIndex">The adapter's IPv6 index; null when IPv6 is off for it, and then IPv6 DNS is left alone.</param>
/// <param name="SetIpv6">With static IPv4 servers: true sets the IPv6 servers below, false hands IPv6 DNS back to automatic.</param>
internal sealed record DnsRequest(
    int InterfaceIndex,
    string AdapterId,
    bool Automatic,
    DnsServerRequest? Primary,
    DnsServerRequest? Secondary,
    int? Ipv6InterfaceIndex = null,
    bool SetIpv6 = false,
    DnsServerRequest? Ipv6Primary = null,
    DnsServerRequest? Ipv6Secondary = null);

/// <summary>
/// One-click internet repair (the classic flushdns/release/renew/winsock/tcpip sequence).
/// The main app runs unelevated by design (taskbar embedding requires it), while most of
/// these commands need admin — so the full reset relaunches this same exe elevated in a
/// headless helper mode that runs the commands, writes a JSON result, and exits.
/// </summary>
internal static class NetworkRepair
{
    public const string HelperArgument = "--network-repair";
    public const string SetDnsArgument = "--set-dns";
    public const string FlushDnsArgument = "--flush-dns";

    private static readonly string ResultFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingMeter", "repair-result.json");

    // One JSON line per completed step, appended by the elevated helper and polled by the
    // main app to drive the progress bar and activity log while the helper runs.
    private static readonly string ProgressFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingMeter", "repair-progress.jsonl");

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

    /// <summary>
    /// Flush DNS only. Tries unelevated first; newer Windows builds refuse that ("The requested
    /// operation requires elevation", exit 1), so on failure it retries through the elevated helper.
    /// The dnsapi DnsFlushResolverCache call and Clear-DnsClientCache are no way round this: on
    /// those builds they report success unelevated but leave the cache untouched.
    /// </summary>
    public static async Task<RepairResult> RunQuickFixAsync()
    {
        int exitCode = await Task.Run(() => RunCommand("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(30)));
        if (exitCode == 0)
            return new RepairResult(RepairOutcome.Success, [new RepairStepResult("Clear DNS cache", 0, true, false)], null);

        return await RunHelperAsync(FlushDnsArgument, "The DNS helper");
    }

    /// <summary>Elevated child entry for "--flush-dns".</summary>
    public static void RunFlushDnsHelper() =>
        WriteResults([RunStep("Clear DNS cache", "ipconfig.exe", "/flushdns")]);

    /// <summary>Relaunch this exe elevated (UAC prompt) to run all five steps; await its JSON result.</summary>
    public static async Task<RepairResult> RunFullResetAsync(IProgress<RepairProgress>? progress = null)
    {
        try
        {
            File.Delete(ResultFile);
            File.Delete(ProgressFile);
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
            var stopwatch = Stopwatch.StartNew();
            int reported = 0;
            while (!helper.HasExited)
            {
                if (stopwatch.Elapsed > TimeSpan.FromMinutes(2))
                    return new RepairResult(RepairOutcome.Failed, [], "The repair helper did not finish within 2 minutes.");
                await Task.Delay(300);
                reported = ReportNewProgress(progress, reported);
            }
            ReportNewProgress(progress, reported); // catch the final step(s)
        }

        return ReadResultFile();
    }

    /// <summary>
    /// Change the active adapter's IPv4 DNS (and its encryption settings) via the elevated
    /// helper. <see cref="DnsRequest.Automatic"/> means hand control back to DHCP.
    /// </summary>
    public static async Task<RepairResult> RunSetDnsAsync(DnsRequest request)
    {
        try
        {
            File.Delete(ResultFile);
        }
        catch
        {
        }

        string payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
        return await RunHelperAsync($"{SetDnsArgument} {payload}", "The DNS helper");
    }

    /// <summary>Relaunch this exe elevated with <paramref name="arguments"/>; await its JSON result.</summary>
    private static async Task<RepairResult> RunHelperAsync(string arguments, string helperName)
    {
        try
        {
            File.Delete(ResultFile);
        }
        catch
        {
        }

        Process helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            })!;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // UAC declined
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
                return new RepairResult(RepairOutcome.Failed, [], $"{helperName} did not finish within 2 minutes.");
        }
        return ReadResultFile();
    }

    /// <summary>
    /// Elevated child entry for "--set-dns &lt;base64 json&gt;". Everything is re-validated
    /// here — this runs with admin rights, so nothing from the payload is trusted blindly.
    /// </summary>
    public static void RunSetDnsHelper(string[] args)
    {
        var results = new List<RepairStepResult>();
        DnsRequest? request = null;
        try
        {
            if (args.Length >= 2)
                request = JsonSerializer.Deserialize<DnsRequest>(Convert.FromBase64String(args[1]));
        }
        catch
        {
            request = null;
        }

        if (request is null || request.InterfaceIndex <= 0 || !IsAdapterGuid(request.AdapterId))
        {
            WriteResults([new RepairStepResult("Invalid arguments", -4, false, false)]);
            return;
        }

        int? v6Index = request.Ipv6InterfaceIndex is > 0 ? request.Ipv6InterfaceIndex : null;
        if (request.Automatic)
        {
            SetFamilyAutomatic(results, request.AdapterId, request.InterfaceIndex, ipv6: false);
            if (v6Index is { } i6)
                SetFamilyAutomatic(results, request.AdapterId, i6, ipv6: true);
        }
        else
        {
            var servers = ValidServers(request.Primary, request.Secondary, ipv6: false);
            var v6Servers = request.SetIpv6 ? ValidServers(request.Ipv6Primary, request.Ipv6Secondary, ipv6: true) : [];
            if (servers.Count == 0 || (request.SetIpv6 && v6Index != null && v6Servers.Count == 0))
            {
                WriteResults([new RepairStepResult("Invalid arguments", -4, false, false)]);
                return;
            }

            SetFamilyStatic(results, request.AdapterId, request.InterfaceIndex, servers, ipv6: false);
            if (v6Index is { } i6)
            {
                if (request.SetIpv6)
                    SetFamilyStatic(results, request.AdapterId, i6, v6Servers, ipv6: true);
                else
                    SetFamilyAutomatic(results, request.AdapterId, i6, ipv6: true);
            }
        }

        results.Add(RunStep("Clear DNS cache", "ipconfig.exe", "/flushdns"));
        WriteResults(results);
    }

    /// <summary>Main then backup, keeping only real addresses of the family, in Windows' own text form.</summary>
    private static List<DnsServerRequest> ValidServers(DnsServerRequest? primary, DnsServerRequest? secondary, bool ipv6) =>
        new[] { primary, secondary }
            .Where(s => s is not null && (ipv6 ? IsIPv6(s.Address) : IsIPv4(s.Address)))
            .Select(s => s! with { Address = IPAddress.Parse(s!.Address).ToString() })
            .ToList();

    /// <summary>Back to DHCP for one address family, dropping any encryption settings we may have written before.</summary>
    private static void SetFamilyAutomatic(List<RepairStepResult> results, string adapterId, int index, bool ipv6)
    {
        foreach (string server in ReadConfiguredDohServers(adapterId, ipv6))
            results.Add(SetDohRegistry(adapterId, server, DohMode.Off, null));
        results.Add(ipv6
            ? RunStep("Switch IPv6 DNS to automatic", "netsh.exe", $"interface ipv6 set dnsservers name={index} source=dhcp")
            : RunStep("Switch DNS to automatic", "netsh.exe", $"interface ipv4 set dnsservers name={index} dhcp"));
    }

    private static void SetFamilyStatic(List<RepairStepResult> results, string adapterId, int index, List<DnsServerRequest> servers, bool ipv6)
    {
        // Encryption first, then the servers. Applying the addresses last makes the
        // DNS client pick the new DoH settings up straight away.
        foreach (var server in servers)
        {
            if (server.Doh is not { } doh)
                continue; // caller didn't ask about encryption, so leave it untouched
            if (doh == DohMode.Manual && IsDohTemplate(server.Template))
            {
                results.Add(RunStep($"Register DoH template for {server.Address}", "netsh.exe",
                    $"dns add encryption server={server.Address} dohtemplate={server.Template} autoupgrade=yes udpfallback=no"));
            }
            results.Add(SetDohRegistry(adapterId, server.Address, doh, server.Template));
        }

        string family = ipv6 ? "ipv6" : "ipv4";
        string label = ipv6 ? "IPv6 DNS" : "DNS";
        results.Add(RunStep($"Set {label} to {servers[0].Address}", "netsh.exe",
            $"interface {family} set dnsservers name={index} static {servers[0].Address} primary validate=no"));
        if (servers.Count > 1)
        {
            results.Add(RunStep($"Add backup {label} {servers[1].Address}", "netsh.exe",
                $"interface {family} add dnsservers name={index} {servers[1].Address} index=2 validate=no"));
        }
    }

    /// <summary>Write (or clear) one server's DoH setting. Needs the elevation we already have.</summary>
    private static RepairStepResult SetDohRegistry(string adapterId, string server, DohMode mode, string? template)
    {
        string label = mode switch
        {
            DohMode.Automatic => $"Encrypt {server} (automatic template)",
            DohMode.Manual => $"Encrypt {server} (custom template)",
            _ => $"Turn off encryption for {server}",
        };
        try
        {
            string path = DnsInfo.DohKeyPath(adapterId, server);
            if (mode == DohMode.Off)
            {
                Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                return new RepairStepResult(label, 0, true, false);
            }

            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key is null)
                return new RepairStepResult(label, -5, false, false);
            // Windows' own encoding: 1 = built-in template, 2 = the custom one below.
            key.SetValue("DohFlags", mode == DohMode.Manual ? 2L : 1L, RegistryValueKind.QWord);
            if (mode == DohMode.Manual && IsDohTemplate(template))
                key.SetValue("DohTemplate", template!, RegistryValueKind.String);
            else
                key.DeleteValue("DohTemplate", throwOnMissingValue: false);
            return new RepairStepResult(label, 0, true, false);
        }
        catch
        {
            return new RepairStepResult(label, -5, false, false);
        }
    }

    /// <summary>Servers of one address family that currently have an encryption setting on this adapter.</summary>
    private static List<string> ReadConfiguredDohServers(string adapterId, bool ipv6)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DnsInfo.DohFamilyKeyPath(adapterId, ipv6));
            return key?.GetSubKeyNames().ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Adapter ids come from the OS and land in a registry path — allow only GUID text.</summary>
    private static bool IsAdapterGuid(string? value) =>
        value != null && Guid.TryParse(value.Trim('{', '}'), out _);

    /// <summary>Templates land on a command line and in the registry — require a plain https URL.</summary>
    private static bool IsDohTemplate(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        value.IndexOfAny([' ', '"', '&', '|', '<', '>', '^']) < 0;

    private static bool IsIPv4(string value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>No zone ids ("%12"): they mean nothing for a DNS server and only widen the command line.</summary>
    private static bool IsIPv6(string value) =>
        !value.Contains('%') && IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

    private static RepairStepResult RunStep(string name, string fileName, string arguments)
    {
        int exitCode = RunCommand(fileName, arguments, TimeSpan.FromSeconds(60));
        return new RepairStepResult(name, exitCode, exitCode == 0, RequiresRestart: false);
    }

    private static RepairResult ReadResultFile()
    {
        try
        {
            var steps = JsonSerializer.Deserialize<List<RepairStepResult>>(File.ReadAllText(ResultFile)) ?? [];
            RepairOutcome outcome =
                steps.Count == 0 ? RepairOutcome.Failed :
                steps.All(s => s.Ok) ? RepairOutcome.Success :
                RepairOutcome.PartialFailure;
            return new RepairResult(outcome, steps, steps.Count == 0 ? "The helper produced no results." : null);
        }
        catch (Exception ex)
        {
            return new RepairResult(RepairOutcome.Failed, [], $"Couldn't read the result: {ex.Message}");
        }
    }

    /// <summary>
    /// Entry point for the elevated child process (see Program.cs). No UI, no mutex —
    /// just run the steps, write the JSON result, exit.
    /// </summary>
    public static void RunElevatedHelper()
    {
        try
        {
            File.Delete(ProgressFile);
        }
        catch
        {
        }

        var results = new List<RepairStepResult>();
        foreach (var step in FullResetSteps)
        {
            int exitCode = RunCommand(step.FileName, step.Arguments, TimeSpan.FromSeconds(60));
            var result = new RepairStepResult(step.Name, exitCode, exitCode == 0, step.RequiresRestart);
            results.Add(result);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProgressFile)!);
                File.AppendAllText(ProgressFile, JsonSerializer.Serialize(result) + Environment.NewLine);
            }
            catch
            {
                // progress is advisory; the final result file is what matters
            }
        }

        WriteResults(results);
    }

    private static void WriteResults(List<RepairStepResult> results)
    {
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

    /// <summary>Report any steps that completed since the last poll; returns the new reported count.</summary>
    private static int ReportNewProgress(IProgress<RepairProgress>? progress, int alreadyReported)
    {
        if (progress is null)
            return alreadyReported;
        List<RepairStepResult> steps = ReadProgressSteps();
        for (int i = alreadyReported; i < steps.Count; i++)
        {
            string? currentStep = i + 1 < FullResetSteps.Length ? FullResetSteps[i + 1].Name : null;
            progress.Report(new RepairProgress(i + 1, FullResetSteps.Length, currentStep, steps[i]));
        }
        return Math.Max(alreadyReported, steps.Count);
    }

    private static List<RepairStepResult> ReadProgressSteps()
    {
        var list = new List<RepairStepResult>();
        try
        {
            if (!File.Exists(ProgressFile))
                return list;
            foreach (string line in File.ReadAllLines(ProgressFile))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<RepairStepResult>(line) is { } step)
                        list.Add(step);
                }
                catch
                {
                    // a partially written last line — picked up on the next poll
                }
            }
        }
        catch
        {
            // brief write/read collision — next poll will succeed
        }
        return list;
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
