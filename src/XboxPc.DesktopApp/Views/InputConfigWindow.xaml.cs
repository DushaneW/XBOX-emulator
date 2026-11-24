using System.Windows;
using System.Windows.Input;
using XboxPc.DesktopApp.ViewModels;

namespace XboxPc.DesktopApp.Views;

public partial class InputConfigWindow : Window
{
    private readonly InputConfigViewModel _vm;

    public InputConfigWindow(InputConfigViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_vm.IsListening) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = _vm.OfferKey(key.ToString());
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsListening) return;
        // The click on the "Rebind" button itself has already been processed (listening starts after it).
        e.Handled = _vm.OfferKey(EmulatorWindow.MouseButtonName(e.ChangedButton));
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.Save()) DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
