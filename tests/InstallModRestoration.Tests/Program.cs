using PersonalCloudLibrarySource;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Composed native-rclone harness: the production factory, interaction, service and
// process runner are real; SDK/plugin/settings/manifest building below are doubles.
// This does not simulate or prove rendered Playnite UI or real Nextcloud behavior.
internal static class Program
{
    private static int passed;
    private static int failed;

    private static async Task<int> Main()
    {
        Run("factory eligibility excludes disabled, blank-path, and local-provider imports", Eligibility);
        Run("invalid imported optional mapping is deferred until restore", InvalidMappingDeferred);
        Run("fresh live mapping mismatch and offline manifest refuse before writes", FreshMappingRequired);
        Run("callback observes replacement settings instance", SettingsReplacementObserved);
        await RunAsync("gate wait cancellation neither over-releases nor leaks the operation gate", GateCancellation);
        await RunAsync("installation only downloads missing cloud mods, never compares or uploads", DownloadOnly);
        await RunAsync("inventory failure cancels the actual adapter's staged rclone transfer", InventoryFailureCancelsCopy);
        await RunAsync("two-way initialization starts only when background queue is explicitly started", DownloadThenBackground);
        await RunAsync("launch defers pending background work until explicit post-game resume", LaunchDefersBackground);
        await RunAsync("actual background adapter observes native work and clears status without setting installing", BackgroundAdapter);
        await RunAsync("notification rendering failure does not orphan background native work", BackgroundNotificationFailure);
        await RunAsync("background notification cancellation reaches the actual factory manifest token", BackgroundNotificationCancellation);
        await RunAsync("actual launch barrier cancels and drains background work without UI deadlock", BackgroundLaunchBarrier);
        await RunAsync("failed background work retains a launch barrier until explicit review", FailedBackgroundLaunchBarrier);
        await RunAsync("failed mod download cannot trigger foreground initialization on launch", FailedDownloadLaunchBarrier);
        Console.WriteLine($"InstallModRestoration composed tests: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void Eligibility()
    {
        using var f = new Fixture();
        var settings = f.Settings();
        Check(f.Source.Create(f.Game, f.Item(), f.Manifest(), settings) != null, "eligible mapping did not create callback");
        settings.Enabled = false;
        Check(f.Source.Create(f.Game, f.Item(), f.Manifest(), settings) == null, "disabled plugin created callback");
        settings.Enabled = true;
        settings.EnableModSync = false;
        Check(f.Source.Create(f.Game, f.Item(), f.Manifest(), settings) == null, "disabled feature created callback");
        settings.EnableModSync = true;
        Check(f.Source.Create(f.Game, f.Item(""), f.Manifest(), settings) == null, "blank mod path created callback");
        var absentPath = f.Item(); absentPath.ModPath = null;
        Check(f.Source.Create(f.Game, absentPath, f.Manifest(), settings) == null, "null mod path created callback");
        Check(f.Source.Create(f.Game, f.Item(" \t"), f.Manifest(), settings) == null, "whitespace mod path created callback");
        settings.SourceProviderType = PersonalCloudLibrarySourceSettings.LocalFileProviderType;
        Check(f.Source.Create(f.Game, f.Item(), f.Manifest(), settings) == null, "local provider created callback");
    }

    private static void InvalidMappingDeferred()
    {
        using var f = new Fixture();
        var settings = f.Settings();
        f.Source.BuildThrows = true;
        var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), settings);
        Check(callback != null, "factory threw/rejected invalid optional mapping");
        Exception resultError = null;
        try { callback(CancellationToken.None, null, false).GetAwaiter().GetResult(); } catch (InvalidOperationException ex) { resultError = ex; }
        Check(resultError != null && resultError.Message.Contains("Invalid optional mod configuration"), "deferred mapping error was not reported");
        Check(!Directory.Exists(f.State) && !Directory.GetFiles(f.Cloud, "*", SearchOption.AllDirectories).Any(), "deferred invalid mapping wrote state/cloud data");
    }

    private static void FreshMappingRequired()
    {
        using var f = new Fixture();
        var settings = f.Settings();
        var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), settings);
        f.Source.LiveManifest = "changed";
        var changedMessage = "";
        try { callback(CancellationToken.None, null, false).GetAwaiter().GetResult(); }
        catch (Exception ex) { changedMessage = ex.Message; }
        Check(changedMessage.Contains("differs"), "fresh changed mapping was accepted: " + changedMessage);
        Check(!Directory.Exists(f.State) && !Directory.GetFiles(f.Cloud, "*", SearchOption.AllDirectories).Any(), "changed mapping wrote state/cloud data");

        f.Source.LiveManifestError = new IOException("manifest offline");
        var offlineMessage = "";
        try { callback(CancellationToken.None, null, false).GetAwaiter().GetResult(); }
        catch (Exception ex) { offlineMessage = ex.Message; }
        Check(offlineMessage.Contains("manifest offline"), "offline manifest was accepted: " + offlineMessage);
        Check(!Directory.Exists(f.State) && !Directory.GetFiles(f.Cloud, "*", SearchOption.AllDirectories).Any(), "offline lookup wrote state/cloud data");
    }

    private static void SettingsReplacementObserved()
    {
        using var f = new Fixture();
        var imported = f.Settings();
        var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), imported);
        var replacement = f.Settings();
        replacement.EnableModSync = false;
        f.Source.SettingsView.Settings = replacement;
        var result = callback(CancellationToken.None, null, false).GetAwaiter().GetResult();
        Check(result == null, "callback did not observe new disabled settings instance");
        Check(!Directory.Exists(f.State), "disabled current settings reached service");
    }

    private static async Task GateCancellation()
    {
        using var f = new Fixture();
        var settings = f.Settings();
        var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), settings);
        var gate = f.Source.Gate;
        gate.Wait();
        using var cts = new CancellationTokenSource();
        var pending = callback(cts.Token, null, false);
        await Task.Delay(100);
        cts.Cancel();
        var cancelled = false;
        try { await pending; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "waiting callback did not cancel");
        Check(!gate.Wait(0), "cancelled waiter released another owner's lock");
        gate.Release();
        Check(gate.Wait(0), "gate leaked after the owning operation released it");
        gate.Release();
        var retry = await callback(CancellationToken.None, null, false);
        Check(retry != null, "callback could not retry after cancelled wait");
    }

    private static async Task DownloadOnly()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Local, "existing.txt"), "local");
        File.WriteAllText(Path.Combine(f.Cloud, "existing.txt"), "cloud");
        File.WriteAllText(Path.Combine(f.Cloud, "new.txt"), "download");
        File.WriteAllText(Path.Combine(f.Local, "local-only.txt"), "keep local");
        var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings());
        var phases = new List<string>();
        var result = await callback(CancellationToken.None, p => phases.Add(p.Phase), true);
        Check(result.Success, result.Message);
        Check(File.ReadAllText(Path.Combine(f.Local, "new.txt")) == "download", "cloud mod not downloaded");
        Check(File.ReadAllText(Path.Combine(f.Local, "existing.txt")) == "local", "existing local file overwritten");
        Check(!File.Exists(Path.Combine(f.Cloud, "local-only.txt")) && File.ReadAllText(Path.Combine(f.Cloud, "existing.txt")) == "cloud", "download wrote cloud data");
        Check(!File.Exists(Path.Combine(f.State, "ready")) && !File.Exists(Path.Combine(f.State, "pair.txt")), "download initialized bidirectional state");
        Check(!phases.Contains("check") && !phases.Contains("bisync") && f.Source.ConfirmCalls == 0, "download compared contents or asked sync consent");
    }

    private static async Task InventoryFailureCancelsCopy()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new Fixture();
        using (var payload = File.Create(Path.Combine(f.Cloud, "slow.bin"))) payload.SetLength(4 * 1024 * 1024);
        var settings = f.Settings();
        var real = settings.RcloneExecutablePath;
        var started = Path.Combine(f.Root, "copy-started");
        var wrapper = Path.Combine(f.Root, "fail-inventory-rclone.sh");
        File.WriteAllText(wrapper, "#!/bin/bash\nif [ \"$1\" = lsjson ]; then\n" +
            "  for ((i=0; i<500; i++)); do [ -f \"" + started + "\" ] && break; sleep 0.01; done\n" +
            "  sleep 0.3\n  echo 'fixture inventory failure' >&2\n  exit 2\nfi\n" +
            "if [ \"$1\" = copy ]; then touch \"" + started + "\"; exec \"" + real + "\" \"$@\" --bwlimit=64k; fi\n" +
            "exec \"" + real + "\" \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        settings.RcloneExecutablePath = wrapper;
        var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), settings);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var timer = Stopwatch.StartNew();
        var result = await callback(deadline.Token, null, false);
        Check(File.Exists(started), "fixture did not start the actual native copy");
        Check(timer.Elapsed < TimeSpan.FromSeconds(4) && !deadline.IsCancellationRequested,
            "failed inventory did not cancel/drain the staged copier through its linked operation token");
        Check(!result.Success && !result.Cancelled, "inventory failure was not reported as a failure");
        Check(!File.Exists(Path.Combine(f.Local, "slow.bin")) && !f.Source.Queue.Contains(f.Game.Id),
            "failed inventory committed a mod or queued two-way initialization");
    }

    private static async Task DownloadThenBackground()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Cloud);
        File.WriteAllText(Path.Combine(f.Cloud, "from-cloud.txt"), "cloud");
        File.WriteAllText(Path.Combine(f.Local, "from-install.txt"), "installed");
        var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings());
        var phases = new List<string>();
        var first = await callback(CancellationToken.None, p => phases.Add(p.Phase), false);
        Check(first.Success, "restore failed: " + first.Message);
        Check(File.ReadAllText(Path.Combine(f.Local, "from-cloud.txt")) == "cloud", "cloud restore missing");
        Check(!File.Exists(Path.Combine(f.Cloud, "from-install.txt")), "local mods uploaded on install critical path");
        Check(!File.Exists(Path.Combine(f.State, "ready")), "download initialized sync instead of queuing it");
        Check(f.Source.Queue.Contains(f.Game.Id), "successful download did not register pending background sync");
        var second = await f.Source.Queue.Start(f.Game.Id);
        Check(second.Success && File.Exists(Path.Combine(f.State, "ready")), "explicit background initialization failed: " + second.Message);
        Check(File.Exists(Path.Combine(f.Cloud, "from-install.txt")), "background sync did not merge local-only mods");
        File.Delete(Path.Combine(f.Local, "from-install.txt"));
        var deletion = f.Source.SyncExisting();
        Check(deletion.Success && !File.Exists(Path.Combine(f.Cloud, "from-install.txt")), "normal retry did not apply established deletion baseline");
        Check(!f.Source.Queue.Contains(f.Game.Id), "finished background work left the game queued");
    }

    private static async Task LaunchDefersBackground()
    {
        using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Cloud, "mod.txt"), "cloud");
        var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings());
        Check((await callback(CancellationToken.None, null, false)).Success, "download failed");
        Check(f.Source.Queue.DeferForLaunch(f.Game.Id) == null, "pending background work unexpectedly started");
        Check(f.Source.Queue.Start(f.Game.Id) == null && !File.Exists(Path.Combine(f.State, "ready")), "game launch started foreground sync");
        Check((await f.Source.Queue.Start(f.Game.Id, true)).Success, "post-game background resume failed");
    }

    private static async Task BackgroundAdapter()
    {
        using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Cloud, "mod.txt"), "cloud");
        Check((await f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings())(CancellationToken.None, null, false)).Success, "download failed");
        f.Source.BeginBackground();
        Check(!f.Game.IsInstalling && f.Game.IsInstalled, "background work changed installation state");
        await WaitFor(() => !f.Source.Queue.Contains(f.Game.Id) && InstallProgressState.Shared.Get(f.Game.Id)?.Active != true);
        Check(File.Exists(Path.Combine(f.State, "ready")), "background adapter did not initialize real sync state");
        Check(f.Source.Api.Notifications.Count == 0 && f.Source.Errors.Count == 0, "completed work left notification or false failure");
        Check(!f.Source.DeferLaunch(out _), "successful background initialization left a permanent launch deferral");
    }

    private static async Task BackgroundNotificationFailure()
    {
        using var f = new Fixture();
        Check((await f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings())(CancellationToken.None, null, false)).Success, "download failed");
        f.Source.Api.Notifications.ThrowAdd = true;
        f.Source.BeginBackground();
        await WaitFor(() => !f.Source.Queue.Contains(f.Game.Id) && InstallProgressState.Shared.Get(f.Game.Id)?.Active != true);
        Check(File.Exists(Path.Combine(f.State, "ready")), "UI notification failure stopped or orphaned background work");
    }

    private static async Task BackgroundNotificationCancellation()
    {
        using var f = new Fixture();
        Check((await f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings())(CancellationToken.None, null, false)).Success, "download failed");
        f.Source.DelayLiveManifest = true;
        f.Source.BeginBackground();
        await WaitFor(() => f.Source.DelayedManifestEntered.IsSet && f.Source.Api.Notifications.Count == 1);
        f.Source.Api.Notifications.Last.Action();
        await WaitFor(() => !f.Source.Queue.Contains(f.Game.Id) && InstallProgressState.Shared.Get(f.Game.Id)?.Active != true);
        Check(InstallProgressState.Shared.Get(f.Game.Id)?.Phase == "Cancelled" && !f.Game.IsInstalling, "cancel did not reach background work or left busy state");
    }

    private static async Task BackgroundLaunchBarrier()
    {
        using var f = new Fixture();
        Check((await f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings())(CancellationToken.None, null, false)).Success, "download failed");
        f.Source.DelayLiveManifest = true; f.Source.BeginBackground();
        await WaitFor(() => f.Source.DelayedManifestEntered.IsSet);
        Check(f.Source.DeferLaunch(out var stopped) && stopped, "launch cancellation did not drain background worker");
        Check(f.Source.Queue.Contains(f.Game.Id) && f.Source.Queue.Start(f.Game.Id) == null, "launch lost deferral or restarted foreground initialization");
        f.Source.DelayLiveManifest = false;
        // Resume before the cancelled observer's UI continuation has cleaned up.
        // The adapter must yield rather than lose this game-exit request to a busy session.
        f.Source.BeginBackground(true);
        await WaitFor(() => !f.Source.Queue.Contains(f.Game.Id) && InstallProgressState.Shared.Get(f.Game.Id)?.Active != true);
        Check(File.Exists(Path.Combine(f.State, "ready")), "post-game resume failed after cancelled metadata lookup");
    }

    private static async Task FailedBackgroundLaunchBarrier()
    {
        using var f = new Fixture();
        Check((await f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings())(CancellationToken.None, null, false)).Success, "download failed");
        f.Source.LiveManifestError = new IOException("background lookup failed before pair initialization");
        f.Source.BeginBackground();
        await WaitFor(() => !f.Source.Queue.Contains(f.Game.Id) && InstallProgressState.Shared.Get(f.Game.Id)?.Active != true);
        Check(!File.Exists(Path.Combine(f.State, "pair.txt")), "fixture unexpectedly initialized a pair");
        Check(f.Source.DeferLaunch(out var stopped) && stopped, "failed background work fell back to foreground initial sync on launch");
    }

    private static async Task FailedDownloadLaunchBarrier()
    {
        using var f = new Fixture();
        var download = f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings());
        f.Source.LiveManifestError = new IOException("mod download lookup failed");
        try { await download(CancellationToken.None, null, false); }
        catch (IOException) { }
        Check(!f.Source.Queue.Contains(f.Game.Id) && !File.Exists(Path.Combine(f.State, "pair.txt")), "failed download queued or initialized two-way sync");
        Check(f.Source.DeferLaunch(out var stopped) && stopped, "failed download retried expensive setup in foreground on launch");
    }

    private static async Task WaitFor(Func<bool> done)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!done()) { if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Background adapter did not settle."); await Task.Delay(10); }
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static Task RunAsync(string name, Func<Task> test)
    {
        try { PumpingContext.Run(test); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        return Task.CompletedTask;
    }
    private sealed class PumpingContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Tuple<SendOrPostCallback, object>> queue = new System.Collections.Concurrent.BlockingCollection<Tuple<SendOrPostCallback, object>>();
        public override void Post(SendOrPostCallback callback, object state) => queue.Add(Tuple.Create(callback, state));
        public static void Run(Func<Task> action)
        {
            var previous = Current;
            var context = new PumpingContext();
            SetSynchronizationContext(context);
            try
            {
                var task = action();
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("UI-pumped integration did not complete.");
                    if (context.queue.TryTake(out var work, 50)) work.Item1(work.Item2);
                }
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "install-restore-tests-" + Guid.NewGuid().ToString("N"));
        public string Local => Path.Combine(Root, "game", "mods");
        public string Cloud => Path.Combine(Root, "cloud");
        public string State => Path.Combine(Root, "plugin-data", "mod-sync", "game-key");
        public readonly int UiThread = Environment.CurrentManagedThreadId;
        public TestGame Game { get; } = new TestGame { GameId = "game", IsRunning = false };
        public PersonalCloudLibrarySource.PersonalCloudLibrarySource Source { get; }
        private readonly string oldConfig;
        public Fixture()
        {
            Directory.CreateDirectory(Local);
            Directory.CreateDirectory(Cloud);
            Directory.CreateDirectory(Path.Combine(Root, "game"));
            File.WriteAllText(Path.Combine(Root, "game", "launch.exe"), "fixture");
            var config = Path.Combine(Root, "rclone.conf");
            File.WriteAllText(config, "[fixture]\ntype = local\n\n");
            oldConfig = Environment.GetEnvironmentVariable("RCLONE_CONFIG");
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", config);
            Source = new PersonalCloudLibrarySource.PersonalCloudLibrarySource(this);
        }
        public PersonalCloudLibrarySourceSettings Settings() => new PersonalCloudLibrarySourceSettings
        {
            Enabled = true, EnableModSync = true, SourceProviderType = PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType,
            RcloneRemoteName = "fixture", RcloneContentRoot = "", RcloneExecutablePath = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone",
            ModSyncTimeoutSeconds = 120
        };
        public PersonalCloudLibraryItem Item(string modPath = null) => new PersonalCloudLibraryItem
        {
            Id = "game", Title = "Fixture", SourcePath = "games/game.zip", InstallDirectory = Path.Combine(Root, "game"),
            LaunchFile = "launch.exe", ModPath = modPath ?? Local
        };
        public PersonalCloudLibraryManifest Manifest(PersonalCloudLibraryItem item = null) => new PersonalCloudLibraryManifest { Items = new List<PersonalCloudLibraryItem> { item ?? Item() } };
        public string Json(PersonalCloudLibraryItem item) => "live";
        public void Dispose()
        {
            Source.Queue.CancelAll();
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", oldConfig);
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    public sealed class TestGame : Game { }


}

namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibrarySourceSettings
    {
        public const string RcloneRemoteProviderType = "RcloneRemote";
        public const string LocalFileProviderType = "LocalFile";
        public bool Enabled { get; set; }
        public bool EnableModSync { get; set; }
        public string SourceProviderType { get; set; }
        public string RcloneRemoteName { get; set; }
        public string RcloneContentRoot { get; set; }
        public string RcloneExecutablePath { get; set; }
        public int ModSyncTimeoutSeconds { get; set; }
        public string RcloneManifestPath { get; set; }
        public int RcloneTimeoutSeconds { get; set; } = 30;
        public int RcloneTransferInactivityTimeoutSeconds { get; set; } = 60;
    }
    public sealed class PersonalCloudLibrarySourceSettingsViewModel { public PersonalCloudLibrarySourceSettings Settings { get; set; } }
    public sealed class PersonalCloudLibraryManifest { public List<PersonalCloudLibraryItem> Items { get; set; } = new List<PersonalCloudLibraryItem>(); }
    public sealed class PersonalCloudLibraryItem { public string Id { get; set; } public string Title { get; set; } public string SourcePath { get; set; } public string InstallDirectory { get; set; } public string LaunchFile { get; set; } public string ModPath { get; set; } }
    public partial class PersonalCloudLibrarySource
    {
        internal readonly Program.Fixture Harness;
        private PersonalCloudLibrarySourceSettingsViewModel settings;
        private readonly SemaphoreSlim modSyncGate = new SemaphoreSlim(1, 1);
        private readonly ModSyncService modSyncService = new ModSyncService();
        private readonly RcloneFileCopier rcloneFileCopier = new RcloneFileCopier();
        public PostInstallModSyncQueue Queue => postInstallModSync;
        public void BeginBackground(bool resume = false) => StartInstallBackgroundModSync(Harness.Game, resume);
        public bool DeferLaunch(out bool stopped) => DeferInstallBackgroundModsForLaunch(Harness.Game, out stopped);
        private readonly TestApi playniteApi = new TestApi();
        public TestApi Api => playniteApi;
        private static readonly TestLogger logger = new TestLogger();
        public readonly List<ModSyncResult> Errors = new List<ModSyncResult>();
        private void NotifyModSync(Game game, ModSyncResult result) => Errors.Add(result);
        private ModSyncResult RunModSync(Game game, bool stopped, bool manual, bool relink) => new ModSyncResult { Success = true };
        public bool DelayLiveManifest;
        public readonly ManualResetEventSlim DelayedManifestEntered = new ManualResetEventSlim();
        public ModSyncResult SyncExisting() => modSyncService.Synchronize(BuildModSyncRequest(Harness.Item(), Harness.Manifest(), SettingsView.Settings), _ => false);
        public SemaphoreSlim Gate => modSyncGate;
        public readonly PersonalCloudLibrarySourceSettingsViewModel SettingsView = new PersonalCloudLibrarySourceSettingsViewModel();
        public int ConfirmCalls;
        public bool BuildThrows;
        public string LiveManifest;
        public Exception LiveManifestError;
        public Func<string, bool> ConfirmHandler = _ => false;
        internal PersonalCloudLibrarySource(Program.Fixture fixture) { Harness = fixture; settings = SettingsView; }
        public Func<CancellationToken, Action<ModSyncProgress>, bool, Task<ModSyncResult>> Create(Game game, PersonalCloudLibraryItem item,
            PersonalCloudLibraryManifest manifest, PersonalCloudLibrarySourceSettings initial)
        {
            if (SettingsView.Settings == null) SettingsView.Settings = initial;
            return CreateInstallModRestore(game, item, manifest, initial);
        }
        private ModSyncRequest BuildModSyncRequest(PersonalCloudLibraryItem item, PersonalCloudLibraryManifest manifest, PersonalCloudLibrarySourceSettings current)
        {
            if (BuildThrows) throw new InvalidOperationException("bad optional map");
            return new ModSyncRequest { GameId = item.Id, LocalFolder = Path.GetFullPath(item.ModPath), RemoteFolder = current.RcloneRemoteName + ":" + Harness.Cloud,
                StateDirectory = Harness.State, LaunchFile = Path.Combine(Harness.Root, "game", item.LaunchFile),
                RcloneExecutable = current.RcloneExecutablePath, TimeoutSeconds = current.ModSyncTimeoutSeconds };
        }
        private string GetProviderType(PersonalCloudLibrarySourceSettings value) => value.SourceProviderType;
        private PersonalCloudLibraryManifest ParseManifest(string json) => Harness.Manifest(json == "changed" ? Harness.Item("different-mods") : Harness.Item());
        private string LoadManifestJson(PersonalCloudLibrarySourceSettings value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (DelayLiveManifest) { DelayedManifestEntered.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            if (LiveManifestError != null) throw LiveManifestError;
            return LiveManifest ?? "live";
        }
        private bool ConfirmModSync(string message) { ConfirmCalls++; return ConfirmHandler(message); }
    }
}
namespace Playnite.SDK.Models { public class Game { public Guid Id { get; } = Guid.NewGuid(); public string GameId { get; set; } public string Name = "fixture"; public bool IsRunning { get; set; } public bool IsInstalled = true, IsInstalling; } }
