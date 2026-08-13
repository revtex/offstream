using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Offstream.Core.Audio;

namespace Offstream.App.Views.Controls;

/// <summary>
/// A stereo level meter drawn as the segment display of a field recorder.
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
/// <b>Why it looks like hardware.</b> A recorder's meter is a solved instrument — segments, a
/// printed decibel ruler, a peak that holds — and borrowing that vocabulary means the control
/// needs no explaining to anyone who has held one. The ruler is the part a generic progress bar
/// cannot offer: it turns "the bar is about two-thirds along" into "the signal is near −12 dB",
/// which is the difference between decoration and an instrument. Every position on it is read
/// from <see cref="LevelReading.Decibels"/> against <see cref="ScaleFloorDecibels"/>, so the bars
/// and the numbers printed under them are the same measurement rather than two scales that
/// happen to sit next to each other.
/// </para>
/// <para>
/// <b>It replaced a scrolling waveform, which could not work here.</b> Spotify normalises
/// loudness, so its output sits in a band a few decibels wide: every bar of the scroll came out
/// the same height and the control drew a solid block. A waveform needs dynamic range the source
/// does not have.
/// </para>
/// <para>
/// <b>It pulls, it is not pushed.</b> Capture delivers a buffer roughly every ten milliseconds.
/// Turning each into a dispatcher callback would put a hundred UI marshals a second behind a
/// decoration; instead <see cref="AudioLevelMeter"/> accumulates the interval's loudness and this
/// control drains it on its own timer.
/// </para>
/// <para>
/// <b>It draws into child visuals, never through <c>OnRender</c>.</b>
/// <see cref="UIElement.InvalidateVisual"/> invalidates arrange as well as rendering, so
/// repainting that way schedules a layout pass on every tick for something whose size never
/// changes. The text is a second visual behind the same rule: ruler labels are expensive to
/// build and almost never change, so they are only rebuilt when the readout does.
/// </para>
/// </remarks>
public sealed class LcdMeterView : FrameworkElement
{
    /// <summary>Readings per second. Fast enough to look continuous, slow enough to be free.</summary>
    private const int SamplesPerSecond = 30;

    /// <summary>How long the peak marker sits at a new high before it starts falling.</summary>
    private static readonly TimeSpan PeakHold = TimeSpan.FromSeconds(1.2);

    /// <summary>
    /// How much of the gap to the current reading a bar closes per sample while falling.
    /// </summary>
    /// <remarks>
    /// Rising is instant and falling is gradual, which is how every level meter behaves: a
    /// transient should register at full height, and the eye needs the decay to read it. A bar
    /// that tracked both directions exactly would flicker at the sample rate.
    /// </remarks>
    private const double ReleasePerSample = 0.25;

    /// <summary>How far the peak marker falls per sample once its hold expires.</summary>
    private const double PeakFallPerSample = 0.012;

    /// <summary>
    /// Quietest point on the scale, in dBFS — the left edge of the bars and of the ruler.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="AudioLevelMeter"/>'s own 60&#160;dB normalisation, which is why
    /// this control works from <see cref="LevelReading.Decibels"/> rather than
    /// <see cref="LevelReading.Level"/>. Spotify normalises loudness to about −14&#160;dBFS, so a
    /// 50&#160;dB scale puts ordinary playback in the upper third with the quiet passages still
    /// on the dial, and every printed tick lands where the number says it does.
    /// </remarks>
    private const double ScaleFloorDecibels = -50;

    /// <summary>The ticks printed under the bars, in dBFS.</summary>
    /// <remarks>
    /// Crowded toward the top on purpose, and taken from the instrument this borrows from: the
    /// decibels worth reading precisely are the last twelve before clipping. Below that, one
    /// number every 20&#160;dB is enough to say where you are.
    /// </remarks>
    private static readonly double[] RulerDecibels = [-50, -30, -20, -12, -6, 0];

    private const double PanelPadding = 10;
    private const double PanelCorner = 3;
    private const double BezelThickness = 1;

    private const double ChannelLabelWidth = 10;
    private const double ChannelLabelGap = 7;
    private const double ChannelLabelSize = 10;

    private const double ReadoutWidth = 58;
    private const double ReadoutGap = 10;
    private const double ReadoutSize = 16;
    private const double ReadoutUnitSize = 9;

    /// <summary>Space kept for the <c>dB</c> unit when the numeric readout is hidden.</summary>
    /// <remarks>
    /// The unit stays either way: it is what makes the row of numbers under the bars a decibel
    /// ruler rather than an unlabelled scale. Reserving only its width is what lets the bars run
    /// nearly the full panel when the readout is off.
    /// </remarks>
    private const double UnitOnlyWidth = 26;

    private const double BarHeight = 13;
    private const double BarGap = 4;

    private const double RulerGap = 7;
    private const double TickWidth = 1;
    private const double TickHeight = 4;
    private const double TickLabelGap = 2;
    private const double TickLabelSize = 9;

    /// <summary>Cells the grid aims for, and the pitch it will accept to get there.</summary>
    /// <remarks>
    /// <para>
    /// Derived from the width rather than fixed, because a fixed pitch is what separates a
    /// segment display from cross-hatching: at 5&#160;px a wide panel came out with over a hundred
    /// hairlines, which the eye reads as texture on a solid bar rather than as discrete steps.
    /// Forty cells is the count a hardware meter uses, and holding it means the display looks the
    /// same whatever the window is doing. The pitch is floored to whole units so every cell is
    /// the same width — an uneven grid is the one thing that gives away that this is drawn.
    /// </para>
    /// <para>
    /// The grid itself is a tiled mask painted over continuous bars rather than one rectangle per
    /// cell: a single draw call at any width, and the cell boundaries land in the same places on
    /// both rows regardless of what either is showing.
    /// </para>
    /// </remarks>
    private const int TargetCells = 40;
    private const double MinimumCellPitch = 4;
    private const double MaximumCellPitch = 14;
    private const double CellGapRatio = 0.25;

    /// <summary>Bars drawn, and the height always reserved for them.</summary>
    /// <remarks>
    /// A mono source draws one bar, centred, rather than the same reading twice — copying it
    /// across would show stereo the capture does not have. The space stays reserved either way so
    /// the card does not resize when a session starts.
    /// </remarks>
    private const int MaximumRows = 2;

    /// <summary>Width used when a parent offers infinite space; it is otherwise fluid.</summary>
    private const double FallbackWidth = 360;

    private static readonly double NaturalHeight =
        (PanelPadding * 2) + (BarHeight * MaximumRows) + BarGap +
        RulerGap + TickHeight + TickLabelGap + (TickLabelSize * 1.4);

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(AudioLevelMeter),
        typeof(LcdMeterView),
        new PropertyMetadata(null, OnLevelChanged));

    public static readonly DependencyProperty PanelBrushProperty = DependencyProperty.Register(
        nameof(PanelBrush),
        typeof(Brush),
        typeof(LcdMeterView),
        new PropertyMetadata(Brushes.DarkSeaGreen, OnAppearanceChanged));

    /// <summary>
    /// What the cell grid is painted in — whatever sits behind the bars.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PanelBrush"/> because the two answer different questions once the
    /// panel stops being a flat fill. Set into a bezel that already paints the glass, this control
    /// draws no background of its own (<see cref="PanelBrush"/> goes transparent) — but the grid
    /// still has to be opaque and still has to match what shows through, or the gaps between cells
    /// read as lit segments. A gradient behind the bars is near-flat across their thirty pixels,
    /// so one solid matching it there is enough.
    /// </remarks>
    public static readonly DependencyProperty MaskBrushProperty = DependencyProperty.Register(
        nameof(MaskBrush),
        typeof(Brush),
        typeof(LcdMeterView),
        new PropertyMetadata(Brushes.DarkSeaGreen, OnMaskChanged));

    /// <summary>
    /// Whether the held peak is printed in dBFS beside the bars.
    /// </summary>
    /// <remarks>
    /// Off gives the bars nearly the whole panel, which is what a display reads like when the
    /// technical line above it already carries the numbers. The <c>dB</c> unit under the ruler
    /// stays either way — without it the row of numbers is an unlabelled scale.
    /// </remarks>
    public static readonly DependencyProperty ShowReadoutProperty = DependencyProperty.Register(
        nameof(ShowReadout),
        typeof(bool),
        typeof(LcdMeterView),
        new PropertyMetadata(true, OnAppearanceChanged));

    public static readonly DependencyProperty GhostBrushProperty = DependencyProperty.Register(
        nameof(GhostBrush),
        typeof(Brush),
        typeof(LcdMeterView),
        new PropertyMetadata(Brushes.Gray, OnAppearanceChanged));

    public static readonly DependencyProperty SegmentBrushProperty = DependencyProperty.Register(
        nameof(SegmentBrush),
        typeof(Brush),
        typeof(LcdMeterView),
        new PropertyMetadata(Brushes.Black, OnAppearanceChanged));

    /// <summary>
    /// Colours the bars across the decibel scale — cool at the floor, warm at clipping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gradient is keyed to the scale, not to the bar.</b> A cell's colour says where on the
    /// ruler it sits, so a signal at −20&#160;dB is the same green whether it is peaking there or
    /// passing through. Painting the lit rectangle with an ordinary brush would stretch the whole
    /// spectrum into whatever the bar currently measures, which would put clipping red on a bar
    /// that is nowhere near clipping. <see cref="RemapToScale"/> pins the gradient to the grid's
    /// own pixels to prevent exactly that.
    /// </para>
    /// <para>
    /// The palette is pigment rather than light — the muted inks of a colour e-paper panel, not the
    /// saturated LEDs of a rack meter. It has to sit on a pale grey ground and stay readable, and
    /// the display is meant to look like something printed and held rather than something lit.
    /// </para>
    /// <para>
    /// Null falls back to <see cref="SegmentBrush"/>, which is the monochrome display this started
    /// as.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty SpectrumBrushProperty = DependencyProperty.Register(
        nameof(SpectrumBrush),
        typeof(Brush),
        typeof(LcdMeterView),
        new PropertyMetadata(null, OnSpectrumChanged));

    public static readonly DependencyProperty InkBrushProperty = DependencyProperty.Register(
        nameof(InkBrush),
        typeof(Brush),
        typeof(LcdMeterView),
        new PropertyMetadata(Brushes.DimGray, OnAppearanceChanged));

    public static readonly DependencyProperty BezelBrushProperty = DependencyProperty.Register(
        nameof(BezelBrush),
        typeof(Brush),
        typeof(LcdMeterView),
        new PropertyMetadata(Brushes.Black, OnAppearanceChanged));

    /// <summary>
    /// The face the ruler and readout are set in, inherited from the page like any other text.
    /// </summary>
    /// <remarks>
    /// <see cref="FrameworkElement"/> has no font properties of its own, so this borrows
    /// <see cref="TextElement"/>'s — which keeps the inheritance a caller would expect and lets
    /// the page set it in the ordinary way rather than through a property invented here.
    /// </remarks>
    public static readonly DependencyProperty FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner(
            typeof(LcdMeterView),
            new FrameworkPropertyMetadata(
                SystemFonts.MessageFontFamily,
                FrameworkPropertyMetadataOptions.Inherits,
                OnAppearanceChanged));

    private readonly DispatcherTimer _sampler;
    private readonly DrawingVisual _barsVisual = new();
    private readonly DrawingVisual _textVisual = new();
    private readonly VisualCollection _children;

    /// <summary>Per-channel bar and peak positions, as 0–1 along the decibel scale.</summary>
    private readonly double[] _displayed = new double[MaximumRows];
    private readonly double[] _peaks = new double[MaximumRows];
    private readonly DateTime[] _peakSetAt = [DateTime.MinValue, DateTime.MinValue];

    private DrawingBrush? _cellMask;
    private double _maskPitch;
    private bool _running;

    /// <summary>The spectrum pinned to the current grid, and the grid it was pinned to.</summary>
    private Brush? _scaledSpectrum;
    private double _spectrumLeft;
    private double _spectrumWidth;

    /// <summary>What the text layer was last drawn for, so it can be skipped when nothing moved.</summary>
    private string _drawnReadout = string.Empty;
    private double _drawnWidth;
    private int _drawnRows;
    private bool _textDirty = true;

    public LcdMeterView()
    {
        _children = new VisualCollection(this) { _barsVisual, _textVisual };

        // Hard cell edges are the whole look; a dot-matrix grid that resolves to grey mush at
        // fractional scaling reads as a gradient, not as segments. Only the bars layer is
        // aliased — text keeps WPF's normal rendering.
        RenderOptions.SetEdgeMode(_barsVisual, EdgeMode.Aliased);

        UseLayoutRounding = true;

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

    /// <summary>The lit background of the display; transparent when a bezel already paints it.</summary>
    public Brush PanelBrush
    {
        get => (Brush)GetValue(PanelBrushProperty);
        set => SetValue(PanelBrushProperty, value);
    }

    /// <inheritdoc cref="MaskBrushProperty"/>
    public Brush MaskBrush
    {
        get => (Brush)GetValue(MaskBrushProperty);
        set => SetValue(MaskBrushProperty, value);
    }

    /// <inheritdoc cref="ShowReadoutProperty"/>
    public bool ShowReadout
    {
        get => (bool)GetValue(ShowReadoutProperty);
        set => SetValue(ShowReadoutProperty, value);
    }

    /// <summary>Unlit cells — present, but barely.</summary>
    public Brush GhostBrush
    {
        get => (Brush)GetValue(GhostBrushProperty);
        set => SetValue(GhostBrushProperty, value);
    }

    /// <summary>Lit cells and the peak marker, when no <see cref="SpectrumBrush"/> is set.</summary>
    public Brush SegmentBrush
    {
        get => (Brush)GetValue(SegmentBrushProperty);
        set => SetValue(SegmentBrushProperty, value);
    }

    /// <inheritdoc cref="SpectrumBrushProperty"/>
    public Brush? SpectrumBrush
    {
        get => (Brush?)GetValue(SpectrumBrushProperty);
        set => SetValue(SpectrumBrushProperty, value);
    }

    /// <summary>The ruler, its numbers, and the channel letters.</summary>
    public Brush InkBrush
    {
        get => (Brush)GetValue(InkBrushProperty);
        set => SetValue(InkBrushProperty, value);
    }

    /// <summary>The frame the display is set into.</summary>
    public Brush BezelBrush
    {
        get => (Brush)GetValue(BezelBrushProperty);
        set => SetValue(BezelBrushProperty, value);
    }

    /// <inheritdoc cref="TextElement.FontFamily"/>
    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    protected override Visual GetVisualChild(int index) => _children[index];

    /// <summary>
    /// Fixed height, fluid width: the display is an instrument face, not content.
    /// </summary>
    /// <remarks>
    /// Its height is the sum of parts that each have to be a specific size to stay legible, so
    /// letting the layout stretch it would only stretch the empty space between them. Width is
    /// taken because a longer scale is a more readable scale.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? FallbackWidth : availableSize.Width, NaturalHeight);

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
    protected override AutomationPeer OnCreateAutomationPeer() => new LcdMeterAutomationPeer(this);

    private static void OnAppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (LcdMeterView)sender;

        view._textDirty = true;
        view.Redraw();
    }

    private static void OnMaskChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (LcdMeterView)sender;

        // The grid is painted in this brush, so it stops matching the moment the brush changes.
        view._cellMask = null;
        view._maskPitch = 0;
        view._textDirty = true;
        view.Redraw();
    }

    private static void OnSpectrumChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (LcdMeterView)sender;

        view._scaledSpectrum = null;
        view._spectrumWidth = 0;
        view.Redraw();
    }

    private static void OnLevelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (LcdMeterView)sender;

        // A new meter is a new session. Keeping the old readings would open the next recording
        // showing the last one's loudness.
        Array.Clear(view._displayed);
        Array.Clear(view._peaks);
        Array.Fill(view._peakSetAt, DateTime.MinValue);
        view._textDirty = true;

        view.UpdateSubscription();
        view.Redraw();
    }

    /// <summary>
    /// Runs the sample timer only while there is something to read and somewhere to draw it.
    /// </summary>
    /// <remarks>
    /// Stopped rather than merely ignored when hidden. The shell keeps every page loaded and
    /// switches them by visibility, so without this the meter would go on sampling from a tab
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

        Span<LevelReading> channels = stackalloc LevelReading[MaximumRows];
        meter.Read(channels);

        var rows = RowsFor(meter);
        var now = DateTime.UtcNow;

        for (var row = 0; row < rows; row++)
        {
            var position = PositionOf(channels[row].Decibels);

            _displayed[row] = position > _displayed[row]
                ? position
                : _displayed[row] + ((position - _displayed[row]) * ReleasePerSample);

            if (position >= _peaks[row])
            {
                _peaks[row] = position;
                _peakSetAt[row] = now;
            }
            else if (now - _peakSetAt[row] > PeakHold)
            {
                _peaks[row] = Math.Max(_displayed[row], _peaks[row] - PeakFallPerSample);
            }
        }

        Redraw();
    }

    /// <summary>Where a decibel figure sits on the scale, as 0–1.</summary>
    private static double PositionOf(double decibels) =>
        double.IsNegativeInfinity(decibels) || double.IsNaN(decibels)
            ? 0
            : Math.Clamp((decibels - ScaleFloorDecibels) / -ScaleFloorDecibels, 0, 1);

    /// <summary>And back again, so the readout can never disagree with the marker.</summary>
    private static double DecibelsAt(double position) =>
        ScaleFloorDecibels + (position * -ScaleFloorDecibels);

    private static int RowsFor(AudioLevelMeter? meter) =>
        Math.Clamp(meter?.ChannelCount ?? MaximumRows, 1, MaximumRows);

    private void Redraw()
    {
        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0) return;

        var meter = Level;
        var rows = RowsFor(meter);

        var barLeft = PanelPadding + ChannelLabelWidth + ChannelLabelGap;
        var barRight = width - PanelPadding - (ShowReadout ? ReadoutWidth + ReadoutGap : UnitOnlyWidth);
        var barWidth = barRight - barLeft;

        if (barWidth < MinimumCellPitch) return;

        var pitch = Math.Clamp(Math.Floor(barWidth / TargetCells), MinimumCellPitch, MaximumCellPitch);
        var cells = (int)(barWidth / pitch);
        var gridWidth = cells * pitch;

        // Both rows are drawn in the space reserved for MaximumRows, centred, so a mono capture
        // does not sit against the top of a panel sized for two.
        var stackHeight = (BarHeight * rows) + (BarGap * (rows - 1));
        var firstRowTop = PanelPadding + (((BarHeight * MaximumRows) + BarGap - stackHeight) / 2);
        var rulerTop = PanelPadding + (BarHeight * MaximumRows) + BarGap + RulerGap;

        DrawBars(width, height, rows, barLeft, firstRowTop, pitch, cells);

        // The text layer is the expensive one - a dozen FormattedText objects laid out from
        // scratch - and almost none of it changes between frames. Skipping it while the readout,
        // the width and the channel count all hold leaves a sampled frame costing a handful of
        // rectangles, which is what lets this run at 30 Hz behind everything else on the page.
        var readout = ReadoutText(meter);

        if (!_textDirty && readout == _drawnReadout && width == _drawnWidth && rows == _drawnRows) return;

        DrawText(readout, rows, barLeft, firstRowTop, rulerTop, gridWidth, width);

        _drawnReadout = readout;
        _drawnWidth = width;
        _drawnRows = rows;
        _textDirty = false;
    }

    private void DrawBars(
        double width, double height, int rows, double barLeft, double firstRowTop, double pitch, int cells)
    {
        var gridWidth = cells * pitch;
        var gap = Math.Max(1, Math.Round(pitch * CellGapRatio));
        var lit = LitBrush(barLeft, gridWidth);

        using var context = _barsVisual.RenderOpen();

        context.DrawRoundedRectangle(
            PanelBrush,
            new Pen(BezelBrush, BezelThickness),
            new Rect(
                BezelThickness / 2,
                BezelThickness / 2,
                Math.Max(width - BezelThickness, 0),
                Math.Max(height - BezelThickness, 0)),
            PanelCorner,
            PanelCorner);

        for (var row = 0; row < rows; row++)
        {
            var top = firstRowTop + (row * (BarHeight + BarGap));

            context.DrawRectangle(GhostBrush, pen: null, new Rect(barLeft, top, gridWidth, BarHeight));

            // Whole cells only. A segment meter that lights half a cell is a bar with stripes on
            // it; the quantisation is what makes the eye read discrete steps.
            var cellsLit = _displayed[row] > 0 ? Math.Max(1, (int)(_displayed[row] * cells)) : 0;

            if (cellsLit > 0)
            {
                context.DrawRectangle(
                    lit, pen: null, new Rect(barLeft, top, cellsLit * pitch, BarHeight));
            }

            if (_peaks[row] > 0)
            {
                var cell = Math.Clamp((int)(_peaks[row] * cells), 0, cells - 1);

                // Painted from the same scale-pinned brush, so a held peak carries the colour of
                // the decibel it is holding at rather than a colour of its own.
                context.DrawRectangle(
                    lit,
                    pen: null,
                    new Rect(barLeft + (cell * pitch), top, pitch - gap, BarHeight));
            }
        }

        // The grid last, in the panel colour: one tiled draw that cuts every row into cells at
        // the same boundaries, whatever each row happens to be showing.
        if (_cellMask is null || _maskPitch != pitch)
        {
            _cellMask = BuildCellMask(pitch, gap);
            _maskPitch = pitch;
        }

        context.DrawRectangle(
            _cellMask,
            pen: null,
            new Rect(barLeft, firstRowTop, gridWidth, (BarHeight * rows) + (BarGap * (rows - 1))));
    }

    private void DrawText(
        string readoutText,
        int rows,
        double barLeft,
        double firstRowTop,
        double rulerTop,
        double gridWidth,
        double width)
    {
        using var context = _textVisual.RenderOpen();

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        for (var row = 0; row < rows; row++)
        {
            var label = rows == 1 ? "M" : row == 0 ? "L" : "R";
            var text = Format(label, ChannelLabelSize, InkBrush, typeface);

            context.DrawText(
                text,
                new Point(
                    PanelPadding + ((ChannelLabelWidth - text.Width) / 2),
                    firstRowTop + (row * (BarHeight + BarGap)) + ((BarHeight - text.Height) / 2)));
        }

        foreach (var decibels in RulerDecibels)
        {
            var x = barLeft + (PositionOf(decibels) * gridWidth);

            context.DrawRectangle(
                InkBrush, pen: null, new Rect(x - (TickWidth / 2), rulerTop, TickWidth, TickHeight));

            var text = Format(
                decibels.ToString("0", CultureInfo.CurrentCulture), TickLabelSize, InkBrush, typeface);

            // Clamped into the bar's own span so the outermost numbers stay inside the panel
            // rather than hanging over the channel letters or the readout.
            var labelLeft = Math.Clamp(x - (text.Width / 2), barLeft, barLeft + gridWidth - text.Width);

            context.DrawText(text, new Point(labelLeft, rulerTop + TickHeight + TickLabelGap));
        }

        var unit = Format("dB", ReadoutUnitSize, InkBrush, typeface);
        var right = width - PanelPadding;

        context.DrawText(unit, new Point(right - unit.Width, rulerTop + TickHeight + TickLabelGap));

        if (!ShowReadout) return;

        var readout = Format(readoutText, ReadoutSize, SegmentBrush, typeface);

        context.DrawText(
            readout,
            new Point(right - readout.Width, firstRowTop + ((BarHeight * 2) - readout.Height) / 2));
    }

    /// <summary>
    /// The held peak, in dBFS — the number a recordist actually reads off a meter.
    /// </summary>
    /// <remarks>
    /// Taken from the loudest channel's marker rather than tracked separately, so the figure and
    /// the marker can never drift apart. An unreadable capture format says so outright instead of
    /// printing a number the meter did not measure.
    /// </remarks>
    private string ReadoutText(AudioLevelMeter? meter)
    {
        if (meter is null) return "--.-";
        if (!meter.IsSupported) return "--.-";

        var peak = Math.Max(_peaks[0], _peaks[1]);

        return peak <= 0
            ? "-∞"
            : DecibelsAt(peak).ToString("0.0", CultureInfo.CurrentCulture);
    }

    private FormattedText Format(string text, double size, Brush brush, Typeface typeface) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>
    /// What lit cells are painted in: the spectrum pinned to the grid, or the plain segment brush.
    /// </summary>
    /// <remarks>
    /// Cached against the grid it was built for. The gradient is rebuilt on a resize and on
    /// nothing else — sampling at 30&#160;Hz must not allocate a brush per frame.
    /// </remarks>
    private Brush LitBrush(double barLeft, double gridWidth)
    {
        if (SpectrumBrush is not { } spectrum) return SegmentBrush;

        if (_scaledSpectrum is not null && _spectrumLeft == barLeft && _spectrumWidth == gridWidth)
        {
            return _scaledSpectrum;
        }

        _scaledSpectrum = RemapToScale(spectrum, barLeft, gridWidth);
        _spectrumLeft = barLeft;
        _spectrumWidth = gridWidth;

        return _scaledSpectrum;
    }

    /// <summary>
    /// Pins a gradient to the grid's pixels, so its stops mean decibels rather than proportions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A brush with the default relative mapping is stretched across the bounding box of whatever
    /// it fills, which for a level meter is the reading itself — so the bar would run the full
    /// spectrum at every height and clipping red would appear on a signal at −40&#160;dB. Absolute
    /// mapping across the whole grid makes a cell's colour a function of its position on the ruler,
    /// which is the only reading that means anything.
    /// </para>
    /// <para>
    /// Anything that is not a <see cref="LinearGradientBrush"/> is used as it comes: a solid colour
    /// has no mapping to fix, and a caller who supplies something more exotic has said what they
    /// want.
    /// </para>
    /// </remarks>
    private static Brush RemapToScale(Brush brush, double left, double width)
    {
        if (brush is not LinearGradientBrush gradient) return brush;

        var pinned = gradient.Clone();

        pinned.MappingMode = BrushMappingMode.Absolute;
        pinned.StartPoint = new Point(left, 0);
        pinned.EndPoint = new Point(left + width, 0);
        pinned.Freeze();

        return pinned;
    }

    /// <summary>One vertical stripe of panel colour, tiled at the cell pitch.</summary>
    private DrawingBrush BuildCellMask(double pitch, double gap)
    {
        var stripe = new GeometryDrawing(
            MaskBrush, pen: null, new RectangleGeometry(new Rect(pitch - gap, 0, gap, 1)));

        return new DrawingBrush(stripe)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, pitch, 1),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, pitch, 1),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
    }

    /// <summary>Reports the meter to automation clients as a named image.</summary>
    private sealed class LcdMeterAutomationPeer(LcdMeterView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(LcdMeterView);

        // Without this the peer is treated as scenery and never surfaces in the control view,
        // which is the tree both a screen reader and the UI suite walk.
        protected override bool IsContentElementCore() => true;
    }
}
