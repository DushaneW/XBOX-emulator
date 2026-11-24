using System.Windows;
using XboxPc.DesktopApp.ViewModels;

namespace XboxPc.DesktopApp.Views;

public partial class ReportDialog : Window
{
    public ReportDialog(ReportEditorViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += (_, saved) => { DialogResult = saved; };
        Loaded += (_, _) => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    public void Cancel() => DialogResult = false;

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();
}
