using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Offstream.Core.Audio;

namespace Offstream.App.Views.Controls;

/// <summary>
/// A scrolling bar rendering of what capture is hearing right now.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the app's only proof that it is working.</b> Everything else on the Record page —
/// status, track name, elapsed — comes from Spotify's window title, which keeps changing whether
/// or not a single sample reaches the encoder. A silent waveform under a scrolling counter is the
/// one thing that distinguishes "recording" from "recording nothing", and the predecessor had no
/// equivalent: users found out at the end, from a folder of silent files.
/// </para>
/// <para>
/// <b>It pulls, it is not pushed.</b> Capture delivers a buffer roughly every ten milliseconds.
/// Turning each into a dispatcher callback would put a hundred UI marshals a second behind a
/// decoration; instead <see cref="AudioLevelMeter"/> accumulates the interval's peak and this
/// control drains it on its own clock — one read per frame, and the peak survives whatever
/// happened between frames.
/// </para>
/// <para>
/// <b>Its clock is fixed, not the monitor's.</b> <see cref="CompositionTarget.Rendering"/> fires
/// at the display's refresh rate, so sampling every tick would make the waveform scroll half as
/// fast on 60&#160;Hz as on 120&#160;Hz. Bars are taken at <see cref="SamplesPerSecond"/> and the
/// frame event is only the invitation to check.
/// </para>
/// </remarks>
public sealed class WaveformView : FrameworkElement
{
    /// <summary>Bars per second. Sets the scroll speed, together with <see cref="BarPitch"/>.</summary>
    private const int SamplesPerSecond = 30;

    /// <summary>Drawn bar width, in device-independent pixels.</summary>
    private const double BarWidth = 3;

    /// <summary>Bar width plus the gap after it.</summary>
    private const double BarPitch = 4;

    /// <summary>
    /// Bars retained. Comfortably more than a wide window shows, so a resize reveals history
    /// rather than empty space.
    /// </summary>
    private const int Capacity = 1024;

    /// <summary>
    /// Half-height of the line drawn for silence, so the control reads as present-and-quiet
    /// rather than broken.
    /// </summary>
    private const double SilenceHalfHeight = 0.5;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(AudioLevelMeter),
        typeof(WaveformView),
        new PropertyMetadata(null, OnLevelChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(WaveformView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly float[] _bars = new float[Capacity];

    private int _head;
    private int _count;
    private TimeSpan _lastSample = TimeSpan.MinValue;
    private bool _subscribed;

    public WaveformView() => IsVisibleChanged += (_, _) => UpdateSubscription();

    /// <summary>The meter to drain, or null when nothing is recording.</summary>
    public AudioLevelMeter? Level
    {
        get => (AudioLevelMeter?)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>Brush the bars are painted with.</summary>
    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>
    /// Puts the control in the automation tree, which a bare element is not.
    /// </summary>
    /// <remarks>
    /// <see cref="FrameworkElement"/> creates no peer of its own, so without this the meter is
    /// invisible to assistive technology and to the UI suite alike — and the
    /// <c>AutomationProperties.Name</c> the page sets on it would never reach anything. Reported
    /// as an image rather than a progress bar: it is a picture of the signal, and claiming a
    /// progress bar would promise a range pattern and a value that this has no meaning for.
    /// </remarks>
    protected override AutomationPeer OnCreateAutomationPeer() => new WaveformViewAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0) return;

        var middle = height / 2;
        var visible = Math.Min(_count, (int)(width / BarPitch));

        // Right-aligned: the newest bar sits against the right edge and older ones march left, so
        // a part-filled control fills from where the eye is already looking.
        for (var index = 0; index < visible; index++)
        {
            var peak = _bars[(_head - 1 - index + Capacity) % Capacity];
            var half = Math.Max(peak * middle, SilenceHalfHeight);
            var left = width - ((index + 1) * BarPitch);

            drawingContext.DrawRectangle(Fill, pen: null, new Rect(left, middle - half, BarWidth, half * 2));
        }
    }

    private static void OnLevelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaveformView)sender;

        // A new meter is a new session. Keeping the old bars would open the next recording with
        // the previous one's tail.
        view._head = 0;
        view._count = 0;
        view._lastSample = TimeSpan.MinValue;

        view.UpdateSubscription();
        view.InvalidateVisual();
    }

    /// <summary>
    /// Runs the frame hook only while there is something to draw and somewhere to draw it.
    /// </summary>
    /// <remarks>
    /// <see cref="CompositionTarget.Rendering"/> is a static event, so a subscription outlives the
    /// control that made it — a page kept alive by <c>NavigationCacheMode</c> would otherwise go
    /// on sampling a stopped session from a tab nobody is looking at.
    /// </remarks>
    private void UpdateSubscription()
    {
        var wanted = Level is not null && IsVisible;

        if (wanted == _subscribed) return;

        if (wanted) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;

        _subscribed = wanted;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var meter = Level;

        if (meter is null) return;

        // RenderingEventArgs carries the frame's own timestamp. Wall-clock time would drift
        // against the compositor and show up as a stutter in the scroll.
        if (e is not RenderingEventArgs args) return;

        var interval = TimeSpan.FromSeconds(1d / SamplesPerSecond);

        if (_lastSample != TimeSpan.MinValue && args.RenderingTime - _lastSample < interval) return;

        _lastSample = args.RenderingTime;

        _bars[_head] = meter.Read();
        _head = (_head + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);

        InvalidateVisual();
    }

    /// <summary>Reports the meter to automation clients as a named image.</summary>
    private sealed class WaveformViewAutomationPeer(WaveformView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(WaveformView);

        // Without this the peer is treated as scenery and never surfaces in the control view,
        // which is the tree both a screen reader and the UI suite walk.
        protected override bool IsContentElementCore() => true;
    }
}
