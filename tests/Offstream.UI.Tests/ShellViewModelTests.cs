using System.ComponentModel;
using System.Globalization;
using System.Text;
using Offstream.App.Resources;
using Offstream.App.Services;
using Offstream.App.ViewModels;
using Offstream.Core.Diagnostics;
using Offstream.Core.Settings;
using Xunit;

namespace Offstream.UI.Tests;

/// <summary>
/// The shell: the startup warning, and everything the tray icon shows.
/// </summary>
/// <remarks>
/// Runs without a <see cref="System.Windows.Application"/>, so <c>UiThread.Dispatch</c> finds no
/// dispatcher and runs its update inline — the same path a report already on the UI thread takes.
/// State changes therefore land synchronously; progress reports still cross a
/// <see cref="Progress{T}"/> hop, so the tests that need one wait for it.
/// </remarks>
public sealed class ShellViewModelTests
{
    /// <remarks>Cached because CA1863 asks for it, exactly as the ViewModel's copy is.</remarks>
    private static readonly CompositeFormat RecordingFormat = CompositeFormat.Parse(Strings.TrayRecording);

    [Fact]
    public void TrayTooltip_StartsIdle()
    {
        var viewModel = Build();

        Assert.False(viewModel.IsRecording);
        Assert.Equal(Strings.TrayIdle, viewModel.TrayTooltip);
    }

    [Fact]
    public async Task Starting_TurnsTheIconRedAndSaysItIsWaiting()
    {
        var controller = ControllerFor();
        await using var _ = controller;
        var viewModel = Build(controller);

        await controller.StartAsync();

        // The colour is the point: this is the one state the user cannot see the Record page in.
        Assert.True(viewModel.IsRecording);
        Assert.Equal(Strings.TrayWaiting, viewModel.TrayTooltip);
    }

    [Fact]
    public async Task Progress_WithATrack_NamesItInTheTooltip()
    {
        var factory = new FakeSessionFactory();
        var controller = ControllerFor(factory);
        await using var _ = controller;
        var viewModel = Build(controller);

        await controller.StartAsync();

        var tooltip = await TooltipAfter(
            viewModel,
            () => factory.Progress!.Report(
                new RecordingProgress(RecordingStage.Recording, "Someone - Something")));

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, RecordingFormat, "Someone - Something"),
            tooltip);
    }

    [Fact]
    public async Task Stopping_ForgetsTheTrackItWasNaming()
    {
        var factory = new FakeSessionFactory();
        var controller = ControllerFor(factory);
        await using var _ = controller;
        var viewModel = Build(controller);

        await controller.StartAsync();
        await TooltipAfter(
            viewModel,
            () => factory.Progress!.Report(
                new RecordingProgress(RecordingStage.Recording, "Someone - Something")));

        await controller.StopAsync();

        // A tooltip still naming a track would claim a recording that has already finished.
        Assert.False(viewModel.IsRecording);
        Assert.Equal(Strings.TrayIdle, viewModel.TrayTooltip);
    }

    [Fact]
    public void ShowCommand_AsksTheWindowToComeBack()
    {
        var viewModel = Build();
        var asked = 0;
        viewModel.ShowRequested += (_, _) => asked++;

        viewModel.ShowCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    [Fact]
    public void ExitCommand_AsksTheWindowToClose()
    {
        var viewModel = Build();
        var asked = 0;
        viewModel.ExitRequested += (_, _) => asked++;

        viewModel.ExitCommand.Execute(null);

        // The predecessor's tray icon had no menu at all, so a hidden app could only be quit by
        // restoring it first.
        Assert.Equal(1, asked);
    }

    [Fact]
    public void IsInTray_StartsFalse() =>
        // The icon exists only while the window is hidden, so a true here would put a permanent
        // tray citizen on screen from launch.
        Assert.False(Build().IsInTray);

    [Fact]
    public void MinimizeToTray_ReadsTheSetting()
    {
        var settings = OffstreamSettings.CreateDefault() with
        {
            App = new AppSettings(MinimizeToTray: false),
        };

        Assert.False(Build(settings: SettingsFakes.DocumentWith(settings)).MinimizeToTray);
    }

    [Fact]
    public void MinimizeToTray_FollowsALaterEdit()
    {
        var document = SettingsFakes.Document();
        var viewModel = Build(settings: document);

        Assert.True(viewModel.MinimizeToTray);

        document.Update(current => current with { App = current.App with { MinimizeToTray = false } });

        // Read fresh rather than cached, so toggling it on the Advanced page takes effect at the
        // next minimise instead of the next launch.
        Assert.False(viewModel.MinimizeToTray);
    }

    [Fact]
    public void HasStartupWarning_FollowsTheWarning()
    {
        var viewModel = Build();

        Assert.False(viewModel.HasStartupWarning);

        viewModel.StartupWarning = "settings.json could not be read.";

        Assert.True(viewModel.HasStartupWarning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasStartupWarning_IgnoresNothingWorthShowing(string? warning)
    {
        var viewModel = Build();

        viewModel.StartupWarning = warning;

        // The InfoBar binds to this; whitespace would open an empty warning bar at every launch.
        Assert.False(viewModel.HasStartupWarning);
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new ShellViewModel(null!, SettingsFakes.Document()));
        Assert.Throws<ArgumentNullException>(() => new ShellViewModel(ControllerFor(), null!));
    }

    /// <summary>Runs <paramref name="report"/> and waits for the tooltip it produces.</summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> posts to the context it was created on, so a report made here
    /// arrives after the call returns — the same hop that puts these on the UI thread in the
    /// running app.
    /// </remarks>
    private static async Task<string> TooltipAfter(ShellViewModel viewModel, Action report)
    {
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.TrayTooltip)) changed.TrySetResult(viewModel.TrayTooltip);
        }

        viewModel.PropertyChanged += OnPropertyChanged;

        try
        {
            report();

            return await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }
    }

    private static ShellViewModel Build(
        RecordingController? controller = null,
        SettingsDocument? settings = null) =>
        new(controller ?? ControllerFor(), settings ?? SettingsFakes.Document());

    private static RecordingController ControllerFor(FakeSessionFactory? factory = null) =>
        new(factory ?? new FakeSessionFactory(), RecordingFakes.Document());
}
