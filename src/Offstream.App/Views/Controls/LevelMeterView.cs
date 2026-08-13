using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Threading;
using Offstream.Core.Audio;

namespace Offstream.App.Views.Controls;

/// <summary>
/// A peak-holding level meter for what capture is hearing right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the app's only proof that it is working.</b> Everything else on the Record page —
/// status, track name, elapsed — comes from Spotify's window title, which keeps changing whether
/// or not a single sample reaches the encoder. A dead meter under a running counter is the one
/// thing that distinguishes "recording" from "recording nothing", and the predecessor had no
/// equivalent: users found out at the end, from a folder of silent files.
/// </para>
/// <para>
/// <b>It replaced a scrolling waveform, which could not work here.</b> Spotify normalises
/// loudness, so its output sits in a band a few decibels wide: every bar of the scroll came out
/// the same height and the control drew a solid block. A waveform needs dynamic range the source
/// does not have. A level bar answers the question actually being asked — is audio arriving, and
/// how loud — without pretending to be a visualisation.
/// </para>
/// <para>
/// <b>It pulls, it is not pushed.</b> Capture delivers a buffer roughly every ten milliseconds.
/// Turning each into a dispatcher callback would put a hundred UI marshals a second behind a
/// decoration; instead <see cref="AudioLevelMeter"/> accumulates the interval's loudness and this
/// control drains it on its own timer.
/// </para>
/// <para>
/// <b>It draws into a child visual, never through <c>OnRender</c>.</b>
/// <see cref="UIElement.InvalidateVisual"/> invalidates arrange as well as rendering, so
/// repainting that way schedules a layout pass on every tick for something whose size never
/// changes.
/// </para>
/// </remarks>
public sealed class LevelMeterView : FrameworkElement
{
    /// <summary>Readings per second. Fast enough to look continuous, slow enough to be free.</summary>
    private const int SamplesPerSecond = 30;

    /// <summary>How long the peak marker sits at a new high before it starts falling.</summary>
    private static readonly TimeSpan PeakHold = TimeSpan.FromSeconds(1.2);

    /// <summary>
    /// How much of the gap to the current reading the bar closes per sample while falling.
    /// </summary>
    /// <remarks>
    /// Rising is instant and falling is gradual, which is how every level meter behaves: a
    /// transient should register at full height, and the eye needs the decay to read it. A bar
    /// that tracked both directions exactly would flicker at the sample rate.
    /// </remarks>
    private const double ReleasePerSample = 0.25;

    /// <summary>How far the peak marker falls per sample once its hold expires.</summary>
    private const double PeakFallPerSample = 0.012;

    /// <summary>Corner rounding on the track and the fill.</summary>
    private const double CornerRadius = 3;

    /// <summary>Width of the peak marker, in device-independent pixels.</summary>
    private const double PeakMarkerWidth = 2.5;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(AudioLevelMeter),
        typeof(LevelMeterView),
        new PropertyMetadata(null, OnLevelChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(LevelMeterView),
        new PropertyMetadata(Brushes.Gray, OnAppearanceChanged));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(LevelMeterView),
        new PropertyMetadata(Brushes.DimGray, OnAppearanceChanged));

    public static readonly DependencyProperty PeakBrushProperty = DependencyProperty.Register(
        nameof(PeakBrush),
        typeof(Brush),
        typeof(LevelMeterView),
        new PropertyMetadata(Brushes.White, OnAppearanceChanged));

    private readonly DispatcherTimer _sampler;
    private readonly DrawingVisual _meterVisual = new();
    private readonly VisualCollection _children;

    private double _displayed;
    private double _peak;
    private DateTime _peakSetAt = DateTime.MinValue;
    private bool _running;

    public LevelMeterView()
    {
        _children = new VisualCollection(this) { _meterVisual };

        _sampler = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1d / SamplesPerSecond),
        };

        _sampler.Tick += OnSample;

        IsVisibleChanged += (_, _) => UpdateSubscription();
        SizeChanged += (_, _) => Redraw();
    }

    protected override int VisualChildrenCount => _children.Count;

    /// <summary>The meter to drain, or null when nothing is recording.</summary>
    public AudioLevelMeter? Level
    {
        get => (AudioLevelMeter?)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>Brush the filled portion is painted with.</summary>
    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Brush for the unfilled track behind the bar.</summary>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Brush for the peak marker.</summary>
    public Brush PeakBrush
    {
        get => (Brush)GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    protected override Visual GetVisualChild(int index) => _children[index];

    /// <summary>
    /// Puts the control in the automation tree, which a bare element is not.
    /// </summary>
    /// <remarks>
    /// <see cref="FrameworkElement"/> creates no peer of its own, so without this the meter is
    /// invisible to assistive technology and to the UI suite alike — and the
    /// <c>AutomationProperties.Name</c> the page sets on it would never reach anything. Reported
    /// as an image rather than a progress bar: a progress bar promises a range pattern and a
    /// value that means completion, and this measures neither.
    /// </remarks>
    protected override AutomationPeer OnCreateAutomationPeer() => new LevelMeterAutomationPeer(this);

    private static void OnAppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((LevelMeterView)sender).Redraw();

    private static void OnLevelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (LevelMeterView)sender;

        // A new meter is a new session. Keeping the old reading would open the next recording
        // showing the last one's loudness.
        view._displayed = 0;
        view._peak = 0;
        view._peakSetAt = DateTime.MinValue;

        view.UpdateSubscription();
        view.Redraw();
    }

    /// <summary>
    /// Runs the sample timer only while there is something to read and somewhere to draw it.
    /// </summary>
    /// <remarks>
    /// Stopped rather than merely ignored when hidden — a page kept alive by
    /// <c>NavigationCacheMode</c> would otherwise go on sampling a stopped session from a tab
    /// nobody is looking at. <see cref="DispatcherPriority.Background"/> so the meter can never
    /// sit ahead of the user's own input in the dispatcher queue.
    /// </remarks>
    private void UpdateSubscription()
    {
        var wanted = Level is not null && IsVisible;

        if (wanted == _running) return;

        if (wanted) _sampler.Start();
        else _sampler.Stop();

        _running = wanted;
    }

    private void OnSample(object? sender, EventArgs e)
    {
        var meter = Level;

        if (meter is null) return;

        var reading = meter.Read();

        // Instant attack, gradual release.
        _displayed = reading > _displayed
            ? reading
            : _displayed + ((reading - _displayed) * ReleasePerSample);

        if (reading >= _peak)
        {
            _peak = reading;
            _peakSetAt = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - _peakSetAt > PeakHold)
        {
            _peak = Math.Max(_displayed, _peak - PeakFallPerSample);
        }

        Redraw();
    }

    private void Redraw()
    {
        using var drawingContext = _meterVisual.RenderOpen();

        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0) return;

        drawingContext.DrawRoundedRectangle(
            TrackBrush, pen: null, new Rect(0, 0, width, height), CornerRadius, CornerRadius);

        var filled = Math.Clamp(_displayed, 0, 1) * width;

        if (filled > 0)
        {
            drawingContext.DrawRoundedRectangle(
                Fill, pen: null, new Rect(0, 0, filled, height), CornerRadius, CornerRadius);
        }

        if (_peak <= 0) return;

        // Kept inside the track at full scale, so the marker never half-disappears off the end.
        var peakLeft = Math.Min(Math.Clamp(_peak, 0, 1) * width, width - PeakMarkerWidth);

        drawingContext.DrawRoundedRectangle(
            PeakBrush, pen: null, new Rect(peakLeft, 0, PeakMarkerWidth, height), 1, 1);
    }

    /// <summary>Reports the meter to automation clients as a named image.</summary>
    private sealed class LevelMeterAutomationPeer(LevelMeterView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(LevelMeterView);

        // Without this the peer is treated as scenery and never surfaces in the control view,
        // which is the tree both a screen reader and the UI suite walk.
        protected override bool IsContentElementCore() => true;
    }
}
