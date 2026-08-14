using System.Windows.Controls;
using Offstream.App.ViewModels;

namespace Offstream.App.Views.Pages;

/// <summary>The Record tab. Code-behind is wiring only.</summary>
/// <remarks>
/// It used to own the log's auto-scroll as well; that moved to <see cref="LogsPage"/> along with
/// the log itself, which is why there is nothing here but the DataContext.
/// </remarks>
public partial class RecordPage : UserControl
{
    public RecordPage(RecordViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        InitializeComponent();
    }
}
