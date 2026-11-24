using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XboxPc.Application;
using XboxPc.DesktopApp.ViewModels;
using XboxPc.Domain;
using XboxPc.Emulation.Gpu.D3D12;
using XboxPc.Emulation.Gpu.Nv2a;
using Frame = XboxPc.Emulation.Gpu.Nv2a.Frame;
using XboxPc.Emulation.Input;
using InputMode = XboxPc.Emulation.Input.InputMode;
using XboxPc.Emulation.OriginalXbox;
using XboxPc.Input;

namespace XboxPc.DesktopApp.Views;

/// <summary>
/// Runs one XBE in the Original Xbox emulator core and presents it full-bleed: a launch transition with the title's
/// artwork until the first real frame, then only the picture. F1 (or Back + Start on a controller) opens the in-game
/// menu: resume, pause, input settings, picture options, performance stats, mute, screenshot, diagnostics and exit.
/// When the title stops, a screen shows the reason instead of the last frame.
/// Input: keyboard/mouse/controller through the Input Engine (focus and capture rules), rumble back to XInput.
/// The dashboard does not receive input while this window is active (InputArbiter).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "The session is disposed when the window closes (Closed -> Shutdown); WPF windows are not IDisposable.")]
public partial class EmulatorWindow : Window
{
    private static readonly TimeSpan MinCurtain = TimeSpan.FromMilliseconds(1100);

    private readonly AppHost _host;
    private readonly EmulationSession _session;
    private readonly WaveOutPlayer? _audio;
    private readonly string? _audioError;
    private readonly XbeImage _xbe;
    private readonly string _gameName;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mouseButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ushort[] _rumble = new ushort[8];
    private readonly ushort[] _rumbleSent = new ushort[8];
    private InputProfile _profile;
    private WriteableBitmap? _bitmap;
    private Frame? _shown;
    private bool _captured;
    private bool _fullscreen;
    private double _mouseDx, _mouseDy, _wheel;
    private double _lastTick;
    private long _lastStats;
    private string _debug = "";
    private bool _stopped;
    private bool _menuOpen;
    private bool _curtainUp = true;
    private PadButtons _padPrev;
    private double _hudLastMs;
    private long _hudFlips, _hudVBlanks;
    private double _hudVirtual;
    private bool _syncingToggles;
    private readonly D3D12Backend? _renderer;
    private readonly string _rendererText;
    private long _hudGpuFrames;
    private long _startLogSecond = -1;

    public EmulatorWindow(AppHost host, EmulatorLaunch launch, UserPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(prefs);
        InitializeComponent();
        _host = host;

        var xbePath = launch.XbePath;
        var file = File.ReadAllBytes(xbePath);
        _xbe = XbeImage.Parse(file);
        _gameName = !string.IsNullOrWhiteSpace(launch.DisplayName) ? launch.DisplayName
            : !string.IsNullOrWhiteSpace(_xbe.TitleName) ? _xbe.TitleName : Path.GetFileName(xbePath);
        var machine = new OriginalXboxMachine { ImageFileName = Path.GetFileName(xbePath) };
        // D: = the XBE's directory (read-only). E: = a per-title sandbox inside the app's data directory.
        var hdd = Path.Combine(host.DataDirectory, "harddisk", _xbe.TitleId.ToString("X8", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(hdd);
        machine.MountDefaults(Path.GetDirectoryName(Path.GetFullPath(xbePath))!, hdd);
        machine.Load(_xbe, file);
        // Direct3D 12 mirrors the NV2A's drawing at the chosen scale; the CPU renderer keeps running underneath.
        if (prefs.GpuRenderer)
        {
            _renderer = D3D12Backend.TryCreate(prefs.RenderScale, useWarp: false, ownsMemory: true, out var gpuError);
            if (_renderer is null) _rendererText = $"CPU picture (Direct3D 12 unavailable: {gpuError})";
            else
            {
                _renderer.Attach(machine.Gpu);
                _rendererText = $"Direct3D 12 · {_renderer.AdapterName} · {_renderer.Scale}x";
            }
        }
        else _rendererText = "CPU reference renderer";
        _profile = host.InputProfiles.ProfileForGame(_xbe.TitleId);
        machine.Input.Profile = _profile;
        machine.Input.CaptureChanged += (_, captured) => Dispatcher.BeginInvoke(() => ApplyCapture(captured));
        _audio = WaveOutPlayer.TryOpen(out _audioError);
        if (_audio is not null) _audio.Volume = prefs.Volume;
        machine.Audio.Output = _audio;
        _session = new EmulationSession(machine);
        _session.Stopped += (_, stats) => Dispatcher.BeginInvoke(() => OnStopped(stats));

        Title = $"{_gameName} - XBOX PC";
        SetupArtwork(launch.CoverPath);
        _syncingToggles = true;
        AspectToggle.IsChecked = prefs.KeepAspectRatio;
        SmoothToggle.IsChecked = prefs.SmoothScaling;
        MuteToggle.IsEnabled = _audio is not null;
        _syncingToggles = false;
        ApplyPicture();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick(), Dispatcher);
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) =>
        {
            if (prefs.LaunchFullscreen) SetFullscreen(true);
            _host.ActiveSession = _session;
            _host.ActiveTitle = _gameName;
            _host.ActiveRenderer = _rendererText;
            _session.Start();
            _timer.Start();
            ViewHost.Focus();
            if (Motion.Enabled) CurtainDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        };
        Activated += (_, _) => SetFocused(true);
        Deactivated += (_, _) => SetFocused(false);
        Closed += (_, _) => Shutdown();
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        ViewHost.MouseDown += OnMouseDown;
        ViewHost.MouseUp += OnMouseUp;
        ViewHost.MouseMove += OnMouseMove;
        ViewHost.MouseWheel += (_, e) => { if (_captured) _wheel += e.Delta / 120.0; }; // WPF: 120 per notch
    }

    public static string MouseButtonName(MouseButton b) => b switch
    {
        MouseButton.Left => "MouseLeft",
        MouseButton.Right => "MouseRight",
        MouseButton.Middle => "MouseMiddle",
        MouseButton.XButton1 => "MouseX1",
        _ => "MouseX2",
    };

    private static string KeyName(KeyEventArgs e) => (e.Key == Key.System ? e.SystemKey : e.Key).ToString();

    private void SetupArtwork(string? coverPath)
    {
        var initials = string.Concat(_gameName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        CurtainInitials.Text = initials;
        MenuInitials.Text = initials;
        CurtainTitle.Text = _gameName;
        MenuTitle.Text = _gameName;
        if (new SafeImageConverter().Convert(coverPath, typeof(ImageSource), "560", CultureInfo.InvariantCulture) is ImageSource art)
        {
            CurtainArt.Source = art;
            CurtainCover.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
            MenuCover.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        }
    }

    // ---------------------------------------------------------------- focus, keyboard, mouse

    private void SetFocused(bool focused)
    {
        _host.InputArbiter.SetEmulatorFocused(focused);
        if (!focused)
        {
            _keys.Clear();
            _mouseButtons.Clear();
            ApplyCapture(false);
        }
        _session.Post(m => m.Input.OnFocusChanged(focused));
        UpdateHint();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = KeyName(e);
        if (key == "F1") { ToggleMenu(); e.Handled = true; return; }
        if (_menuOpen)
        {
            // The menu owns the keyboard: arrows/Tab move focus natively, Enter selects, Esc closes.
            if (key == "Escape") { CloseMenu(); e.Handled = true; }
            else if (key == "Return" && Keyboard.FocusedElement is UIElement f) { e.Handled = Invoke(f); }
            return;
        }
        if (key.Equals(_profile.ReleaseCaptureKey, StringComparison.OrdinalIgnoreCase))
        {
            _session.Post(m => m.Input.ReleaseCapture());
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case "Pause": TogglePause(); e.Handled = true; return;
            case "F11": SetFullscreen(!_fullscreen); e.Handled = true; return;
            case "F12": SaveScreenshot(); e.Handled = true; return;
        }
        if (_stopped || _profile.Mode == InputMode.Controller) return; // keyboard is not game input in controller mode
        _keys.Add(key);
        e.Handled = true; // game keys never trigger WPF navigation (Tab, Alt menu, arrows)
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_keys.Remove(KeyName(e))) e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewHost.Focus();
        if (_stopped || _menuOpen) return;
        if (!_captured)
        {
            _session.Post(m => m.Input.RequestCapture()); // the capturing click itself is not game input
            return;
        }
        _mouseButtons.Add(MouseButtonName(e.ChangedButton));
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => _mouseButtons.Remove(MouseButtonName(e.ChangedButton));

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_captured) { ShowMenuButton(); return; }
        var c = CenterOnScreen();
        if (!NativeMethods.GetCursorPos(out var p)) return;
        _mouseDx += p.X - c.X;
        _mouseDy += p.Y - c.Y;
        if (p.X != c.X || p.Y != c.Y) NativeMethods.SetCursorPos(c.X, c.Y);
    }

    private NativeMethods.Point CenterOnScreen()
    {
        var pt = ViewHost.PointToScreen(new Point(ViewHost.ActualWidth / 2, ViewHost.ActualHeight / 2));
        return new NativeMethods.Point { X = (int)pt.X, Y = (int)pt.Y };
    }

    private void ApplyCapture(bool captured)
    {
        if (captured && (_menuOpen || _stopped)) captured = false;
        if (captured == _captured) return;
        _captured = captured;
        if (captured)
        {
            Mouse.Capture(ViewHost);
            ViewHost.Cursor = Cursors.None;
            var tl = ViewHost.PointToScreen(new Point(0, 0));
            var br = ViewHost.PointToScreen(new Point(ViewHost.ActualWidth, ViewHost.ActualHeight));
            var rect = new NativeMethods.Rect { Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y };
            NativeMethods.ClipCursor(ref rect);
            var c = CenterOnScreen();
            NativeMethods.SetCursorPos(c.X, c.Y);
        }
        else
        {
            NativeMethods.ClipCursor(IntPtr.Zero);
            ViewHost.Cursor = Cursors.Arrow;
            if (Mouse.Captured == ViewHost) Mouse.Capture(null);
            _mouseButtons.Clear();
        }
        _mouseDx = _mouseDy = _wheel = 0;
        UpdateHint();
    }

    /// <summary>One short hint at the bottom of the picture, only when it tells the player something they need.</summary>
    private void UpdateHint()
    {
        string text;
        if (_curtainUp || _stopped || _menuOpen) text = "";
        else if (!IsActive) text = "Window not focused: no input reaches the game";
        else if (_profile.Mode != InputMode.Controller && !_captured) text = "Click to capture the mouse  ·  F1 menu";
        else if (_captured) text = $"{_profile.ReleaseCaptureKey} releases the mouse  ·  F1 menu";
        else text = "";
        if (text.Length > 0) HintText.Text = text;
        Hint.BeginAnimation(OpacityProperty, new DoubleAnimation(text.Length > 0 ? 1 : 0, TimeSpan.FromMilliseconds(Motion.Enabled ? 220 : 1)));
    }

    // ---------------------------------------------------------------- per-frame work

    private void Tick()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var dt = _lastTick == 0 ? 1.0 / 60 : Math.Clamp((now - _lastTick) / 1000, 0.001, 0.25);
        _lastTick = now;

        PollPadShortcuts();
        if (!_stopped) SubmitInput(dt, (long)now);
        PresentFrame();
        ApplyRumble();
        if ((long)now - _lastStats >= 250)
        {
            _lastStats = (long)now;
            ShowStats();
        }
    }

    /// <summary>Back + Start toggles the menu; while it is open the controller drives the menu, not the game.</summary>
    private void PollPadShortcuts()
    {
        if (!IsActive) { _padPrev = PadButtons.None; return; }
        var source = _host.Controllers.Source;
        var buttons = PadButtons.None;
        short ly = 0;
        for (var i = 0; i < source.SlotCount; i++)
            if (source.Read(i) is { } r) { buttons |= r.Buttons; if (Math.Abs((int)r.LeftY) > Math.Abs((int)ly)) ly = r.LeftY; }
        if (ly > 16000) buttons |= PadButtons.DPadUp;
        else if (ly < -16000) buttons |= PadButtons.DPadDown;
        var pressed = buttons & ~_padPrev;
        _padPrev = buttons;

        const PadButtons chord = PadButtons.Back | PadButtons.Start;
        if ((buttons & chord) == chord && (pressed & chord) != 0) { ToggleMenu(); return; }
        if (!_menuOpen && !_stopped) return;
        if ((pressed & PadButtons.DPadUp) != 0) (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
        if ((pressed & PadButtons.DPadDown) != 0) (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
        if ((pressed & PadButtons.DPadLeft) != 0) (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Left));
        if ((pressed & PadButtons.DPadRight) != 0) (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));
        if ((pressed & PadButtons.A) != 0 && Keyboard.FocusedElement is UIElement f) Invoke(f);
        if ((pressed & PadButtons.B) != 0 && _menuOpen) CloseMenu();
    }

    private static bool Invoke(UIElement element)
    {
        switch (element)
        {
            case CheckBox box: box.IsChecked = !(box.IsChecked ?? false); return true;
            case ButtonBase b when b.IsEnabled: b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b)); return true;
            default: return false;
        }
    }

    private void SubmitInput(double dt, long nowMs)
    {
        var frame = new HostInputFrame { MouseDeltaX = _mouseDx, MouseDeltaY = _mouseDy, WheelDelta = (int)Math.Round(_wheel) };
        _mouseDx = _mouseDy = _wheel = 0;
        if (IsActive && !_menuOpen)
        {
            frame.Keys.UnionWith(_keys);
            if (_captured) frame.MouseButtons.UnionWith(_mouseButtons);
            var source = _host.Controllers.Source;
            for (var i = 0; i < InputEngine.PortCount && i < source.SlotCount; i++)
                if (source.Read(i) is { } r)
                    frame.Pads[i] = new HostPad((HostPadButtons)(ushort)r.Buttons, r.LeftTrigger, r.RightTrigger, r.LeftX, r.LeftY, r.RightX, r.RightY);
        }
        var rumble = _rumble;
        _session.Post(m =>
        {
            m.Input.Update(frame, dt, nowMs);
            for (var p = 0; p < InputEngine.PortCount; p++)
            {
                var s = m.Input.Rumble(p);
                Volatile.Write(ref rumble[p * 2], s.Left);
                Volatile.Write(ref rumble[(p * 2) + 1], s.Right);
            }
        });
    }

    private void ApplyRumble()
    {
        if (_host.Controllers.Source is not XInputGamepadSource xinput) return;
        for (var p = 0; p < InputEngine.PortCount; p++)
        {
            var l = Volatile.Read(ref _rumble[p * 2]);
            var r = Volatile.Read(ref _rumble[(p * 2) + 1]);
            if (l == _rumbleSent[p * 2] && r == _rumbleSent[(p * 2) + 1]) continue;
            _rumbleSent[p * 2] = l;
            _rumbleSent[(p * 2) + 1] = r;
            xinput.SetVibration(p, l, r);
        }
    }

    private void PresentFrame()
    {
        var f = _session.LatestFrame;
        if (f is null || ReferenceEquals(f, _shown)) return;
        _shown = f;
        if (_bitmap is null || _bitmap.PixelWidth != f.Width || _bitmap.PixelHeight != f.Height)
        {
            _bitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgr32, null);
            Screen.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
        if (_curtainUp && !_stopped) LowerCurtain();
    }

    /// <summary>The first real frame exists: the artwork gives way to the game's own picture.</summary>
    private void LowerCurtain()
    {
        var wait = MinCurtain - _clock.Elapsed;
        if (wait > TimeSpan.Zero)
        {
            CurtainStatus.Text = "Running";
            return; // the next frame after the minimum transition time lowers it
        }
        _curtainUp = false;
        if (Motion.Enabled)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new SineEase() };
            fade.Completed += (_, _) => Curtain.Visibility = Visibility.Collapsed;
            Curtain.BeginAnimation(OpacityProperty, fade);
            var grow = new ScaleTransform(1, 1, 0.5, 0.5);
            CurtainContent.RenderTransformOrigin = new Point(0.5, 0.5);
            CurtainContent.RenderTransform = grow;
            var ease = new QuarticEase { EasingMode = EasingMode.EaseIn };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
        }
        else Curtain.Visibility = Visibility.Collapsed;
        UpdateHint();
    }

    private void ShowStats()
    {
        var s = _session.Stats;
        var now = _clock.Elapsed.TotalMilliseconds;
        // start-up trace in the app log: one line per second for the first 30 s (diagnoses slow starts)
        if (now < 30_000 && (long)(now / 1000) != _startLogSecond)
        {
            _startLogSecond = (long)(now / 1000);
            _host.Log.Info("Emulator", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"t={now / 1000:F0}s state {s.State} retired {s.Instructions:N0} mips {s.Mips:F1} virtual {s.VirtualMs:F0} ms wall {s.WallMs:F0} ms vblanks {s.VBlanks} flips {s.Flips} gpu frames {_renderer?.FramesPresented ?? 0}"));
        }
        if (_curtainUp && !_stopped && _clock.Elapsed > TimeSpan.FromSeconds(3) && _shown is null)
            CurtainStatus.Text = "Running · the program has not shown a picture yet";

        // On-screen stats, measured over the last half second.
        if (now - _hudLastMs >= 500)
        {
            var dt = (now - _hudLastMs) / 1000;
            var fps = (s.Flips - _hudFlips) / dt;
            var hz = (s.VBlanks - _hudVBlanks) / dt;
            var speed = (s.VirtualMs - _hudVirtual) / 1000 / dt;
            var gpuFrames = _renderer?.FramesPresented ?? 0;
            var picture = _renderer is null ? "cpu picture"
                : _renderer.FailureReason is not null ? "cpu picture (Direct3D 12 stopped)"
                : gpuFrames > _hudGpuFrames ? $"Direct3D 12 {_renderer.Scale}x" : "cpu picture (no GPU frame)";
            _hudGpuFrames = gpuFrames;
            if (_hudLastMs > 0 && Hud.Visibility == Visibility.Visible)
            {
                HudText.Text = string.Create(CultureInfo.InvariantCulture,
                    $"{(s.Flips == 0 ? "game     - fps (no buffer flips)" : $"game  {fps,5:0} fps  {(fps >= 0.5 ? 1000 / fps : 0),6:0.0} ms")}\nspeed {speed * 100,5:0} %   video {hz:0} Hz\ncpu   {s.Mips,5:0.0} MIPS\n{picture}");
            }
            _hudLastMs = now;
            _hudFlips = s.Flips;
            _hudVBlanks = s.VBlanks;
            _hudVirtual = s.VirtualMs;
        }

        if (!_menuOpen) return;
        MenuState.Text = s.State switch
        {
            SessionState.Running => string.Create(CultureInfo.InvariantCulture, $"Running · {s.Speed:P0} speed"),
            SessionState.Paused => "Paused",
            SessionState.Stopped => "Stopped",
            _ => "Starting",
        };
        PauseItem.Content = _session.IsPaused ? "Continue game" : "Pause";
        DiagStats.Text = string.Create(CultureInfo.InvariantCulture,
            $"Title ID {_xbe.TitleId:X8} · emulated {s.VirtualMs / 1000:F1} s in {s.WallMs / 1000:F1} s · {s.Mips:F1} MIPS · {s.VBlanks} vblanks · {s.Flips} buffer flips · {s.Interrupts} interrupts · USB {s.UsbFrames} frames · audio {AudioText()}") +
            (s.UnmodelledCount > 0 ? $"\nUnmodelled hardware accessed {s.UnmodelledCount}x, last: {s.LastUnmodelled}" : "") +
            $"\nRenderer: {_rendererText}" +
            (_renderer is { } r ? $" · {r.Draws} draws, {r.ShaderCount} shaders, {r.FramesPresented} frames shown" + (r.FailureReason is { } why ? $" · stopped: {why}" : "") : "");
        _session.Post(m =>
        {
            var text = m.Kernel.DebugOutput;
            if (m.UnmodelledAccesses.Count > 0) text += "\n--- unmodelled hardware ---\n" + string.Join("\n", m.UnmodelledAccesses.Distinct().Take(100));
            Volatile.Write(ref _debug, text);
        });
        var debug = Volatile.Read(ref _debug);
        if (DebugText.Text.Length != debug.Length) DebugText.Text = debug.Length > 0 ? debug : "The title has not written any debug output.";
    }

    private string AudioText() => _audio is null
        ? $"unavailable ({_audioError})"
        : $"{_audio.BlocksPlayed * 20} ms played{(_audio.Dropped > 0 ? $", {_audio.Dropped * 20} ms dropped" : "")}{(_audio.Muted ? ", muted" : "")}";

    private void OnStopped(SessionStats stats)
    {
        _stopped = true;
        _session.Stop();
        ApplyCapture(false);
        PresentFrame();
        CloseMenu();
        var m = _session.Machine;
        var exited = stats.Stop == MachineStop.TitleExited;
        FailTitle.Text = exited ? $"{_gameName} has closed" : $"{_gameName} stopped";
        FailReason.Text = exited ? "The program exited on its own." : $"{stats.Stop}: {stats.Detail}";
        FailHint.Text = exited
            ? "Nothing was lost: anything it saved is in its E: drive folder."
            : "The emulator cannot continue this program. This is a limit of the emulator, not of your PC; the diagnostics show what it was doing.";
        FailDebug.Text = string.Create(CultureInfo.InvariantCulture,
            $"Title ID {_xbe.TitleId:X8} · {stats.Instructions:N0} instructions · emulated {stats.VirtualMs / 1000:F2} s · {stats.Mips:F1} MIPS · {stats.VBlanks} vblanks · {stats.Flips} flips\n") +
            (m.Kernel.DebugOutput.Length > 0 ? m.Kernel.DebugOutput : "(no debug output)") +
            (m.UnmodelledAccesses.Count > 0 ? "\n--- unmodelled hardware ---\n" + string.Join("\n", m.UnmodelledAccesses.Distinct().Take(100)) : "");
        Hud.Visibility = Visibility.Collapsed;
        Curtain.Visibility = Visibility.Collapsed;
        Failure.Visibility = Visibility.Visible;
        if (Motion.Enabled) Failure.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
        UpdateHint();
        FailExit.Focus();
    }

    // ---------------------------------------------------------------- in-game menu

    private void ToggleMenu()
    {
        if (_menuOpen) CloseMenu();
        else OpenMenu();
    }

    private void OpenMenu()
    {
        if (_stopped || _menuOpen) return;
        _menuOpen = true;
        _keys.Clear();
        _session.Post(m => m.Input.ReleaseCapture());
        ApplyCapture(false);
        _syncingToggles = true;
        FullscreenToggle.IsChecked = _fullscreen;
        StatsToggle.IsChecked = Hud.Visibility == Visibility.Visible;
        MuteToggle.IsChecked = _audio?.Muted == true;
        _syncingToggles = false;
        MenuMessage.Text = "";
        Menu.Visibility = Visibility.Visible;
        var d = TimeSpan.FromMilliseconds(Motion.Enabled ? 300 : 1);
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        Menu.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
        MenuMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-40, 0, d) { EasingFunction = ease });
        ShowStats();
        UpdateHint();
        ResumeItem.Focus();
    }

    private void CloseMenu()
    {
        if (!_menuOpen) return;
        _menuOpen = false;
        DiagPanel.Visibility = Visibility.Collapsed;
        var d = TimeSpan.FromMilliseconds(Motion.Enabled ? 180 : 1);
        var fade = new DoubleAnimation(0, d);
        fade.Completed += (_, _) => { if (!_menuOpen) Menu.Visibility = Visibility.Collapsed; };
        Menu.BeginAnimation(OpacityProperty, fade);
        ViewHost.Focus();
        UpdateHint();
    }

    private void OnScrimMouseDown(object sender, MouseButtonEventArgs e) => CloseMenu();

    private DispatcherTimer? _menuButtonTimer;

    /// <summary>Shows the menu button for a moment while the pointer moves over the picture (not while the mouse is captured).</summary>
    private void ShowMenuButton()
    {
        if (_menuOpen || _stopped || _curtainUp) return;
        MenuButton.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Motion.Enabled ? 150 : 1)));
        _menuButtonTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Normal, (_, _) =>
        {
            _menuButtonTimer!.Stop();
            if (!MenuButton.IsMouseOver) MenuButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Motion.Enabled ? 400 : 1)));
        }, Dispatcher);
        _menuButtonTimer.Stop();
        _menuButtonTimer.Start();
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        MenuButton.BeginAnimation(OpacityProperty, null);
        MenuButton.Opacity = 0;
        OpenMenu();
    }

    private void OnResumeClick(object sender, RoutedEventArgs e)
    {
        if (_session.IsPaused) TogglePause();
        CloseMenu();
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        TogglePause();
        PauseItem.Content = _session.IsPaused ? "Continue game" : "Pause";
        MenuState.Text = _session.IsPaused ? "Paused" : "Running";
    }

    private void OnPictureChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;
        ApplyPicture();
    }

    private void ApplyPicture()
    {
        Screen.Stretch = AspectToggle.IsChecked == true ? Stretch.Uniform : Stretch.Fill;
        RenderOptions.SetBitmapScalingMode(Screen, SmoothToggle.IsChecked == true ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
    }

    private void OnFullscreenToggled(object sender, RoutedEventArgs e)
    {
        if (!_syncingToggles) SetFullscreen(FullscreenToggle.IsChecked == true);
    }

    private void OnStatsToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;
        Hud.Visibility = StatsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HudText.Text = "measuring...";
    }

    private void OnMuteToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles || _audio is null) return;
        _audio.Muted = MuteToggle.IsChecked == true;
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        DiagPanel.Visibility = DiagPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowStats();
    }

    private void OnFailDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        FailDebug.Visibility = FailDebug.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        FailDiag.Content = FailDebug.Visibility == Visibility.Visible ? "Hide diagnostics" : "Show diagnostics";
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnScreenshotClick(object sender, RoutedEventArgs e) => MenuMessage.Text = SaveScreenshot();

    private void OnInputClick(object sender, RoutedEventArgs e)
    {
        var vm = new InputConfigViewModel(_host.InputProfiles, _profile, _xbe.TitleId, _gameName);
        if (new InputConfigWindow(vm) { Owner = this }.ShowDialog() == true && vm.Saved is { } p)
        {
            _profile = p;
            _keys.Clear();
            _session.Post(m => m.Input.Profile = p);
            MenuMessage.Text = $"Input profile \"{p.Name}\" in use.";
        }
    }

    // ---------------------------------------------------------------- commands

    private void TogglePause()
    {
        if (_stopped) return;
        if (_session.IsPaused) _session.Resume(); else { _session.Pause(); _session.Post(m => m.Input.ReleaseCapture()); }
    }

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        WindowState = WindowState.Normal;
        WindowStyle = on ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = on ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowState = on ? WindowState.Maximized : WindowState.Normal;
    }

    private string SaveScreenshot()
    {
        if (_bitmap is null) return "No picture yet: the program has not set a display mode.";
        var dir = Path.Combine(_host.DataDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{_xbe.TitleId:X8}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_bitmap));
        using (var fs = File.Create(path)) encoder.Save(fs);
        return $"Screenshot saved to {Path.GetFileName(path)}";
    }

    private void Shutdown()
    {
        _timer.Stop();
        ApplyCapture(false);
        _session.Dispose();
        _renderer?.Dispose();
        _audio?.Dispose();
        if (_host.Controllers.Source is XInputGamepadSource xinput)
            for (var p = 0; p < InputEngine.PortCount; p++) xinput.SetVibration(p, 0, 0);
        _host.InputArbiter.SetEmulatorFocused(false);
        if (ReferenceEquals(_host.ActiveSession, _session))
        {
            _host.ActiveSession = null;
            _host.ActiveTitle = null;
            _host.ActiveRenderer = null;
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClipCursor(ref Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClipCursor(IntPtr rect);
    }
}
