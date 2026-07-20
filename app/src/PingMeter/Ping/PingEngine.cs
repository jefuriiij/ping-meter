using System.Diagnostics;
using System.Net.NetworkInformation;

namespace PingMeter.Ping;

/// <summary>
/// Continuous ICMP ping loop (the app's equivalent of `ping host -t`).
/// Uses System.Net.NetworkInformation.Ping — no shelling out, no admin rights.
/// </summary>
public sealed class PingEngine : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SynchronizationContext? _syncContext;
    private volatile string _target;
    private int _generation;

    public PingStats Stats { get; }
    public string Target => _target;
    public int IntervalMs { get; set; }
    public int TimeoutMs { get; set; }
    public bool IsPaused { get; private set; }

    /// <summary>Raised on the UI thread after each sample lands in <see cref="Stats"/>.</summary>
    public event Action? SampleReceived;

    /// <summary>
    /// Raised on the UI thread only for real ping results (never for the refresh-only
    /// signals SetTarget/SetPaused emit) — this is the event logging must hang off.
    /// </summary>
    public event Action<PingSample>? SampleAdded;

    public PingEngine(string target, int intervalMs, int timeoutMs, int statsWindow)
    {
        _target = target;
        IntervalMs = intervalMs;
        TimeoutMs = timeoutMs;
        Stats = new PingStats(statsWindow);
    }

    public void Start()
    {
        if (_loop != null)
            return;
        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void SetTarget(string host)
    {
        // Bump the generation so an in-flight reply for the old target is discarded.
        Interlocked.Increment(ref _generation);
        _target = host;
        Stats.Clear();
        RaiseSample();
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        RaiseSample();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var stopwatch = new Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            stopwatch.Restart();
            if (!IsPaused)
            {
                string target = _target;
                int generation = Volatile.Read(ref _generation);
                int timeout = TimeoutMs;
                PingSample sample;
                try
                {
                    // Fresh instance per iteration: if the WaitAsync guard below abandons a hung
                    // send (e.g. slow DNS), disposing the instance kills it instead of poisoning
                    // the next SendPingAsync with "a call is already in progress".
                    using var ping = new System.Net.NetworkInformation.Ping();
                    PingReply reply = await ping.SendPingAsync(target, timeout)
                        .WaitAsync(TimeSpan.FromMilliseconds(timeout + 3000), ct)
                        .ConfigureAwait(false);
                    sample = reply.Status == IPStatus.Success
                        ? new PingSample(reply.RoundtripTime)
                        : new PingSample(null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // DNS failure, no network, hung send — all count as a lost sample.
                    sample = new PingSample(null);
                }

                if (Volatile.Read(ref _generation) == generation && !ct.IsCancellationRequested)
                {
                    Stats.Add(sample);
                    RaiseSample(sample);
                }
            }

            // Keep the cadence at IntervalMs including the time the ping itself took.
            int delay = Math.Max(50, IntervalMs - (int)stopwatch.ElapsedMilliseconds);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RaiseSample(PingSample? added = null)
    {
        var ctx = _syncContext;
        if (ctx != null)
            ctx.Post(_ => Raise(added), null);
        else
            Raise(added);

        void Raise(PingSample? sample)
        {
            if (sample is { } s)
                SampleAdded?.Invoke(s);
            SampleReceived?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(1000);
        }
        catch
        {
            // cancellation surfaces as AggregateException; nothing to do
        }
        _cts?.Dispose();
    }
}
