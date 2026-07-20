using PingMeter.Config;
using PingMeter.Ping;

namespace PingMeter.Logging;

/// <summary>
/// Watches the sample stream and logs state transitions (Ok / Degraded / Down) plus hourly
/// summaries — so the log answers "was my network unstable?" without per-sample spam.
/// Degraded uses 3-sample hysteresis around YellowBelowMs to avoid flapping near the threshold.
/// </summary>
internal sealed class StabilityTracker
{
    private enum State
    {
        Ok,
        Degraded,
        Down,
    }

    private const int Hysteresis = 3;

    private readonly EventLogger _log;
    private readonly AppConfig _config;

    private State _state = State.Ok;
    private int _consecutiveLost;
    private DateTime _downSince;
    private int _consecutiveHigh;
    private int _consecutiveNormal;
    private readonly List<long> _recentHigh = [];

    // Hourly accumulator
    private int _hour = DateTime.Now.Hour;
    private int _count;
    private int _lost;
    private long _sum;
    private long _min = long.MaxValue;
    private long _max;

    public StabilityTracker(EventLogger log, AppConfig config)
    {
        _log = log;
        _config = config;
    }

    /// <summary>Call when the target changes — old state is meaningless for a new host.</summary>
    public void Reset()
    {
        _state = State.Ok;
        _consecutiveLost = 0;
        _consecutiveHigh = 0;
        _consecutiveNormal = 0;
        _recentHigh.Clear();
    }

    public void Process(string target, PingSample sample)
    {
        EmitHourlySummaryIfDue(target);
        Accumulate(sample);

        if (sample.IsLost)
        {
            _consecutiveLost++;
            if (_state != State.Down)
            {
                _log.Warn($"{target}: ping timeout");
                _downSince = DateTime.Now;
                _state = State.Down;
            }
            _consecutiveHigh = 0;
            _consecutiveNormal = 0;
            return;
        }

        long ms = sample.RoundtripMs!.Value;

        if (_state == State.Down)
        {
            if (_consecutiveLost >= 2)
            {
                TimeSpan outage = DateTime.Now - _downSince;
                _log.Info($"{target}: recovered after {_consecutiveLost} lost pings (~{outage.TotalSeconds:0}s), now {ms} ms");
            }
            else
            {
                _log.Info($"{target}: recovered, {ms} ms");
            }
            _consecutiveLost = 0;
            _consecutiveHigh = 0;
            _consecutiveNormal = 0;
            _recentHigh.Clear();
            _state = State.Ok;
            return;
        }
        _consecutiveLost = 0;

        if (ms >= _config.YellowBelowMs)
        {
            _consecutiveNormal = 0;
            _recentHigh.Add(ms);
            if (_recentHigh.Count > Hysteresis)
                _recentHigh.RemoveAt(0);
            if (_state == State.Ok && ++_consecutiveHigh >= Hysteresis)
            {
                _log.Warn($"{target}: latency degraded ({string.Join("/", _recentHigh)} ms, threshold {_config.YellowBelowMs} ms)");
                _state = State.Degraded;
            }
        }
        else
        {
            _consecutiveHigh = 0;
            _recentHigh.Clear();
            if (_state == State.Degraded && ++_consecutiveNormal >= Hysteresis)
            {
                _log.Info($"{target}: latency back to normal ({ms} ms)");
                _consecutiveNormal = 0;
                _state = State.Ok;
            }
        }
    }

    private void Accumulate(PingSample sample)
    {
        _count++;
        if (sample.IsLost)
        {
            _lost++;
        }
        else
        {
            long ms = sample.RoundtripMs!.Value;
            _sum += ms;
            _min = Math.Min(_min, ms);
            _max = Math.Max(_max, ms);
        }
    }

    private void EmitHourlySummaryIfDue(string target)
    {
        int hour = DateTime.Now.Hour;
        if (hour == _hour)
            return;
        if (_count > 0)
        {
            int ok = _count - _lost;
            string stats = ok > 0 ? $"min {_min} / avg {_sum / ok} / max {_max} ms" : "no successful pings";
            _log.Info($"{target}: hourly summary — {stats}, loss {100.0 * _lost / _count:0.#}% ({_count} samples)");
        }
        _hour = hour;
        _count = 0;
        _lost = 0;
        _sum = 0;
        _min = long.MaxValue;
        _max = 0;
    }
}
