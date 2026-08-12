using System.Windows.Controls;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Advanced tab. Code-behind is wiring only.</summary>
public partial class AdvancedPage : Page
{
    public AdvancedPage(AdvancedViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        InitializeComponent();
    }
}
