using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Threading;
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
/// decoration; instead <see cref="AudioLevelMeter"/> accumulates the interval's loudness and
/// this control drains it on its own clock — one read per bar, covering everything that
/// happened since the last one.
/// </para>
/// <para>
/// <b>Its clock is fixed, not the monitor's.</b> A <see cref="DispatcherTimer"/> at
/// <see cref="SamplesPerSecond"/> keeps the scroll speed the same on a 60&#160;Hz panel and a
/// 144&#160;Hz one. It replaced <see cref="CompositionTarget.Rendering"/>, which fires once per
/// composed frame: that woke the UI thread at the refresh rate only to decide, most times, that
/// no bar was due yet, and kept WPF composing continuously instead of idling.
/// </para>
/// <para>
/// <b>Bars are RMS on a decibel scale, not peak amplitude.</b> A peak taken over a
/// thirtieth of a second is at or near full scale for essentially all mastered music, so the
/// control drew a solid block that conveyed only "audio is arriving". See
/// <see cref="AudioLevelMeter.Read"/>.
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
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.None, OnFillChanged));

    private readonly float[] _bars = new float[Capacity];
    private readonly DispatcherTimer _sampler;
    private readonly DrawingVisual _barsVisual = new();
    private readonly VisualCollection _children;

    private int _head;
    private int _count;
    private bool _subscribed;

    public WaveformView()
    {
        _children = new VisualCollection(this) { _barsVisual };

        _sampler = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1d / SamplesPerSecond),
        };

        _sampler.Tick += OnSample;

        IsVisibleChanged += (_, _) => UpdateSubscription();
        SizeChanged += (_, _) => Redraw();
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

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

    /// <summary>
    /// Repaints the bars into their own visual.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>OnRender</c>, and deliberately so.</b> <see cref="UIElement.InvalidateVisual"/>
    /// invalidates <em>arrange</em> as well as rendering, so driving this from
    /// <c>OnRender</c> scheduled a layout pass thirty times a second — the page's whole visual
    /// tree marked dirty, over and over, for a decoration whose size never changes. Rendering
    /// into a child <see cref="DrawingVisual"/> re-composes that one visual and touches the
    /// layout system not at all.
    /// </remarks>
    private void Redraw()
    {
        using var drawingContext = _barsVisual.RenderOpen();

        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0) return;

        var middle = height / 2;
        var visible = Math.Min(_count, (int)(width / BarPitch));

        // One geometry, one draw call. A DrawRectangle per bar meant several hundred drawing
        // instructions rebuilt thirty times a second, which is what made the page lag while
        // recording; the bars are a single figure set and the compositor treats them as one.
        var geometry = new StreamGeometry();

        using (var figures = geometry.Open())
        {
            // Right-aligned: the newest bar sits against the right edge and older ones march left,
            // so a part-filled control fills from where the eye is already looking.
            for (var index = 0; index < visible; index++)
            {
                var peak = _bars[(_head - 1 - index + Capacity) % Capacity];
                var half = Math.Max(peak * middle, SilenceHalfHeight);
                var left = width - ((index + 1) * BarPitch);

                figures.BeginFigure(new Point(left, middle - half), isFilled: true, isClosed: true);

                figures.PolyLineTo(
                    [
                        new Point(left + BarWidth, middle - half),
                        new Point(left + BarWidth, middle + half),
                        new Point(left, middle + half),
                    ],
                    isStroked: false,
                    isSmoothJoin: false);
            }
        }

        // Frozen so the render thread can take it without a copy, and it is discarded each frame
        // anyway — nothing here ever mutates a geometry after building it.
        geometry.Freeze();

        drawingContext.DrawGeometry(Fill, pen: null, geometry);
    }

    /// <summary>
    /// Repaints on a brush change, which <c>AffectsRender</c> can no longer do for us.
    /// </summary>
    /// <remarks>
    /// That flag invalidates the element's own rendering — the thing this control deliberately
    /// no longer uses. The bars live in a child visual, so the repaint has to be explicit.
    /// </remarks>
    private static void OnFillChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((WaveformView)sender).Redraw();

    private static void OnLevelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (WaveformView)sender;

        // A new meter is a new session. Keeping the old bars would open the next recording with
        // the previous one's tail.
        view._head = 0;
        view._count = 0;

        view.UpdateSubscription();
        view.Redraw();
    }

    /// <summary>
    /// Runs the sample timer only while there is something to draw and somewhere to draw it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The timer is stopped rather than merely ignored when the control is hidden — a page kept
    /// alive by <c>NavigationCacheMode</c> would otherwise go on sampling a stopped session from
    /// a tab nobody is looking at.
    /// </para>
    /// <para>
    /// <b>A timer, not <see cref="CompositionTarget.Rendering"/>.</b> That event fires once per
    /// composed frame, so subscribing to it wakes the UI thread at the display's refresh rate —
    /// 144 times a second on this machine — to decide 114 times out of 144 that it is not yet
    /// time to sample. It also keeps WPF composing continuously instead of idling. Ticking at
    /// exactly <see cref="SamplesPerSecond"/> costs a fifth of the wake-ups and gives the fixed
    /// clock the scroll speed needs directly, rather than by filtering a variable one.
    /// </para>
    /// </remarks>
    private void UpdateSubscription()
    {
        var wanted = Level is not null && IsVisible;

        if (wanted == _subscribed) return;

        if (wanted) _sampler.Start();
        else _sampler.Stop();

        _subscribed = wanted;
    }

    private void OnSample(object? sender, EventArgs e)
    {
        var meter = Level;

        if (meter is null) return;

        _bars[_head] = meter.Read();
        _head = (_head + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);

        Redraw();
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
