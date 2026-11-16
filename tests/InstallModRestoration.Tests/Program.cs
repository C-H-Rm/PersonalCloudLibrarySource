using PersonalCloudLibrarySource;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
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
        await RunAsync("restore has no ordinary UI confirmation; recovery decline is UI-pumped outside worker and lock", ConfirmationBoundaries);
        await RunAsync("successful factory restore followed by normal retry uses existing deletion baseline", RestoreThenRetry);
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

    private static async Task ConfirmationBoundaries()
    {
        using (var f = new Fixture())
        {
            var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings());
            var result = await callback(CancellationToken.None, null, false);
            Check(result.Success, "ordinary install restoration failed: " + result.Message);
            Check(f.Source.ConfirmCalls == 0, "ordinary restore showed UI confirmation");
        }
        using (var f = new Fixture())
        {
            var callback = f.Source.Create(f.Game, f.Item(), f.Manifest(), f.Settings());
            Directory.CreateDirectory(f.State);
            File.WriteAllText(Path.Combine(f.State, "paused"), "paused after uninstall");
            f.Source.ConfirmHandler = message =>
            {
                Check(Environment.CurrentManagedThreadId == f.UiThread, "recovery prompt did not run on pumped UI context");
                Check(f.Source.Gate.CurrentCount == 1, "recovery prompt ran while operation gate held");
                Check(message.Contains("Explicitly relink"), "unexpected recovery question");
                return false;
            };
            var result = await callback(CancellationToken.None, null, true);
            Check(f.Source.ConfirmCalls == 1 && result.Cancelled && result.RequiresRelink, "declined recovery did not return cancelled/relink result");
            Check(File.Exists(Path.Combine(f.State, "paused")), "declined recovery altered paused state");
            Check(Directory.GetFiles(f.Cloud, "*", SearchOption.AllDirectories).Length == 0, "declined recovery altered cloud files");
        }
    }

    private static async Task RestoreThenRetry()
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
        Check(File.Exists(Path.Combine(f.Cloud, "from-install.txt")), "installed mods did not upload");
        Check(File.Exists(Path.Combine(f.State, "ready")), "restore did not establish ready baseline");
        var secondPhases = new List<string>();
        var second = await callback(CancellationToken.None, p => secondPhases.Add(p.Phase), false);
        Check(second.Success, "same callback normal retry failed: " + second.Message);
        Check(!secondPhases.Contains("check"), "normal retry repeated initial comparison/baseline setup");
        File.Delete(Path.Combine(f.Local, "from-install.txt"));
        var deletion = await callback(CancellationToken.None, null, false);
        Check(deletion.Success && !File.Exists(Path.Combine(f.Cloud, "from-install.txt")), "normal retry did not apply established deletion baseline");
        Check(Directory.GetDirectories(f.Root, "state.archive-*").Length == 0, "normal retry archived/reinitialized baseline");
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
            return new ModSyncRequest { GameId = item.Id, LocalFolder = Path.GetFullPath(item.ModPath), RemoteFolder = ":local:" + Harness.Cloud,
                StateDirectory = Harness.State, LaunchFile = Path.Combine(Harness.Root, "game", item.LaunchFile),
                RcloneExecutable = current.RcloneExecutablePath, TimeoutSeconds = current.ModSyncTimeoutSeconds };
        }
        private string GetProviderType(PersonalCloudLibrarySourceSettings value) => value.SourceProviderType;
        private PersonalCloudLibraryManifest ParseManifest(string json) => Harness.Manifest(json == "changed" ? Harness.Item("different-mods") : Harness.Item());
        private string LoadManifestJson(PersonalCloudLibrarySourceSettings value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (LiveManifestError != null) throw LiveManifestError;
            return LiveManifest ?? "live";
        }
        private bool ConfirmModSync(string message) { ConfirmCalls++; return ConfirmHandler(message); }
    }
}
namespace Playnite.SDK.Models { public class Game { public string GameId { get; set; } public bool IsRunning { get; set; } } }
