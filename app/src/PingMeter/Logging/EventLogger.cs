namespace PingMeter.Logging;

/// <summary>
/// Append-only daily event log at %APPDATA%\PingMeter\logs\events-YYYY-MM-DD.log.
/// All calls happen on the UI thread (samples arrive there), so no locking is needed.
/// </summary>
internal sealed class EventLogger
{
    public static readonly string LogsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingMeter", "logs");

    public static string TodayEventFile => Path.Combine(LogsDir, $"events-{DateTime.Now:yyyy-MM-dd}.log");

    public bool Enabled { get; set; } = true;

    public void Info(string message) => Append("INFO", message);

    public void Warn(string message) => Append("WARN", message);

    private void Append(string level, string message)
    {
        if (!Enabled)
            return;
        try
        {
            Directory.CreateDirectory(LogsDir);
            File.AppendAllText(TodayEventFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the app down
        }
    }

    /// <summary>Delete log/CSV files older than the retention window.</summary>
    public static void SweepOldLogs(int retentionDays)
    {
        try
        {
            if (!Directory.Exists(LogsDir))
                return;
            DateTime cutoff = DateTime.Now.Date.AddDays(-retentionDays);
            foreach (string file in Directory.EnumerateFiles(LogsDir))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
