using Offstream.App.ViewModels;
using Wpf.Ui.Controls;

namespace Offstream.App.Views;

/// <summary>
/// The shell window. Code-behind is wiring only, per the MVVM convention in CLAUDE.md.
/// </summary>
public partial class ShellWindow : FluentWindow
{
    public ShellWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
