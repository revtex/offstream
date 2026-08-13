using System.Windows.Controls;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Settings tab. Code-behind is wiring only.</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        InitializeComponent();
    }
}
