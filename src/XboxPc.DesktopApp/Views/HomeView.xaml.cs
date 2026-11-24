using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using XboxPc.DesktopApp.ViewModels;

namespace XboxPc.DesktopApp.Views;

public partial class HomeView : System.Windows.Controls.UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => OnShown();
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    private void OnShown()
    {
        if (Motion.Enabled)
        {
            // The hero's text rises in first, the rows follow.
            var i = 0;
            foreach (UIElement section in Sections.Children)
            {
                if (section.Visibility != Visibility.Visible) continue;
                var delay = TimeSpan.FromMilliseconds(80 * i++);
                var move = new TranslateTransform(0, 22);
                section.RenderTransform = move;
                var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
                section.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(460)) { BeginTime = delay, EasingFunction = ease });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(620)) { BeginTime = delay, EasingFunction = ease });
            }
        }
    }

    private static string? XbePath(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && string.Equals(System.IO.Path.GetExtension(files[0]), ".xbe", StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = XbePath(e) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropOverlay.Visibility = Visibility.Visible;
        DropBorder.Stroke = (Brush)FindResource(ok ? "AccentBrush" : "WarnBrush");
        DropTitle.Text = ok ? "Release to boot" : "Only a single .xbe file can be booted";
        DropHint.Text = ok ? "The emulator window opens" : "Look for default.xbe in the program's folder";
        if (ok && Motion.Enabled)
            DropBorder.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -9, TimeSpan.FromMilliseconds(600)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            DropBorder.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = XbePath(e);
        DropOverlay.Visibility = Visibility.Collapsed;
        if (path is not null && DataContext is HomeViewModel vm) vm.RunXbeFile(path);
    }
}
