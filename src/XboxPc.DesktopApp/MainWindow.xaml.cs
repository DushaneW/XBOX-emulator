using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using XboxPc.DesktopApp.ViewModels;
using XboxPc.DesktopApp.Views;
using XboxPc.Domain;
using XboxPc.Emulation.Input;
using XboxPc.Input;

namespace XboxPc.DesktopApp;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppHost _host;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _secondTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Dictionary<Key, UiAction> _keyMap = [];
    private bool _renderingHooked;
    private bool _animations = true;
    private bool _fullscreen;
    private bool _shown;

    public MainWindow(MainViewModel vm, AppHost host)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(host);
        InitializeComponent();
        _vm = vm;
        _host = host;
        DataContext = vm;

        vm.ShowReportDialog = ShowReportDialog;
        vm.OpenEmulator = launch => new EmulatorWindow(host, launch, vm.Preferences) { Owner = this }.Show();
        vm.OpenInputSettings = OpenInputSettings;
        vm.PageChanged += (_, _) => OnPageChanged();
        vm.PreferencesApplied += (_, prefs) => ApplyPreferences(prefs);
        vm.PropertyChanged += OnViewModelChanged;
        vm.Settings.InputMapChanged += (_, map) => BuildKeyMap(map);
        vm.Performance.MeasuringChanged += (_, _) => UpdateRenderingHook();
        host.Controllers.ActionTriggered += OnControllerAction;
        host.Controllers.Connected += (_, e) => vm.StatusMessage = $"Controller connected in slot {e.Slot + 1}.";
        host.Controllers.Disconnected += (_, e) => vm.StatusMessage = $"Controller in slot {e.Slot + 1} disconnected.";
        BuildKeyMap(host.Controllers.Map);

        _pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => PollControllers(), Dispatcher);
        _secondTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnSecond(), Dispatcher);
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.6) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) =>
        {
            _pollTimer.Start();
            _secondTimer.Start();
            if (!_shown && _animations && Motion.Enabled)
            {
                RootGrid.Opacity = 0;
                RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
            }
            _shown = true;
            Dispatcher.BeginInvoke(FocusMenu, DispatcherPriority.Loaded);
        };
        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            _secondTimer.Stop();
            _toastTimer.Stop();
            if (_renderingHooked) CompositionTarget.Rendering -= OnRendering;
            vm.Performance.Dispose();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void PollControllers()
    {
        _host.Controllers.Poll(_clock.Elapsed);
        if (_vm.CurrentPage.ViewModel == _vm.Settings) _vm.Settings.RefreshControllerDiagnostics();
    }

    private void OnSecond()
    {
        _vm.UpdateClock();
        _vm.Performance.Tick();
        UpdateRenderingHook();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StatusMessage) && _vm.StatusMessage.Length > 0) ShowToast(_vm.StatusMessage);
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        _toastTimer.Stop();
        _toastTimer.Start();
        var d = TimeSpan.FromMilliseconds(Motion.Enabled ? 260 : 1);
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        ToastMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, d) { EasingFunction = ease });
    }

    private void HideToast()
    {
        var d = TimeSpan.FromMilliseconds(Motion.Enabled ? 320 : 1);
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        ToastMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, d));
    }

    private void OnControllerAction(object? sender, UiActionEventArgs e)
    {
        if (!_host.InputArbiter.DashboardMayHandle) return; // an emulator window owns input
        // Route to whichever of our windows is active (main window or a dialog).
        var target = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (target is null) return; // app is in the background: never act on controller input
        if (target is ReportDialog dialog) { InputRouter.Apply(dialog, e.Action, back: dialog.Cancel); return; }
        if (target == this) RouteAction(e.Action);
    }

    private bool RouteAction(UiAction action) =>
        InputRouter.Apply(this, action, _vm.NavigateRelative, () => { if (!_vm.GoBack()) FocusMenu(); }, FocusMenu);

    private void FocusMenu()
    {
        var index = _vm.Pages.IndexOf(_vm.CurrentPage);
        if (NavItems.ItemContainerGenerator.ContainerFromIndex(index) is ContentPresenter cp &&
            VisualTreeHelper.GetChildrenCount(cp) > 0 && VisualTreeHelper.GetChild(cp, 0) is UIElement radio)
            radio.Focus();
    }

    private void BuildKeyMap(InputMap map)
    {
        var dict = new Dictionary<Key, UiAction>();
        foreach (var (action, bindings) in map.Bindings)
            foreach (var b in bindings.Where(b => b.StartsWith("Key.", StringComparison.Ordinal)))
                if (Enum.TryParse<Key>(b[4..], ignoreCase: true, out var key)) dict[key] = action;
        _keyMap = dict;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None) { SetFullscreen(!_fullscreen); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None || !_keyMap.TryGetValue(key, out var action)) return;
        var focused = Keyboard.FocusedElement;
        if (InputRouter.IsTextInput(focused) && action is not (UiAction.PreviousPage or UiAction.NextPage or UiAction.Menu)) return;
        if (focused is ComboBox { IsDropDownOpen: true } || focused is ComboBoxItem) return;
        if (focused is ListBoxItem && action is UiAction.Up or UiAction.Down && key is Key.Up or Key.Down) return; // native list navigation
        if (focused is Slider && action is UiAction.Left or UiAction.Right && key is Key.Left or Key.Right) return;
        e.Handled = RouteAction(action);
    }

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        // Normal first so a maximised window really covers the taskbar when the border is removed.
        WindowState = WindowState.Normal;
        WindowStyle = on ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = on ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowState = on ? WindowState.Maximized : WindowState.Normal;
    }

    private void OnPageChanged()
    {
        if (!_animations || !Motion.Enabled) return;
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(380);
        var translate = new TranslateTransform();
        PageHost.RenderTransform = translate;
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, duration) { EasingFunction = ease });
    }

    private void OpenInputSettings(uint? titleId, string? gameName)
    {
        var store = _host.InputProfiles;
        var profile = titleId is { } t ? store.ProfileForGame(t) : store.Load("Default") ?? InputProfile.CreateDefault();
        var vm = new InputConfigViewModel(store, profile, titleId, gameName);
        new InputConfigWindow(vm) { Owner = this }.ShowDialog();
    }

    /// <summary>Applies render preset and accessibility preferences to this app's own rendering.</summary>
    public void ApplyPreferences(UserPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        var resources = System.Windows.Application.Current.Resources;
        resources["CardShadow"] = prefs.RenderPreset == RenderQualityPreset.Quality
            ? new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black }
            : null;
        RenderOptions.SetBitmapScalingMode(this, prefs.RenderPreset switch
        {
            RenderQualityPreset.Quality => BitmapScalingMode.HighQuality,
            RenderQualityPreset.Balanced => BitmapScalingMode.Linear,
            _ => BitmapScalingMode.LowQuality,
        });
        _animations = !prefs.ReduceMotion && prefs.RenderPreset != RenderQualityPreset.Performance;
        Motion.Enabled = _animations && SystemParameters.ClientAreaAnimation;
        // The blurred backdrop is the most expensive effect on screen: off in the Performance preset.
        Backdrop.Visibility = prefs.RenderPreset == RenderQualityPreset.Performance ? Visibility.Collapsed : Visibility.Visible;
        UiScaleTransform.ScaleX = UiScaleTransform.ScaleY = prefs.Normalized().UiScale;
        if (IsLoaded || !_shown) SetFullscreen(prefs.DashboardFullscreen);
        UpdateRenderingHook();
    }

    private void UpdateRenderingHook()
    {
        var needed = _vm.Performance.NeedsFrameCallbacks;
        if (needed == _renderingHooked) return;
        if (needed) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = needed;
        if (!needed) _vm.FrameStatsText = "";
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs r) _vm.Performance.OnFrame(r.RenderingTime);
    }

    private bool? ShowReportDialog(ReportEditorViewModel vm) => new ReportDialog(vm) { Owner = this }.ShowDialog();
}

/// <summary>Dark window caption to match the dashboard (Windows 10 20H1+ / 11; ignored elsewhere).</summary>
internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var on = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
        var caption = 0x000E0C0A; // COLORREF 0x00BBGGRR = #0A0C0E
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
