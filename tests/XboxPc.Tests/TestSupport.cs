using System.Windows.Threading;
using XboxPc.Application;
using XboxPc.DesktopApp;
using XboxPc.Domain;
using XboxPc.Infrastructure;
using XboxPc.Input;
using XboxPc.Runtime.Abstractions;

namespace XboxPc.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xboxpc-tests", Guid.NewGuid().ToString("N"));

    public string File(string name, string content)
    {
        var p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string Bytes(string name, byte[] content)
    {
        var p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class ListLog : IAppLog
{
    public List<AppLogEntry> Entries { get; } = [];

    public void Write(AppLogLevel level, string category, string message, Exception? exception = null) =>
        Entries.Add(new AppLogEntry(DateTimeOffset.UtcNow, level, category, message, exception?.Message));
}

public static class Samples
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "samples", name);

    public static IReadOnlyList<long> Fibonacci(int count)
    {
        var list = new List<long>();
        long a = 0, b = 1;
        for (var i = 0; i < count; i++) { list.Add(a); (a, b) = (b, a + b); }
        return list;
    }

    public static IReadOnlyList<long> PrimesBelow(int limit)
    {
        var sieve = new bool[limit];
        var list = new List<long>();
        for (var i = 2; i < limit; i++)
        {
            if (sieve[i]) continue;
            list.Add(i);
            for (var j = i * i; j < limit; j += i) sieve[j] = true;
        }
        return list;
    }
}

public static class TestDb
{
    public static async Task<SqliteDatabase> CreateAsync(TempDir dir)
    {
        var db = new SqliteDatabase(System.IO.Path.Combine(dir.Path, "test.db"));
        await db.MigrateAsync(NullAppLog.Instance);
        return db;
    }

    public static async Task<(LibraryService Library, SqliteDatabase Db)> LibraryAsync(TempDir dir)
    {
        var db = await CreateAsync(dir);
        return (new LibraryService(new SqliteLibraryRepository(db), NullAppLog.Instance), db);
    }
}

/// <summary>
/// TEST ONLY. A fake runtime used to exercise LaunchCoordinator failure paths. Never registered in the app.
/// </summary>
public sealed class FakeRuntime(ImplementationState state = ImplementationState.Available, ConsoleGeneration generation = ConsoleGeneration.Other) : IRuntimeAdapter
{
    public enum Behavior { CompleteImmediately, Hang, Fault, RejectContent }

    public Behavior Mode { get; set; } = Behavior.CompleteImmediately;
    public int StopCalls { get; private set; }
    public Version Contract { get; set; } = RuntimeContract.Current;

    public RuntimeDescriptor Descriptor => new()
    {
        RuntimeId = "test.fake-runtime",
        Generation = generation,
        DisplayName = "FakeRuntime (TEST ONLY)",
        Version = "0.0.0-test",
        State = state,
        Capabilities = new HashSet<string> { RuntimeCapabilities.SessionRun, RuntimeCapabilities.ContentValidate },
        LicenseNotice = "Test fake.",
        Summary = "Test fake. Not a runtime.",
        ContractVersion = Contract,
    };

    public Task<ContentValidationResult> ValidateContentAsync(ContentDescriptor content, CancellationToken cancellationToken) =>
        Task.FromResult(Mode == Behavior.RejectContent ? ContentValidationResult.Invalid("rejected by fake") : new ContentValidationResult(true, "ok", "sha256:fake"));

    public Task<IRuntimeSession> PrepareSessionAsync(ContentDescriptor content, SessionOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<IRuntimeSession>(new Session(this));

    private sealed class Session(FakeRuntime owner) : IRuntimeSession
    {
        private readonly TaskCompletionSource<SessionResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid SessionId { get; } = Guid.NewGuid();
        public SessionState State { get; private set; } = SessionState.Prepared;
        public Task<SessionResult> Completion => _tcs.Task;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            State = SessionState.Running;
            switch (owner.Mode)
            {
                case Behavior.CompleteImmediately: Finish(SessionState.Completed, "fake completed"); break;
                case Behavior.Fault: Finish(SessionState.Faulted, "fake fault"); break;
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            owner.StopCalls++;
            Finish(SessionState.Stopped, "fake stopped");
            return Task.CompletedTask;
        }

        private void Finish(SessionState state, string summary)
        {
            State = state;
            _tcs.TrySetResult(new SessionResult(state, summary, [], TimeSpan.Zero));
        }

        public RuntimeDiagnostics GetDiagnostics() => new(State, [], new Dictionary<string, string>());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>TEST ONLY controller source with scripted readings.</summary>
public sealed class ScriptedGamepadSource : IGamepadSource
{
    public GamepadReading?[] Slots { get; } = new GamepadReading?[4];
    public string ApiName => "Scripted (test)";
    public bool IsApiAvailable => true;
    public int SlotCount => 4;
    public GamepadReading? Read(int slot) => Slots[slot];
}

public sealed class RecordingDialogs : IDialogService
{
    public List<string> Errors { get; } = [];
    public Queue<string?> FilesToPick { get; } = new();
    public bool ConfirmResult { get; set; } = true;

    public string? PickFile(string title, string filter) => FilesToPick.Count > 0 ? FilesToPick.Dequeue() : null;
    public string? PickSaveFile(string title, string filter, string defaultName) => FilesToPick.Count > 0 ? FilesToPick.Dequeue() : null;
    public bool Confirm(string title, string message) => ConfirmResult;
    public void ShowError(string title, string message) => Errors.Add($"{title}: {message}");
    public void ShowInfo(string title, string message) { }
    public void OpenFolder(string path) { }
    public void OpenFile(string path) { }
}

public static class Sta
{
    /// <summary>Runs an async test body on an STA thread with a WPF dispatcher.</summary>
    public static void Run(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = body();
            task.ContinueWith(t =>
            {
                failure = t.Exception?.GetBaseException();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(2))) throw new TimeoutException("STA test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Lets queued dispatcher work (bindings, layout) run.</summary>
    public static async Task Pump()
    {
        for (var i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
