using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace XboxPc.DesktopApp;

/// <summary>
/// Start-up sequence. Stage 1: a black full-screen stage (the real start-up work begins immediately).
/// Stage 2: the aperture mark assembles, a light sweep crosses it and the wordmark settles. Stage 3: the original
/// chime (<see cref="StartupSound"/>) starts with the mark and rings when the light passes. Stage 4: the status line
/// names the real step still running, if any. Stage 5: fade to black and hand over to the dashboard; a failure
/// is shown here with the reason instead of continuing. Esc, Enter, a click or the A button skips.
/// </summary>
public partial class SplashWindow : Window
{
    private static readonly Duration Slow = TimeSpan.FromMilliseconds(560);
    private readonly LinearGradientBrush _markBrush;
    private readonly GradientStop _sweepA, _sweepB, _sweepC;
    private readonly DispatcherTimer _padTimer;
    private TaskCompletionSource? _intro;
    private TaskCompletionSource? _failure;
    private StartupSound? _sound;
    private bool _workPending = true;

    public SplashWindow()
    {
        InitializeComponent();
        _sweepA = new GradientStop(Color.FromRgb(0xD5, 0xDA, 0xDE), -0.1);
        _sweepB = new GradientStop(Colors.White, -0.05);
        _sweepC = new GradientStop(Color.FromRgb(0xD5, 0xDA, 0xDE), 0);
        _markBrush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(-40, -40),
            EndPoint = new Point(160, 160),
            GradientStops = { new GradientStop(Color.FromRgb(0xD5, 0xDA, 0xDE), 0), _sweepA, _sweepB, _sweepC, new GradientStop(Color.FromRgb(0xD5, 0xDA, 0xDE), 1) },
        };
        foreach (var bar in Bars) bar.Stroke = _markBrush;
        BuildWordmark();
        Logo.Opacity = 0;

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => OpenFolder?.Invoke()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => _failure?.TrySetResult()));
        PreviewKeyDown += (_, e) =>
        {
            if (_failure is not null) return;
            if (e.Key is Key.Escape or Key.Enter or Key.Space) { Skip(); e.Handled = true; }
        };
        MouseDown += (_, _) => { if (_failure is null) Skip(); };
        _padTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => { if (SkipPressed?.Invoke() == true) Skip(); }, Dispatcher);
        Closed += (_, _) => { _padTimer.Stop(); _sound?.Dispose(); };
    }

    /// <summary>Polled while the intro runs: true when a controller button that skips is down.</summary>
    public Func<bool>? SkipPressed { get; set; }

    /// <summary>Opens the data folder from the failure screen.</summary>
    public Action? OpenFolder { get; set; }

    public bool Skipped { get; private set; }

    private Line[] Bars => [Bar1, Bar2, Bar3, Bar4];

    private void BuildWordmark()
    {
        foreach (var (ch, i) in "XBOX PC".Select((c, i) => (c, i)))
        {
            var dim = i >= 5;
            Word.Children.Add(new TextBlock
            {
                Text = ch == ' ' ? " " : ch.ToString(),
                FontFamily = (FontFamily)FindResource("DisplayFont"),
                FontSize = 30,
                FontWeight = dim ? FontWeights.Light : FontWeights.SemiBold,
                Foreground = dim ? (Brush)FindResource("MutedBrush") : Brushes.White,
                Margin = new Thickness(9, 0, 9, 0),
                Opacity = 0,
                TextWrapping = TextWrapping.NoWrap,
            });
        }
    }

    /// <summary>Names the start-up step that is actually running. Shown only if it is still running when the logo has settled.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>All start-up work finished: the status line is not needed any more.</summary>
    public void WorkFinished()
    {
        _workPending = false;
        StatusPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>Plays the logo sequence. Completes when it has finished or was skipped.</summary>
    public Task PlayIntroAsync(StartupSound? sound, bool reducedMotion)
    {
        _intro = new TaskCompletionSource();
        _sound = sound;
        _padTimer.Start();
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        Logo.Opacity = 1;
        _sound?.Play();

        if (reducedMotion)
        {
            // No movement: everything fades in together.
            foreach (var bar in Bars) bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Slow));
            foreach (UIElement letter in Word.Children) letter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Slow));
            Finish(Ms(1600));
            return _intro.Task;
        }

        // Stage 2a: the four bars travel in from outside the aperture and settle.
        var origins = new[] { new Vector(-26, -26), new Vector(26, -26), new Vector(26, 26), new Vector(-26, 26) };
        for (var i = 0; i < 4; i++)
        {
            var bar = Bars[i];
            var move = new TranslateTransform(origins[i].X, origins[i].Y);
            bar.RenderTransform = move;
            bar.Opacity = 0;
            var begin = Ms(120 + (i * 55));
            bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(380)) { BeginTime = begin });
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(origins[i].X, 0, Ms(640)) { BeginTime = begin, EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(origins[i].Y, 0, Ms(640)) { BeginTime = begin, EasingFunction = ease });
        }
        var spin = new RotateTransform(-24, 60, 60);
        Mark.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-24, 0, Ms(900)) { BeginTime = Ms(120), EasingFunction = ease });

        // Stage 2b: light sweep across the mark, on the chime.
        var chime = StartupSound.ChimeAt.TotalMilliseconds;
        AnimateStop(_sweepA, -0.10, 1.00, chime);
        AnimateStop(_sweepB, -0.05, 1.05, chime);
        AnimateStop(_sweepC, 0.00, 1.10, chime);
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            BeginTime = Ms(chime - 120),
            KeyFrames =
            {
                new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(Ms(380)), new SineEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(Ms(1500)), new SineEase { EasingMode = EasingMode.EaseInOut }),
            },
        });

        // Stage 2c: the wordmark letters close in from wide tracking.
        var k = 0;
        foreach (TextBlock letter in Word.Children)
        {
            var begin = Ms(chime + 60 + (k++ * 34));
            letter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(420)) { BeginTime = begin });
            letter.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(9, 0, 9, 0), new Thickness(3, 0, 3, 0), Ms(900)) { BeginTime = begin, EasingFunction = ease });
        }

        // Stage 4: status line, only if real work is still going on.
        var statusTimer = new DispatcherTimer { Interval = Ms(1500) };
        statusTimer.Tick += (_, _) =>
        {
            statusTimer.Stop();
            if (!_workPending || Skipped) return;
            StatusPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(300)));
            StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, Ms(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        };
        statusTimer.Start();
        SkipHint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(400)) { BeginTime = Ms(700) });

        Finish(Ms(2500));
        return _intro.Task;
    }

    private static void AnimateStop(GradientStop stop, double from, double to, double beginMs) =>
        stop.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(760))
        {
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

    private void Finish(TimeSpan after)
    {
        var t = new DispatcherTimer { Interval = after };
        t.Tick += (_, _) => { t.Stop(); _intro?.TrySetResult(); };
        t.Start();
    }

    private void Skip()
    {
        if (_intro is null || _intro.Task.IsCompleted) return;
        Skipped = true;
        _sound?.Stop();
        _intro.TrySetResult();
    }

    /// <summary>Stage 5: fade everything to black.</summary>
    public Task FadeOutAsync(bool reducedMotion)
    {
        _padTimer.Stop();
        var done = new TaskCompletionSource();
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(reducedMotion || Skipped ? 160 : 420)) { EasingFunction = new SineEase() };
        fade.Completed += (_, _) => done.TrySetResult();
        Root.BeginAnimation(OpacityProperty, fade);
        return done.Task;
    }

    /// <summary>Shows why start-up failed. Completes when the user closes the screen.</summary>
    public Task ShowFailureAsync(string message, string hint)
    {
        _intro?.TrySetResult();
        _sound?.Stop();
        _failure = new TaskCompletionSource();
        Cursor = Cursors.Arrow;
        Logo.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        SkipHint.Visibility = Visibility.Collapsed;
        Glow.Visibility = Visibility.Collapsed;
        FailureText.Text = message;
        FailureHint.Text = hint;
        FailurePanel.Visibility = Visibility.Visible;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 1;
        CloseButton.Focus();
        return _failure.Task;
    }
}
