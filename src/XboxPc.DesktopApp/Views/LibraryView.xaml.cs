using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XboxPc.DesktopApp.ViewModels;

namespace XboxPc.DesktopApp.Views;

/// <summary>
/// My Games. Opening a title shows its game page in place (focus goes to Play, or Back when Play is unavailable);
/// closing it returns focus to the tile that was opened, so controller navigation never loses its place.
/// </summary>
public partial class LibraryView : UserControl
{
    private LibraryViewModel? _vm;

    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as LibraryViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as LibraryViewModel);
    }

    private void Attach(LibraryViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.DetailClosed -= OnDetailClosed;
        }
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnChanged;
        vm.DetailClosed += OnDetailClosed;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryViewModel.IsDetailOpen) || _vm is not { IsDetailOpen: true }) return;
        if (Motion.Enabled)
        {
            var move = new TranslateTransform(0, 24);
            DetailPane.RenderTransform = move;
            var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
            DetailPane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = ease });
        }
        Dispatcher.BeginInvoke(() =>
        {
            var play = Find<Button>(DetailPane, b => b.Name == "PlayButton" && b.IsVisible);
            var back = Find<Button>(DetailPane, b => b.Name == "BackButton");
            (play ?? back)?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnDetailClosed(object? sender, EventArgs e)
    {
        var id = _vm?.ReturnFocusId;
        Dispatcher.BeginInvoke(() =>
        {
            var host = _vm?.RowLayout == true ? (DependencyObject)RowItems : GridItems;
            var tile = Find<Button>(host, b => id is not null && b.Tag is Guid g && g == id) ?? Find<Button>(host, _ => true);
            if (tile is not null) tile.Focus();
            else SearchBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t)) return t;
            if (Find(child, match) is { } found) return found;
        }
        return null;
    }
}
