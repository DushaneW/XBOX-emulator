using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XboxPc.DesktopApp;
using XboxPc.DesktopApp.ViewModels;
using XboxPc.Domain;

namespace XboxPc.Tests.Ui;

/// <summary>
/// Real WPF smoke test: builds the actual window against a temp database, visits every page,
/// verifies no inert buttons, and drives the main user flows through the view-models.
/// One test method because WPF allows a single Application per process.
/// </summary>
public class UiSmokeTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    [Fact]
    public void Shell_pages_flows_and_controls()
    {
        Sta.Run(async () =>
        {
            using var dir = new TempDir();
            var app = new App();
            app.InitializeComponent();
            var dialogs = new RecordingDialogs();
            var pads = new ScriptedGamepadSource();
            using var host = await AppHost.CreateAsync(dir.Path, dialogs, pads);
            var main = new MainViewModel(host);
            var window = new MainWindow(main, host) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            app.MainWindow = window;
            window.Show();
            await main.InitializeAsync();
            await Sta.Pump();

            // 1. Every page renders and every button is wired to something.
            foreach (var page in main.Pages)
            {
                main.Navigate(page.Key);
                await Sta.Pump();
                Assert.Same(page, main.CurrentPage);
                var buttons = Descendants<ButtonBase>(window).ToList();
                Assert.NotEmpty(buttons);
                foreach (var b in buttons.Where(b => b.IsVisible && b is not ToggleButton and not RepeatButton && b.TemplatedParent is null))
                    Assert.True(b.Command is not null, $"Inert button '{b.Content}' on page {page.Title}.");
            }

            // 2. Runtime status labels on Home.
            main.Navigate("home");
            Assert.Contains(main.Home.Runtimes, r => r.Generation == ConsoleGeneration.SeriesXS.ToDisplay() && r.StateText == "Research Only");
            Assert.Single(main.Home.Runtimes, r => r.IsAvailable);

            // 3. Add bundled test program from Home and launch it from the Library editor.
            await ((AsyncRelayCommand)main.Home.AddSampleCommand).ExecuteAsync();
            await Sta.Pump();
            Assert.Same(main.Library, main.CurrentPage.ViewModel);
            var editor = main.Library.Editor!;
            Assert.Null(editor.LaunchBlockReason);
            await ((AsyncRelayCommand)editor.LaunchCommand).ExecuteAsync();
            Assert.StartsWith("Completed", editor.LaunchStatus, StringComparison.Ordinal);
            Assert.Contains("514229", editor.LaunchOutput, StringComparison.Ordinal); // fib(29), computed by the runtime
            Assert.Contains("fingerprint: sha256:", editor.LaunchDiagnostics, StringComparison.Ordinal);
            Assert.Empty(dialogs.Errors);

            // 4. A Series X|S entry can be stored but never launched.
            main.Library.BeginNew();
            var seriesEditor = main.Library.Editor!;
            seriesEditor.Name = "GTA VI";
            seriesEditor.Generation = seriesEditor.Generations.Single(g => g.Value == ConsoleGeneration.SeriesXS);
            await seriesEditor.SaveAsync();
            await Sta.Pump();
            var saved = main.Library.Editor!;
            Assert.Equal("GTA VI", saved.Title);
            Assert.Contains("Research Only", saved.LaunchBlockReason, StringComparison.Ordinal);
            Assert.False(saved.LaunchCommand.CanExecute(null));
            Assert.Equal("Not Tested", saved.CompatibilityText);

            // 5. Report form refuses an unsupported claim, accepts an evidence-backed local Researching note.
            var report = new ReportEditorViewModel(host, (await host.Library.GetAsync(saved.EntryId))!, null, null)
            {
                Outcome = new Choice<CompatibilityStatus>("Playable", CompatibilityStatus.Playable),
                RuntimeId = "xpc.series-xs.research",
                RuntimeVersion = "0.0.0-research",
            };
            Assert.False(await report.SaveAsync());
            Assert.Contains("reviewer", report.Errors, StringComparison.OrdinalIgnoreCase);
            report.Outcome = new Choice<CompatibilityStatus>("Researching", CompatibilityStatus.Researching);
            Assert.True(await report.SaveAsync());
            Assert.Equal(CompatibilityStatus.Researching, (await host.Compatibility.GetSummaryAsync(saved.EntryId)).Status);

            // 6. Preferences apply to the app's own rendering and persist.
            main.Settings.Preset = main.Settings.Presets.Single(p => p.Value == RenderQualityPreset.Quality);
            main.Settings.UiScale = 1.25;
            await main.Settings.SavePreferencesAsync();
            Assert.NotNull(System.Windows.Application.Current.Resources["CardShadow"]);
            main.Settings.Preset = main.Settings.Presets.Single(p => p.Value == RenderQualityPreset.Performance);
            await main.Settings.SavePreferencesAsync();
            Assert.Null(System.Windows.Application.Current.Resources["CardShadow"]);
            Assert.Equal(1.25, host.Profiles.Active!.Preferences.UiScale);

            // 7. Invalid mapping is shown, not saved; valid remap is applied to the controller service.
            main.Settings.Mappings.Single(m => m.Action == UiAction.Accept).Pad = SettingsViewModel.None;
            main.Settings.Mappings.Single(m => m.Action == UiAction.Accept).Key = SettingsViewModel.None;
            await main.Settings.SaveInputAsync();
            Assert.True(main.Settings.HasInputErrors);
            main.Settings.Mappings.Single(m => m.Action == UiAction.Accept).Pad = "Pad.X";
            main.Settings.Mappings.Single(m => m.Action == UiAction.Menu).Pad = "Pad.Start";
            await main.Settings.SaveInputAsync();
            Assert.False(main.Settings.HasInputErrors, main.Settings.InputErrors);
            Assert.Equal(UiAction.Accept, host.Controllers.Map.Resolve("Pad.X"));

            // 8. Controller connect/disconnect updates the shell status.
            pads.Slots[0] = new XboxPc.Input.GamepadReading(1, XboxPc.Input.PadButtons.None, 0, 0, 0, 0, 0, 0);
            host.Controllers.Poll(TimeSpan.FromSeconds(1));
            Assert.Equal("1 controller connected", main.ControllerStatus);
            pads.Slots[0] = null;
            host.Controllers.Poll(TimeSpan.FromSeconds(2));
            Assert.Equal("No controller", main.ControllerStatus);

            // 9. Input routing: Accept on a focused nav item navigates; NextPage cycles pages.
            main.Navigate("home");
            await Sta.Pump();
            var lastNav = Descendants<RadioButton>(window).Single(r => (string)r.CommandParameter == "settings");
            lastNav.Focus();
            Assert.True(InputRouter.Apply(window, UiAction.Accept, main.NavigateRelative));
            Assert.Equal("settings", main.CurrentPage.Key);
            InputRouter.Apply(window, UiAction.NextPage, main.NavigateRelative);
            Assert.Equal("home", main.CurrentPage.Key);

            // 10. Performance page shows explicit labels, and measured values only once frames exist.
            main.Performance.Tick();
            Assert.Contains(main.Performance.Metrics, m => m.Name.StartsWith("Generated", StringComparison.Ordinal) && m.Value == "Not applicable");
            Assert.Contains(main.Performance.Metrics, m => m.Name == "Input latency" && m.Value == "Not measured");
            Assert.Contains(main.Performance.Metrics, m => m.Name.StartsWith("Native rendered FPS", StringComparison.Ordinal) && m.Value == "-");

            window.Close();
            app.Shutdown();
        });
    }
}
