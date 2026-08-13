using FlaUI.Core.AutomationElements;
using Offstream.App.Resources;
using Xunit;

namespace Offstream.UI.Tests.Desktop;

/// <summary>
/// The Record page as it opens, and the two log controls that work without a recording.
/// </summary>
/// <remarks>
/// Nothing here presses Start. Doing so would open a real capture endpoint and go looking for
/// ffmpeg, which makes the result depend on the machine rather than on the code — the transport's
/// own behaviour is covered by <see cref="RecordViewModelTests"/> against a fake session.
/// </remarks>
[Collection(DesktopSuite.Name)]
[Trait("Category", "Desktop")]
public sealed class RecordPageTests : IClassFixture<OffstreamApp>
{
    private readonly OffstreamApp _app;

    public RecordPageTests(OffstreamApp app)
    {
        _app = app;
        _app.Navigate("NavRecord");
    }

    [Fact]
    public void ItOpensIdleWithStartOffered()
    {
        Assert.True(_app.Find("RecordStartButton").IsEnabled);

        // The two transport buttons swap rather than one relabelling itself, so Stop is absent
        // while nothing is running - a control that changes what it does under the pointer is
        // how a recording gets stopped by accident.
        Assert.False(_app.IsPresent("RecordStopButton"));
    }

    [Fact]
    public void TheElapsedCounterStartsAtZero() =>
        Assert.Equal("00H00M00S", _app.Find("RecordElapsed").AsLabel().Text);

    [Fact]
    public void TheTransportIndicatorReadsStopped() =>
        Assert.Equal(Strings.RecordTransportStopped, _app.Find("RecordTransport").AsLabel().Text);

    [Fact]
    public void TheLevelMeterIsOnThePage() =>
        // It draws a flat line until something plays, but a missing meter is a page that looks
        // broken before the user has done anything.
        Assert.True(_app.IsPresent("RecordLevelMeter"));

    [Fact]
    public void TheActivityLogShowsWhatStartupWrote()
    {
        var log = _app.Find("RecordLogList").AsListBox();

        // The page replays the sink, so the log is populated before the page ever existed.
        // An empty box on launch is the failure this catches.
        Assert.NotEmpty(log.Items);
    }

    [Fact]
    public void ClearingTheLogEmptiesIt()
    {
        _app.Find("RecordClearLogButton").Click();

        Assert.Empty(_app.Find("RecordLogList").AsListBox().Items);
    }

    [Fact]
    public void TheLogFilterOffersOneChoicePerLevel()
    {
        var filter = _app.Find("RecordLogFilter").AsComboBox();

        filter.Expand();

        try
        {
            Assert.NotEmpty(filter.Items);
        }
        finally
        {
            // Left open, the dropdown covers whatever the next test is looking for.
            filter.Collapse();
        }
    }

    /// <summary>
    /// The log scrolls inside its box; it does not push the page taller than the window.
    /// </summary>
    /// <remarks>
    /// This is the regression WPF-UI's <c>NavigationViewContentPresenter</c> causes by default:
    /// it wraps a <see cref="System.Windows.Controls.Page"/> whose
    /// <c>ScrollViewer.CanContentScroll</c> is true — which its own static constructor makes the
    /// default — in a <c>DynamicScrollViewer</c>, and inside that the page is measured with
    /// infinite height. The star row holding the log then resolves to its content's size rather
    /// than the viewport, so the list lays out every retained line and runs off the bottom of the
    /// window. Asserted against the window's own rectangle rather than a fixed pixel height, so
    /// this holds however the window is sized.
    /// </remarks>
    [Fact]
    public void TheActivityLogStaysInsideTheWindow()
    {
        var window = _app.Window.BoundingRectangle;
        var log = _app.Find("RecordLogList").BoundingRectangle;

        Assert.True(
            log.Bottom <= window.Bottom,
            $"The log ends at {log.Bottom} but the window at {window.Bottom} — the page is taller than its viewport.");
    }

    [Fact]
    public void CopyingTheLogIsOffered() =>
        // Asserted rather than clicked: copying replaces whatever the developer has on their
        // clipboard, and a test suite that quietly does that is a bad neighbour.
        Assert.True(_app.Find("RecordCopyLogButton").IsEnabled);
}
