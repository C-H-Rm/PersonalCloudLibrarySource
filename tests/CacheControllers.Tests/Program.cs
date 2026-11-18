using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PersonalCloudLibrarySource;

// Exercise the real controller with lightweight host doubles; not a Playnite UI test.
class Program
{
    static int Main(string[] args)
    {
        var context = new UiContext();
        SynchronizationContext.SetSynchronizationContext(context);
        int passed = 0, failed = 0;
        void Test(string name, Action action)
        {
            if (args.Length > 0 && !name.StartsWith(args[0], StringComparison.Ordinal)) return;
            try { action(); Console.WriteLine("PASS " + name); passed++; }
            catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
        }
        Test("deletion runs off UI; completion returns to UI after deletion", () =>
        {
            using var f = new Fixture();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int worker = 0, completed = 0;
            var controller = f.Controller(() => { worker = Environment.CurrentManagedThreadId; entered.Set(); release.Wait(TimeSpan.FromMilliseconds(600)); });
            controller.Uninstalled = () => { Check(!Directory.Exists(f.Folder), "completion before deletion"); completed = Environment.CurrentManagedThreadId; };
            var timer = Stopwatch.StartNew();
            try
            {
                controller.Uninstall(new UninstallActionArgs());
                Check(timer.ElapsedMilliseconds < 400, "Uninstall blocked the caller");
                Check(entered.Wait(2000), "worker did not start");
                Check(worker != context.Owner, "pause/deletion still on UI thread");
                Check(f.Game.IsInstalled && completed == 0, "reported success while still deleting");
                Check(f.Game.IsUninstalling, "missing in-progress state");
            }
            finally { release.Set(); }
            context.Until(() => !f.Game.IsUninstalling);
            Check(completed == context.Owner && !f.Game.IsInstalled, "completion/state not returned to UI");
        });
        Test("file-only uninstall preserves other files", () =>
        {
            using var f = new Fixture();
            f.Settings.UninstallBehavior = PersonalCloudLibrarySourceSettings.RemoveCachedFileOnlyUninstallBehavior;
            f.Run(context);
            Check(!File.Exists(f.Item.Launch) && File.Exists(Path.Combine(f.Folder, "keep.dat")), "wrong deletion scope");
            Check(!f.Game.IsInstalled && f.Completed == 1, "missing completion");
        });
        Test("cancel preserves files, installed state and mod state", () =>
        {
            using var f = new Fixture();
            f.Settings.UninstallBehavior = PersonalCloudLibrarySourceSettings.AskEachTimeUninstallBehavior;
            f.Api.Dialogs.Choice = System.Windows.MessageBoxResult.Cancel;
            f.Run(context);
            Check(File.Exists(f.Item.Launch) && f.Game.IsInstalled && f.Completed == 0 && f.Paused == 0, "cancel mutated data/state");
        });
        Test("unsafe target refuses deletion and clears progress", () =>
        {
            using var f = new Fixture();
            f.Settings.Refuse = true;
            f.Run(context);
            Check(File.Exists(f.Item.Launch) && f.Game.IsInstalled && f.Completed == 0 && f.Paused == 0, "unsafe target deleted");
        });
        Test("missing target does not report successful deletion", () =>
        {
            using var f = new Fixture();
            Directory.Delete(f.Folder, true);
            f.Run(context);
            Check(f.Game.IsInstalled && f.Completed == 0, "missing target reported as uninstalled");
        });
        Test("pause failure preserves cache; controller can retry", () =>
        {
            using var f = new Fixture();
            bool fail = true;
            var c = f.Controller(() => { if (fail) throw new IOException("sync active"); f.Paused++; });
            c.Uninstalled = () => f.Completed++;
            c.Uninstall(new UninstallActionArgs());
            context.Until(() => !f.Game.IsUninstalling);
            Check(File.Exists(f.Item.Launch) && f.Game.IsInstalled && f.Completed == 0, "pause failure deleted cache");
            fail = false; f.Game.IsUninstalling = true;
            c.Uninstall(new UninstallActionArgs());
            context.Until(() => !f.Game.IsUninstalling);
            Check(!Directory.Exists(f.Folder) && f.Completed == 1, "retry failed");
        });
        Test("duplicate request cannot start a second deletion", () =>
        {
            using var f = new Fixture();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int calls = 0;
            var c = f.Controller(() => { Interlocked.Increment(ref calls); entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); });
            c.Uninstalled = () => f.Completed++;
            try
            {
                c.Uninstall(new UninstallActionArgs());
                Check(entered.Wait(2000), "worker did not start");
                f.Controller(() => Interlocked.Increment(ref calls)).Uninstall(new UninstallActionArgs());
            }
            finally { release.Set(); }
            context.Until(() => !f.Game.IsUninstalling);
            Check(calls == 1 && f.Completed == 1, "duplicate deletion/completion");
        });
        foreach (var provider in new[] { "rclone", "local" })
        Test(provider + " install is nonblocking; actions and completion stay on UI", () =>
        {
            using var f = new Fixture();
            f.Game.IsInstalled = false; f.Settings.ProviderType = provider;
            File.Delete(f.Item.Launch);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int worker = 0, completed = 0;
            var c = f.Installer(() =>
            {
                worker = Environment.CurrentManagedThreadId; entered.Set();
                release.Wait(TimeSpan.FromMilliseconds(600));
                File.WriteAllText(f.Item.Launch, "downloaded");
                return new CopyResult { Succeeded = true };
            });
            f.Game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>();
            f.Game.GameActions.CollectionChanged += (_, __) => Check(Environment.CurrentManagedThreadId == context.Owner, "actions changed off UI");
            c.Installed = () => completed = Environment.CurrentManagedThreadId;
            var timer = Stopwatch.StartNew();
            try
            {
                c.Install(new InstallActionArgs());
                Check(timer.ElapsedMilliseconds < 400 && entered.Wait(2000), "install blocked/did not start");
                Check(worker != context.Owner && f.Game.IsInstalling && !f.Game.IsInstalled && completed == 0, "wrong worker/in-progress state");
                f.Installer(() => throw new Exception("duplicate install started")).Install(new InstallActionArgs());
            }
            finally { release.Set(); }
            context.Until(() => !f.Game.IsInstalling);
            Check(completed == context.Owner && f.Game.IsInstalled && f.Game.GameActions.Count == 1, "wrong install completion");
            Check(f.Game.GameActions[0].Path == f.Item.Launch, "wrong launch action");
        });
        Test("install publishes shared per-game download progress and removes it after success", () =>
        {
            using var f = new Fixture();
            f.Game.IsInstalled = false; File.Delete(f.Item.Launch);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var c = f.Installer(() => {
                entered.Set(); release.Wait(TimeSpan.FromSeconds(2));
                File.WriteAllText(f.Item.Launch, "fixture download");
                return new CopyResult { Succeeded = true };
            });
            try {
                c.Install(new InstallActionArgs());
                Check(entered.Wait(2000), "download fixture did not start");
                var status = InstallProgressState.Shared.Get(f.Game.Id);
                Check(status != null && status.Active && status.Percentage == 50 && status.TotalBytes == 100 && status.BytesPerSecond == 25,
                    "shared progress state did not receive the exact fixture telemetry");
            } finally { release.Set(); }
            context.Until(() => !f.Game.IsInstalling);
            Check(f.Game.IsInstalled && InstallProgressState.Shared.Get(f.Game.Id) == null, "successful progress did not disappear");
        });
        foreach (var failure in new[] { "copy failure", "missing executable", "exception" })
        Test("install " + failure + " clears progress without success", () =>
        {
            using var f = new Fixture();
            f.Game.IsInstalled = false; File.Delete(f.Item.Launch);
            int cancelled = 0, installed = 0;
            var c = f.Installer(() => failure == "exception" ? throw new IOException("copy crashed") : new CopyResult { Succeeded = failure != "copy failure", Message = "copy failed" });
            c.Installed = () => installed++;
            c.Cancelled = () => { Check(Environment.CurrentManagedThreadId == context.Owner, "cancel off UI"); cancelled++; };
            c.Install(new InstallActionArgs());
            context.Until(() => !f.Game.IsInstalling);
            Check(cancelled == 1 && installed == 0 && !f.Game.IsInstalled, "failure reported as installed or left busy");
        });
        Test("installed callback failure does not also report cancellation", () =>
        {
            using var f = new Fixture();
            f.Game.IsInstalled = false;
            int cancelled = 0, installed = 0;
            var c = f.Installer(() => new CopyResult { Succeeded = true });
            c.Installed = () => { installed++; throw new Exception("host callback failed"); };
            c.Cancelled = () => cancelled++;
            c.Install(new InstallActionArgs());
            context.Until(() => !f.Game.IsInstalling);
            Check(installed == 1 && cancelled == 0 && f.Game.IsInstalled, "reported both installation and cancellation");
        });
        foreach (var mode in new[] { "disabled", "no path", "local provider", "plugin disabled" })
        Test("optional install mods are skipped: " + mode, () =>
        {
            using var f = new Fixture();
            f.Game.IsInstalled = false; f.Item.ModPath = "Mods";
            if (mode == "disabled") f.Settings.EnableModSync = false;
            if (mode == "no path") f.Item.ModPath = " ";
            if (mode == "local provider") f.Settings.ProviderType = "local";
            if (mode == "plugin disabled") f.Settings.Enabled = false;
            int mods = 0, installed = 0;
            var c = f.Installer(() => new CopyResult { Succeeded = true }, (token, progress, recover) =>
            { mods++; return Task.FromResult(new ModSyncResult { Success = true }); });
            c.Installed = () => installed++;
            c.Install(new InstallActionArgs()); context.Until(() => !f.Game.IsInstalling);
            Check(mods == 0 && installed == 1 && f.Game.IsInstalled, "ineligible install restored mods or lost the base game");
        });
        foreach (var mode in new[] { "failure", "cancelled", "exception", "conflicts" })
        Test("mod-only retry retains base install after optional " + mode, () =>
        {
            using var f = new Fixture();
            f.Game.IsInstalled = false; f.Item.ModPath = "Mods";
            int copies = 0, mods = 0, installed = 0, cancelled = 0;
            var c = f.Installer(() => { copies++; return new CopyResult { Succeeded = true }; }, (token, progress, recover) =>
            {
                Check(!recover, "ordinary mod retry silently authorized state recovery");
                if (++mods > 1) return Task.FromResult(new ModSyncResult { Success = true });
                if (mode == "exception") throw new IOException("mod fixture failed");
                return Task.FromResult(new ModSyncResult { Success = mode == "conflicts", HasConflicts = mode == "conflicts",
                    Cancelled = mode == "cancelled", Message = "optional mods fixture" });
            });
            c.Installed = () => { Check(Environment.CurrentManagedThreadId == context.Owner, "installed event off UI"); installed++; };
            c.Cancelled = () => cancelled++;
            c.Install(new InstallActionArgs()); context.Until(() => !f.Game.IsInstalling);
            var status = InstallProgressState.Shared.Get(f.Game.Id);
            Check(f.Game.IsInstalled && installed == 1 && cancelled == 0 && status.CanRetry && status.RetryLabel == "Retry mods",
                "optional failure cancelled base install or lost retry status");
            Check(InstallProgressState.Shared.Retry(f.Game.Id), "mod-only retry not offered");
            context.Until(() => !f.Game.IsInstalling);
            Check(copies == 1 && mods == 2 && installed == 1 && cancelled == 0 && InstallProgressState.Shared.Get(f.Game.Id) == null,
                "mod retry repeated base download/install event or left progress busy");
        });
        Test("install mod retry never starts recovery or two-way synchronization", () =>
        {
            using var f = new Fixture(); f.Game.IsInstalled = false; f.Item.ModPath = "Mods";
            int calls = 0, copies = 0;
            var c = f.Installer(() => { copies++; return new CopyResult { Succeeded = true }; }, (token, progress, recover) =>
            {
                Check(!recover, "installation retry requested two-way recovery");
                calls++;
                return Task.FromResult(calls == 1 ? new ModSyncResult { RequiresRelink = true, Message = "interrupted setup" } :
                    new ModSyncResult { Success = true });
            });
            c.Install(new InstallActionArgs()); context.Until(() => !f.Game.IsInstalling);
            var status = InstallProgressState.Shared.Get(f.Game.Id);
            Check(calls == 1 && status.RetryLabel == "Retry mods" && !status.Message.Contains("archives"),
                "download failure exposed a baseline reset instead of download-only retry");
            Check(InstallProgressState.Shared.Retry(f.Game.Id), "explicit recovery unavailable");
            context.Until(() => !f.Game.IsInstalling);
            Check(calls == 2 && copies == 1 && f.Game.IsInstalled && InstallProgressState.Shared.Get(f.Game.Id) == null,
                "recovery redownloaded game or left mod progress stuck");
        });
        Test("background mods start only after Installed and cleared installation state", () =>
        {
            using var f = new Fixture(); f.Game.IsInstalled = false; f.Item.ModPath = "Mods";
            int installed = 0, backgroundStarted = 0;
            var unfinishedBackground = new TaskCompletionSource<bool>();
            var c = f.Installer(() => new CopyResult { Succeeded = true },
                (token, progress, recover) => Task.FromResult(new ModSyncResult { Success = true }), () =>
                {
                    Check(installed == 1 && !f.Game.IsInstalling && f.Game.IsInstalled, "background started before install completion");
                    Check(InstallProgressState.Shared.Get(f.Game.Id) == null, "install session still owned by background starter");
                    backgroundStarted++;
                    _ = unfinishedBackground.Task; // Explicit blocked background double; must not be awaited by Install.
                });
            c.Installed = () => { Check(!f.Game.IsInstalling, "Installed emitted while game still marked installing"); installed++; };
            c.Install(new InstallActionArgs()); context.Until(() => backgroundStarted == 1);
            Check(!unfinishedBackground.Task.IsCompleted && installed == 1 && !f.Game.IsInstalling,
                "background work blocked completion");
        });
        Test("failed optional download never queues background sync", () =>
        {
            using var f = new Fixture(); f.Game.IsInstalled = false; f.Item.ModPath = "Mods";
            int background = 0, installed = 0;
            var c = f.Installer(() => new CopyResult { Succeeded = true },
                (token, progress, recover) => Task.FromResult(new ModSyncResult { Message = "download failed" }), () => background++);
            c.Installed = () => installed++;
            c.Install(new InstallActionArgs()); context.Until(() => !f.Game.IsInstalling);
            Check(installed == 1 && background == 0 && f.Game.IsInstalled, "failure queued two-way work or lost base game");
        });
        Test("active inline mod restoration can be cancelled without uninstalling the game", () =>
        {
            using var f = new Fixture(); f.Game.IsInstalled = false; f.Item.ModPath = "Mods";
            int installed = 0, cancelled = 0;
            var c = f.Installer(() => new CopyResult { Succeeded = true }, async (token, progress, recover) =>
            {
                progress(new ModSyncProgress { Phase = "bisync", Bytes = 5, TotalBytes = 100, BytesPerSecond = 2 });
                await Task.Delay(Timeout.Infinite, token);
                return new ModSyncResult { Success = true };
            });
            c.Installed = () => installed++; c.Cancelled = () => cancelled++;
            c.Install(new InstallActionArgs());
            context.Until(() => InstallProgressState.Shared.Get(f.Game.Id)?.Phase == "DownloadingMods");
            var status = InstallProgressState.Shared.Get(f.Game.Id);
            Check(f.Game.IsInstalled && f.Game.IsInstalling && installed == 0 && status.Percentage == null,
                "mod stage lost base game or invented a combined-stage percentage");
            Check(InstallProgressState.Shared.Cancel(f.Game.Id, status.OperationId), "inline mod cancel not accepted");
            context.Until(() => !f.Game.IsInstalling);
            Check(f.Game.IsInstalled && installed == 1 && cancelled == 0 && InstallProgressState.Shared.Get(f.Game.Id).CanRetry,
                "mod cancellation cancelled/uninstalled base game or permanently disabled retry");
        });
        Test("cancel clears the separate host game record", () =>
        {
            using var f = new Fixture();
            var stored = new Game { IsInstalled = true, IsUninstalling = true };
            f.Api.Database.Games.Game = stored;
            f.Settings.UninstallBehavior = PersonalCloudLibrarySourceSettings.AskEachTimeUninstallBehavior;
            f.Run(context);
            Check(stored.IsInstalled && !stored.IsUninstalling, "host record left busy");
        });
        foreach (var path in new string[] { null, "", "   ", "\t\r\n", "Mods", @"D:\Mods\Game" })
        Test("mod menu requires nonblank modPath: " + (path ?? "null"), () =>
        {
            using var f = new Fixture();
            var plugin = f.MenuPlugin(System.Text.Json.JsonSerializer.Serialize(new { items = new[] { new { id = "test", modPath = path } } }));
            var menu = plugin.GetGameMenuItems(new GetGameMenuItemsArgs { Games = new[] { f.Game } }).ToList();
            Check(menu.Count == 0, "ZIP-only mode exposed manual cloud synchronization");
            Check(plugin.SyncCalls == 0, "manual sync entrypoint was invoked");
        });
        foreach (var json in new[] { "{}", "{broken", "{\"items\":[]}", "{\"items\":[null]}", "{\"items\":[{\"id\":\"other\",\"modPath\":\"Mods\"}]}", "{\"items\":[{\"id\":\"test\",\"modPath\":\"Mods\"},{\"id\":\"TEST\",\"modPath\":\"Mods\"}]}" })
        Test("invalid or ambiguous cached mod configuration hides menu: " + json, () =>
        {
            using var f = new Fixture(); var plugin = f.MenuPlugin(json);
            Check(!plugin.GetGameMenuItems(new GetGameMenuItemsArgs { Games = new[] { f.Game } }).Any(), "invalid cache exposed mod actions");
        });
        Test("missing cache hides mod actions without a cloud read", () =>
        {
            using var f = new Fixture(); var plugin = f.MenuPlugin("{\"items\":[]}"); File.Delete(plugin.ModManifestPath);
            Check(!plugin.GetGameMenuItems(new GetGameMenuItemsArgs { Games = new[] { f.Game } }).Any(), "missing cache exposed menu");
        });
        Test("disabled plugin or mod sync hides configured mod actions", () =>
        {
            using var f = new Fixture(); var plugin = f.MenuPlugin("{\"items\":[{\"id\":\"test\",\"modPath\":\"Mods\"}]}");
            var menuArgs = new GetGameMenuItemsArgs { Games = new[] { f.Game } };
            plugin.settings.Settings.Enabled = false;
            Check(!plugin.GetGameMenuItems(menuArgs).Any(), "disabled plugin exposed actions");
            plugin.settings.Settings.Enabled = true; plugin.settings.Settings.EnableModSync = false;
            Check(!plugin.GetGameMenuItems(menuArgs).Any(), "disabled mod sync exposed actions");
        });
        Test("only a single owned game can expose mod actions", () =>
        {
            using var f = new Fixture(); var plugin = f.MenuPlugin("{\"items\":[{\"id\":\"test\",\"modPath\":\"Mods\"}]}");
            var foreign = new Game();
            foreach (var games in new[] { new Game[0], new[] { foreign }, new[] { f.Game, foreign }, new[] { f.Game, f.Game }, new Game[] { null } })
                Check(!plugin.GetGameMenuItems(new GetGameMenuItemsArgs { Games = games }).Any(), "invalid selection exposed actions");
            Check(!plugin.GetGameMenuItems(null).Any() && !plugin.GetGameMenuItems(new GetGameMenuItemsArgs()).Any(), "missing selection exposed actions");
        });
        Test("imported game IDs remain case insensitive", () =>
        {
            using var f = new Fixture(); var plugin = f.MenuPlugin("{\"items\":[null,{\"id\":\"TEST\",\"modPath\":\"Mods\"}]}");
            Check(!plugin.GetGameMenuItems(new GetGameMenuItemsArgs { Games = new[] { f.Game } }).Any(), "legacy sync actions remained exposed");
        });
        Test("cloud mod folders use the literal manifest ID", () => {
            Check(ModSyncService.ResolveCloudModFolder("Library/files", "skyrim-se") == "Library/files/mods/skyrim-se", "wrong ID folder");
            Check(ModSyncService.ResolveCloudModFolder(null, "Game-ID_1") == "mods/Game-ID_1", "ID was normalized");
            Check(ModSyncService.ResolveCloudModFolder("/Library/files/", "game-2") == "Library/files/mods/game-2", "root normalization changed");
        });
        foreach (var id in new[] { null, "", " ", ".", "..", "../game", "game/path", @"game\path", "game:edition", "game\"name", "game*", "game?", "game|", "game<", "game>", "game.", "game ", "game\nname", "game\u0001name" })
        Test("unsafe game ID is refused rather than rewritten: " + (id ?? "null"), () => {
            bool refused = false;
            try { ModSyncService.ResolveCloudModFolder("Library/files", id); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "invalid ID was accepted or silently rewritten");
        });
        foreach (var root in new[] { "../Library", "Library/../games", "Library/./games", "remote:games", "Library\nfiles", "Library\"files" })
        Test("unsafe mod content root is refused: " + root, () => {
            bool refused = false;
            try { ModSyncService.ResolveCloudModFolder(root, "game-id"); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "unsafe root was accepted");
        });
        Test("uninstall revalidates a path changed by the actual pause hook", () =>
        {
            using var f = new Fixture();
            var external = Path.Combine(Path.GetTempPath(), "pcls-uninstall-external-" + Guid.NewGuid().ToString("N"));
            var backup = f.Folder + ".original";
            Directory.CreateDirectory(external); File.WriteAllText(Path.Combine(external, "keep.bin"), "keep");
            bool linked = false;
            try
            {
                var c = f.Controller(() =>
                {
                    f.Paused++; Directory.Move(f.Folder, backup);
                    if (OperatingSystem.IsWindows())
                    {
                        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + f.Folder + "\" \"" + external + "\"") { UseShellExecute = false, CreateNoWindow = true });
                        process.WaitForExit(); Check(process.ExitCode == 0, "junction fixture could not be created");
                    }
                    else Directory.CreateSymbolicLink(f.Folder, external);
                    linked = true;
                });
                c.Uninstalled = () => f.Completed++;
                c.Uninstall(new UninstallActionArgs()); context.Until(() => !f.Game.IsUninstalling);
                Check(linked && f.Paused == 1 && f.Completed == 0 && f.Game.IsInstalled, "unsafe uninstall was completed or pause not exercised");
                Check(File.ReadAllText(Path.Combine(external, "keep.bin")) == "keep", "external data was deleted after pause");
            }
            finally
            {
                if (linked && Directory.Exists(f.Folder)) Directory.Delete(f.Folder, false);
                if (Directory.Exists(backup)) Directory.Move(backup, f.Folder);
                if (Directory.Exists(external)) Directory.Delete(external, true);
            }
        });
        Console.WriteLine($"RESULT Cache controllers: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    sealed class Fixture : IDisposable
    {
        public string Folder = Path.Combine(Path.GetTempPath(), "pcls-uninstall-" + Guid.NewGuid().ToString("N"));
        public Game Game = new Game { IsInstalled = true, IsUninstalling = true };
        public PersonalCloudLibrarySourceSettings Settings = new PersonalCloudLibrarySourceSettings();
        public PersonalCloudLibraryItem Item;
        public IPlayniteAPI Api = new IPlayniteAPI();
        public int Paused, Completed;
        public Fixture()
        {
            Directory.CreateDirectory(Folder);
            Item = new PersonalCloudLibraryItem { Launch = Path.Combine(Folder, "game.exe"), Folder = Folder };
            File.WriteAllText(Item.Launch, "game"); File.WriteAllText(Path.Combine(Folder, "keep.dat"), "keep");
            Settings.LocalCacheFolder = Path.GetDirectoryName(Folder);
            Api.Database.Games.Game = Game;
        }
        public PersonalCloudLibraryUninstallController Controller(Action before = null) => new PersonalCloudLibraryUninstallController(Api, Game, Item, Settings, before ?? (() => Paused++));
        public RcloneInstallController Installer(Func<CopyResult> copy,
            Func<CancellationToken, Action<ModSyncProgress>, bool, Task<ModSyncResult>> mods = null, Action background = null) =>
            new RcloneInstallController(Game, Item, Settings, new RcloneFileCopier { Copy = copy }, new LocalFileCopier { Copy = copy }, mods, background);
        public PersonalCloudLibrarySource.PersonalCloudLibrarySource MenuPlugin(string json)
        {
            var plugin = new PersonalCloudLibrarySource.PersonalCloudLibrarySource { ModManifestPath = Path.Combine(Folder, "manifest.json") };
            Game.PluginId = plugin.Id; File.WriteAllText(plugin.ModManifestPath, json); return plugin;
        }
        public void Run(UiContext context)
        {
            var c = Controller(); c.Uninstalled = () => Completed++;
            c.Uninstall(new UninstallActionArgs()); context.Until(() => !Game.IsUninstalling);
        }
        public void Dispose() { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
    }
    sealed class UiContext : SynchronizationContext
    {
        public int Owner = Environment.CurrentManagedThreadId;
        readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
        public override void Post(SendOrPostCallback callback, object state) => queue.Enqueue(() => callback(state));
        public void Until(Func<bool> done)
        {
            var timer = Stopwatch.StartNew();
            while (!done())
            {
                if (timer.Elapsed.TotalSeconds > 10) throw new Exception("controller did not finish");
                if (queue.TryDequeue(out var action)) action(); else Thread.Sleep(1);
            }
        }
    }
}

namespace System.Windows
{
    public enum MessageBoxButton { YesNoCancel }
    public enum MessageBoxImage { Question }
    public enum MessageBoxResult { Yes, No, Cancel }
}
namespace Playnite.SDK.Models
{
    public class Game { public Guid Id = Guid.NewGuid(), PluginId; public string Name = "Test game", GameId = "test", InstallDirectory; public bool IsInstalled, IsUninstalling, IsInstalling; public System.Collections.ObjectModel.ObservableCollection<GameAction> GameActions; }
    public enum GameActionType { File }
    public class GameAction { public string Name, Path, WorkingDir; public GameActionType Type; public bool IsPlayAction; }
}
namespace Playnite.SDK
{
    public class ILogger { public void Info(string s) {} public void Warn(string s) {} public void Warn(Exception e, string s) {} public void Error(Exception e, string s) {} }
    public static class LogManager { public static ILogger GetLogger() => new ILogger(); }
    public class IPlayniteAPI { public Dialogs Dialogs = new Dialogs(); public Database Database = new Database(); }
    public class Database { public Games Games = new Games(); }
    public class Games { public Game Game; public Game Get(Guid id) => Game; public void Update(Game game) {} }
    public class Dialogs
    {
        public System.Windows.MessageBoxResult Choice = System.Windows.MessageBoxResult.Cancel;
        public System.Windows.MessageBoxResult ShowMessage(string text, string title, System.Windows.MessageBoxButton buttons, System.Windows.MessageBoxImage icon) => Choice;
        public void ShowErrorMessage(string text, string title) {}
    }
}
namespace Playnite.SDK.Plugins
{
    public class GetGameMenuItemsArgs { public System.Collections.Generic.IEnumerable<Game> Games; }
    public class GameMenuItem { public string Description, MenuSection; public Action<object> Action; }
    public abstract class LibraryPlugin { public abstract System.Collections.Generic.IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args); }
    public class UninstallActionArgs {}
    public class InstallActionArgs {}
    public class GameInstallationData { public string InstallDirectory; }
    public class GameInstalledEventArgs { public GameInstalledEventArgs(GameInstallationData data) {} }
    public class GameInstallationCancelledEventArgs {}
    public abstract class InstallController
    {
        public Game Game; public string Name; public Action Installed, Cancelled;
        protected InstallController(Game game) { Game = game; }
        public abstract void Install(InstallActionArgs args);
        protected void InvokeOnInstalled(GameInstalledEventArgs args) => Installed?.Invoke();
        protected void InvokeOnInstallationCancelled(GameInstallationCancelledEventArgs args) => Cancelled?.Invoke();
    }
    public abstract class UninstallController
    {
        public Game Game; public string Name; public Action Uninstalled;
        protected UninstallController(Game game) { Game = game; }
        public abstract void Uninstall(UninstallActionArgs args);
        protected void InvokeOnUninstalled() => Uninstalled?.Invoke();
    }
}
namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibraryItem { public string Launch, Folder, Title = "test", Id, ModPath; }
    public class PersonalCloudLibrarySourceSettings
    {
        public const string AskEachTimeUninstallBehavior = "ask", RemoveCachedFileOnlyUninstallBehavior = "file", RemoveCachedInstallFolderUninstallBehavior = "folder";
        public string UninstallBehavior = "folder", LocalCacheFolder; public bool Refuse, AllowUninstallOutsideCacheFolder;
        public const string RcloneRemoteProviderType = "rclone";
        public string ProviderType = "rclone";
        public bool Enabled = true, EnableModSync = true;
    }
    public partial class PersonalCloudLibrarySource : LibraryPlugin
    {
        public Guid Id = Guid.NewGuid();
        public string ModManifestPath;
        public int SyncCalls; public bool Relink;
        public MenuSettings settings = new MenuSettings();
        private static readonly ILogger logger = new ILogger();
        private void RunModSync(Game game, bool stopped, bool manual, bool relink) { SyncCalls++; Relink = relink; }
        private static PersonalCloudLibraryManifest ParseManifest(string json)
        {
            var result = System.Text.Json.JsonSerializer.Deserialize<PersonalCloudLibraryManifest>(json, new System.Text.Json.JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true });
            if (result?.Items == null) throw new InvalidOperationException("invalid manifest");
            return result;
        }
        public static string ResolveLaunchPath(PersonalCloudLibraryItem i, PersonalCloudLibrarySourceSettings s) => i.Launch;
        public static string ResolveDownloadDestinationFolder(PersonalCloudLibraryItem i, PersonalCloudLibrarySourceSettings s, string launch) => i.Folder;
        public static string GetItemSourcePath(PersonalCloudLibraryItem i) => i.Launch;
        public static string GetProviderType(PersonalCloudLibrarySourceSettings s) => s.ProviderType;
        public static string ResolveRcloneSourcePath(PersonalCloudLibrarySourceSettings s, string source) => source;
        public static string ResolveLocalFolderSourcePath(PersonalCloudLibrarySourceSettings s, string source) => source;
        public static string ResolveInstallDirectory(PersonalCloudLibraryItem i, PersonalCloudLibrarySourceSettings s, string launch) => i.Folder;
        public static string ResolveSafeUninstallTarget(PersonalCloudLibrarySourceSettings s, string path, out string reason) { reason = s.Refuse || string.IsNullOrEmpty(path) ? "refused" : ""; return reason == "" ? path : ""; }
        public static bool IsPathInsideCacheFolder(string path, string root) => path.StartsWith(root);
    }
    public class MenuSettings { public PersonalCloudLibrarySourceSettings Settings = new PersonalCloudLibrarySourceSettings(); }
    public class PersonalCloudLibraryManifest { public System.Collections.Generic.List<PersonalCloudLibraryItem> Items; }
    public class CopyResult { public bool Succeeded, Cancelled; public string Message; public Exception Exception; }
    // Explicit transfer double for exercising the real install controller/host-context boundary.
    public class RcloneTransferProgress
    {
        public string Phase; public long CompletedBytes; public long? TotalBytes;
        public double BytesPerSecond; public double? Percentage;
    }
    public class RcloneFileCopier
    {
        public Func<CopyResult> Copy;
        public CopyResult CopyRemoteDirectoryToLocalPath(PersonalCloudLibrarySourceSettings settings, string source, string target) => Copy();
        public CopyResult CopyRemoteDirectoryToLocalPath(PersonalCloudLibrarySourceSettings settings, string source, string target,
            CancellationToken cancellationToken, Action<RcloneTransferProgress> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new RcloneTransferProgress { Phase = "Inventory" });
            progress?.Invoke(new RcloneTransferProgress { Phase = "Downloading", CompletedBytes = 50, TotalBytes = 100, BytesPerSecond = 25, Percentage = 50 });
            return Copy();
        }
    }
    public class LocalFileCopier { public Func<CopyResult> Copy; public CopyResult CopyFileToLocalPath(string source, string target) => Copy(); }
}
