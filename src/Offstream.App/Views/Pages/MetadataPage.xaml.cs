using System.Windows.Controls;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Metadata tab. Code-behind is wiring only.</summary>
public partial class MetadataPage : UserControl
{
    public MetadataPage(MetadataViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        InitializeComponent();
    }
}
