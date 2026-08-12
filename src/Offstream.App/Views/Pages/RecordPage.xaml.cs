using System.Windows.Controls;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Record tab. Code-behind is wiring only.</summary>
public partial class RecordPage : Page
{
    public RecordPage(RecordViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
