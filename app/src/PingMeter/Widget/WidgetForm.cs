using System.Drawing.Text;
using Microsoft.Win32;
using PingMeter.Config;
using PingMeter.Ping;
using PingMeter.Taskbar;

namespace PingMeter.Widget;

/// <summary>
/// The borderless window that lives inside the taskbar: sparkline + color-coded ms readout.
/// One instance per taskbar; all instances render the same shared ping data.
/// </summary>
internal sealed class WidgetForm : Form
{
    // Colorkey for the layered window. Never pure black (0x000000) — TrafficMonitor
    // documents that a black colorkey breaks dark-mode context menus on the Win11 taskbar.
    private static readonly Color KeyColor = Color.FromArgb(1, 0, 1);

    private static readonly Color GoodColor = Color.FromArgb(102, 187, 106);
    private static readonly Color WarnColor = Color.FromArgb(255, 179, 0);
    private static readonly Color BadColor = Color.FromArgb(239, 83, 80);

    private const int SparklinePoints = 24;

    private readonly AppConfig _config;
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 250 };
    private DateTime _hoverSince;
    private bool _tooltipShown;
    private StatsSnapshot? _snapshot;
    private bool _paused;
    private uint _dpi = 96;
    private Font _font;
    private string _tooltipText = "PingMeter";

    /// <summary>Set when size-affecting config changed; the embedder repositions on next tick.</summary>
    public bool LayoutDirty { get; set; }

    public event Action? SettingsRequested;

    public WidgetForm(AppConfig config, ContextMenuStrip menu)
    {
        _config = config;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // DPI handled manually from the taskbar's DPI
        DoubleBuffered = true;
        BackColor = KeyColor;
        ContextMenuStrip = menu;
        _font = CreateFont();
        DoubleClick += (_, _) => SettingsRequested?.Invoke();
        _hoverTimer.Tick += (_, _) => HoverTick();
        _hoverTimer.Start();
    }

    /// <summary>
    /// Colorkey transparency makes Windows route mouse input through the see-through pixels,
    /// so MouseEnter/ToolTip never fire in transparent mode. Poll the cursor instead and show
    /// the tooltip manually after a short dwell — works identically in both display modes.
    /// </summary>
    private void HoverTick()
    {
        if (!IsHandleCreated || !Visible)
            return;
        bool inside = new Rectangle(PointToScreen(Point.Empty), ClientSize).Contains(Cursor.Position);
        if (!inside)
        {
            _hoverSince = default;
            if (_tooltipShown)
            {
                _tooltipShown = false;
                _toolTip.Hide(this);
            }
            return;
        }
        if (_hoverSince == default)
            _hoverSince = DateTime.UtcNow;
        if (!_tooltipShown && (DateTime.UtcNow - _hoverSince).TotalMilliseconds >= 400)
        {
            _tooltipShown = true;
            _toolTip.Show(_tooltipText, this, 0, -Dpi(64));
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // COLORREF is 0x00BBGGRR
        uint key = (uint)(KeyColor.R | (KeyColor.G << 8) | (KeyColor.B << 16));
        NativeMethods.SetLayeredWindowAttributes(Handle, key, 0, NativeMethods.LWA_COLORKEY);
    }

    public void UpdateDpi(uint dpi)
    {
        if (dpi == _dpi)
            return;
        _dpi = dpi;
        _font.Dispose();
        _font = CreateFont();
        LayoutDirty = true;
        Invalidate();
    }

    public void RefreshConfig()
    {
        LayoutDirty = true;
        Invalidate();
    }

    public void UpdateSnapshot(StatsSnapshot snapshot, bool paused, string target)
    {
        _snapshot = snapshot;
        _paused = paused;
        _tooltipText = BuildTooltip(snapshot, paused, target);
        Invalidate();
    }

    public Size ComputeSize(int visibleBarHeight)
    {
        int height = Math.Max(Dpi(16), visibleBarHeight - Dpi(10));
        // Reserve for 3 digits so the width doesn't jitter as the ping moves.
        int textWidth = TextRenderer.MeasureText("888 ms", _font).Width;
        int width = Dpi(4) + textWidth + Dpi(4);
        if (_config.ShowSparkline)
            width += Dpi(36) + Dpi(6);
        return new Size(width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        bool transparent = _config.TransparentBackground;
        Color background = transparent ? KeyColor : ThemeBackground();
        using (var bg = new SolidBrush(background))
            g.FillRectangle(bg, ClientRectangle);

        var snapshot = _snapshot;
        string text;
        Color textColor;
        if (_paused)
        {
            text = "||";
            textColor = MutedText();
        }
        else if (snapshot?.Current is not { } current)
        {
            text = "--";
            textColor = MutedText();
        }
        else if (current.IsLost)
        {
            text = "T/O";
            textColor = BadColor;
        }
        else
        {
            text = $"{current.RoundtripMs} ms";
            textColor = ColorFor(current.RoundtripMs!.Value);
        }

        // Grayscale AA blends toward the colorkey and fringes when transparent; use crisp
        // single-bit rendering there, normal grid-fit AA on a solid background.
        g.TextRenderingHint = transparent
            ? TextRenderingHint.SingleBitPerPixelGridFit
            : TextRenderingHint.AntiAliasGridFit;

        int pad = Dpi(4);
        int textLeft = pad;
        if (_config.ShowSparkline)
        {
            var sparkRect = new Rectangle(pad, Dpi(3), Dpi(36), ClientSize.Height - Dpi(6));
            if (!_paused)
                DrawSparkline(g, sparkRect, snapshot);
            textLeft = sparkRect.Right + Dpi(6);
        }

        using var brush = new SolidBrush(textColor);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, _font, brush,
            new RectangleF(textLeft, 0, ClientSize.Width - textLeft - pad, ClientSize.Height), format);
    }

    private void DrawSparkline(Graphics g, Rectangle rect, StatsSnapshot? snapshot)
    {
        long?[] series = snapshot?.Series ?? [];
        if (series.Length == 0)
            return;

        // Scale to the window max with a 50 ms floor so a quiet LAN doesn't look noisy.
        long scaleMax = Math.Max(50, series.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max());
        float barWidth = (float)rect.Width / SparklinePoints;

        for (int i = 0; i < series.Length && i < SparklinePoints; i++)
        {
            long? value = series[^(i + 1)]; // newest sample at the right edge
            float right = rect.Right - i * barWidth;
            float height = value is null
                ? rect.Height
                : Math.Max(2f, rect.Height * Math.Min(1f, value.Value / (float)scaleMax));
            Color color = value is null ? BadColor : ColorFor(value.Value);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, right - barWidth + 0.5f, rect.Bottom - height, Math.Max(1f, barWidth - 1f), height);
        }
    }

    private Color ColorFor(long ms) =>
        ms < _config.GreenBelowMs ? GoodColor :
        ms < _config.YellowBelowMs ? WarnColor : BadColor;

    private static string BuildTooltip(StatsSnapshot snapshot, bool paused, string target)
    {
        if (paused)
            return $"{target} — paused";
        if (snapshot.SampleCount == 0)
            return $"{target} — waiting for first reply…";
        string current = snapshot.Current is { } c ? (c.IsLost ? "timeout" : $"{c.RoundtripMs} ms") : "--";
        return $"{target}\ncur {current} · min {snapshot.MinMs} · avg {snapshot.AvgMs} · max {snapshot.MaxMs}\nloss {snapshot.LossPercent:0.#}% ({snapshot.SampleCount} samples)";
    }

    private Font CreateFont() => new("Segoe UI", Dpi(13), FontStyle.Regular, GraphicsUnit.Pixel);

    private int Dpi(int px) => (int)Math.Round(px * _dpi / 96.0);

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v ? v != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    private static Color ThemeBackground() =>
        IsLightTheme() ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32);

    private static Color MutedText() =>
        IsLightTheme() ? Color.FromArgb(96, 96, 96) : Color.FromArgb(160, 160, 160);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTimer.Dispose();
            _toolTip.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
