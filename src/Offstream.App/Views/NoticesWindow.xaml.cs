using Wpf.Ui.Controls;

namespace Offstream.App.Views;

/// <summary>
/// Offstream's own licence and the third-party notices that ship with it.
/// </summary>
/// <remarks>
/// No constructor dependencies and no ViewModel: everything on it comes from
/// <see cref="Services.ThirdPartyNotices"/> through <c>x:Static</c>, so the window is created
/// directly by whichever view opens it rather than resolved from the container. Nothing here
/// needs to outlive the dialog.
/// </remarks>
public partial class NoticesWindow : FluentWindow
{
    public NoticesWindow() => InitializeComponent();
}
