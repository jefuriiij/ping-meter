namespace PingMeter.Ping;

/// <summary>One ping result; a null roundtrip means timeout / unreachable / DNS failure.</summary>
public readonly record struct PingSample(long? RoundtripMs)
{
    public bool IsLost => RoundtripMs is null;
}

public sealed record StatsSnapshot(
    PingSample? Current,
    long MinMs,
    long AvgMs,
    long MaxMs,
    double LossPercent,
    int SampleCount,
    long?[] Series);

/// <summary>Thread-safe ring buffer of the most recent ping samples.</summary>
public sealed class PingStats
{
    private readonly object _gate = new();
    private PingSample[] _buffer;
    private int _next;
    private int _count;

    public PingStats(int capacity) => _buffer = new PingSample[Math.Max(1, capacity)];

    public void Add(PingSample sample)
    {
        lock (_gate)
        {
            _buffer[_next] = sample;
            _next = (_next + 1) % _buffer.Length;
            _count = Math.Min(_count + 1, _buffer.Length);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
        }
    }

    public void Resize(int capacity)
    {
        capacity = Math.Max(1, capacity);
        lock (_gate)
        {
            var recent = SamplesInOrderLocked();
            _buffer = new PingSample[capacity];
            _next = 0;
            _count = 0;
            foreach (var sample in recent.Skip(Math.Max(0, recent.Length - capacity)))
            {
                _buffer[_next] = sample;
                _next = (_next + 1) % capacity;
                _count = Math.Min(_count + 1, capacity);
            }
        }
    }

    /// <param name="seriesLength">How many of the newest samples to include for the sparkline.</param>
    public StatsSnapshot GetSnapshot(int seriesLength)
    {
        PingSample[] samples;
        lock (_gate)
        {
            samples = SamplesInOrderLocked();
        }

        PingSample? current = samples.Length > 0 ? samples[^1] : null;
        var ok = samples.Where(s => !s.IsLost).Select(s => s.RoundtripMs!.Value).ToArray();
        long min = ok.Length > 0 ? ok.Min() : 0;
        long max = ok.Length > 0 ? ok.Max() : 0;
        long avg = ok.Length > 0 ? (long)Math.Round(ok.Average()) : 0;
        double loss = samples.Length > 0
            ? 100.0 * samples.Count(s => s.IsLost) / samples.Length
            : 0;
        long?[] series = samples
            .Skip(Math.Max(0, samples.Length - seriesLength))
            .Select(s => s.RoundtripMs)
            .ToArray();

        return new StatsSnapshot(current, min, avg, max, loss, samples.Length, series);
    }

    private PingSample[] SamplesInOrderLocked()
    {
        var result = new PingSample[_count];
        int start = (_next - _count + _buffer.Length) % _buffer.Length;
        for (int i = 0; i < _count; i++)
            result[i] = _buffer[(start + i) % _buffer.Length];
        return result;
    }
}
