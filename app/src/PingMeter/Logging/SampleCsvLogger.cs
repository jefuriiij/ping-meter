using PingMeter.Ping;

namespace PingMeter.Logging;

/// <summary>
/// Optional raw per-ping CSV (samples-YYYY-MM-DD.csv) for graphing/analysis.
/// A timeout is written with an empty ms field.
/// </summary>
internal sealed class SampleCsvLogger
{
    public bool Enabled { get; set; }

    private static string TodayFile => Path.Combine(EventLogger.LogsDir, $"samples-{DateTime.Now:yyyy-MM-dd}.csv");

    public void Log(string target, PingSample sample)
    {
        if (!Enabled)
            return;
        try
        {
            Directory.CreateDirectory(EventLogger.LogsDir);
            string file = TodayFile;
            if (!File.Exists(file))
                File.AppendAllText(file, "timestamp,target,ms" + Environment.NewLine);
            File.AppendAllText(file,
                $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss},{target},{sample.RoundtripMs}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
