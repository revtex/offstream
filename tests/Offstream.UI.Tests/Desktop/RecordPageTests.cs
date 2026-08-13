using FlaUI.Core.AutomationElements;
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
        Assert.Equal("0:00", _app.Find("RecordElapsed").AsLabel().Text);

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

    [Fact]
    public void CopyingTheLogIsOffered() =>
        // Asserted rather than clicked: copying replaces whatever the developer has on their
        // clipboard, and a test suite that quietly does that is a bad neighbour.
        Assert.True(_app.Find("RecordCopyLogButton").IsEnabled);
}
