using PersonalCloudLibrarySource;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
    passed++;
}

string Root()
{
    var path = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath()), "legacy-migration-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

(List<LegacyGameOwnership> games, Action<string, Guid> update) Owners(params LegacyGameOwnership[] initial)
{
    var records = initial.ToList();
    void Update(string id, Guid pluginId)
    {
        var game = records.Single(g => string.Equals(g.GameId, id, StringComparison.OrdinalIgnoreCase));
        game.PluginId = pluginId;
    }
    return (records, Update);
}

void Seed(string source, string destination, bool settings = true, bool manifest = true)
{
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(destination);
    if (settings) File.WriteAllText(Path.Combine(source, "config.json"), "{\"LibraryDisplayName\":\"Custom\",\"secretLikeConfig\":\"not copied from any rclone config\"}");
    if (manifest) File.WriteAllText(Path.Combine(source, "mod-sync-manifest.json"), "{\"items\":[]}");
}

LegacyGameOwnership NewLegacy() => new LegacyGameOwnership { GameId = "a", PluginId = ForkIdentity.LegacyPluginId };
Check(ForkIdentity.PluginId == Guid.Parse("402561c2-ea33-4c60-a8f8-ec25e997a1d5") && ForkIdentity.LegacyPluginId == Guid.Parse("61993828-67a8-4468-93a2-293442e36328") && ForkIdentity.DisplayName == "Cloud Storage Integration" && ForkIdentity.ExtensionYamlId == "CHRCloudLibrary_402561c2-ea33-4c60-a8f8-ec25e997a1d5", "fork and legacy identities match the wiring contract");
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy());
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(false, false, false);
    Check(result.Success && !result.Changed && games[0].PluginId == ForkIdentity.LegacyPluginId && !File.Exists(Path.Combine(destination, "config.json")), "consent no-op changes neither files nor ownership");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy());
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, true, false);
    Check(!result.Success && games[0].PluginId == ForkIdentity.LegacyPluginId && !File.Exists(Path.Combine(destination, "config.json")), "active legacy extension is refused before mutation");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy());
    File.WriteAllText(Path.Combine(destination, "config.json"), "new settings");
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(!result.Success && File.ReadAllText(Path.Combine(destination, "config.json")) == "new settings" && games[0].PluginId == ForkIdentity.LegacyPluginId, "target settings conflict is preserved and refused");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy(), new LegacyGameOwnership { GameId = "A", PluginId = ForkIdentity.LegacyPluginId });
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(!result.Success && games.All(g => g.PluginId == ForkIdentity.LegacyPluginId) && !File.Exists(Path.Combine(destination, "config.json")), "duplicate legacy GameId mapping is refused");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy(), new LegacyGameOwnership { GameId = "a", PluginId = ForkIdentity.PluginId });
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(!result.Success && games.All(g => g.PluginId == ForkIdentity.LegacyPluginId || g.PluginId == ForkIdentity.PluginId) && !File.Exists(Path.Combine(destination, "mod-sync-manifest.json")), "legacy/new duplicate GameId mapping is refused");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy(), new LegacyGameOwnership { GameId = "other", PluginId = Guid.NewGuid() });
    var unrelated = Path.Combine(destination, "existing-history.log"); File.WriteAllText(unrelated, "keep");
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(result.Success && result.GamesAdopted == 1 && games[0].PluginId == ForkIdentity.PluginId && games[1].PluginId != ForkIdentity.PluginId, "adoption changes only legacy ownership and preserves unrelated ownership/history");
    Check(File.ReadAllText(Path.Combine(destination, "config.json")) == File.ReadAllText(Path.Combine(source, "config.json")) && File.ReadAllText(Path.Combine(destination, "mod-sync-manifest.json")) == File.ReadAllText(Path.Combine(source, "mod-sync-manifest.json")), "settings and cached manifest are copied byte-for-byte");
    Check(File.Exists(Path.Combine(source, "config.json")) && File.Exists(unrelated), "legacy data and unrelated destination history remain intact");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    Directory.CreateDirectory(Path.Combine(source, "mod-sync", "hash")); File.WriteAllText(Path.Combine(source, "mod-sync", "hash", "pair.txt"), "C:/mods\nremote:mods/game");
    var (games, update) = Owners(NewLegacy());
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(result.Success && games[0].PluginId == ForkIdentity.PluginId && File.ReadAllText(Path.Combine(destination, "mod-sync", "hash", "pair.txt")) == File.ReadAllText(Path.Combine(source, "mod-sync", "hash", "pair.txt")), "existing mod-sync pairing is copied byte-for-byte with adoption");
    Check(File.Exists(Path.Combine(source, "mod-sync", "hash", "pair.txt")), "source pairing remains intact after adoption");
    Check(File.GetLastWriteTimeUtc(Path.Combine(source, "mod-sync", "hash", "pair.txt")) == File.GetLastWriteTimeUtc(Path.Combine(destination, "mod-sync", "hash", "pair.txt")), "copied pairing state retains its file timestamp");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy(), new LegacyGameOwnership { GameId = "b", PluginId = ForkIdentity.LegacyPluginId });
    var failedOnce = false;
    void UpdateWithFault(string id, Guid pluginId)
    {
        if (id == "b" && pluginId == ForkIdentity.PluginId && !failedOnce) { failedOnce = true; throw new IOException("injected ownership write failure"); }
        update(id, pluginId);
    }
    var result = new LegacyLibraryMigration(source, destination, () => games, UpdateWithFault).Execute(true, false, false);
    Check(!result.Success && games.All(g => g.PluginId == ForkIdentity.LegacyPluginId) && !File.Exists(Path.Combine(destination, "config.json")) && !File.Exists(Path.Combine(destination, "legacy-migration-journal.json")), "ordinary ownership failure rolls back game changes and copied files");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(new LegacyGameOwnership { GameId = "a", PluginId = ForkIdentity.PluginId });
    File.WriteAllText(Path.Combine(destination, "config.json"), "newly copied");
    File.WriteAllText(Path.Combine(destination, "legacy-migration-journal.json"), "{\"Phase\":\"data-copied\",\"GameIds\":[\"a\"],\"Files\":[\"config.json\"],\"Source\":\"" + source.Replace("\\", "\\\\") + "\",\"Destination\":\"" + destination.Replace("\\", "\\\\") + "\"}");
    new LegacyLibraryMigration(source, destination, () => games, update).RecoverInterruptedMigration();
    Check(games[0].PluginId == ForkIdentity.LegacyPluginId && !File.Exists(Path.Combine(destination, "config.json")) && !File.Exists(Path.Combine(destination, "legacy-migration-journal.json")), "startup recovery reverses journaled partial adoption and removes only copied paths");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy()); File.WriteAllText(Path.Combine(destination, "config.json"), "committed");
    var journal = Path.Combine(destination, "legacy-migration-journal.json");
    var src = source.Replace("\\", "\\\\"); var dst = destination.Replace("\\", "\\\\");
    File.WriteAllText(journal, "{\"Phase\":\"complete\",\"GameIds\":[\"a\"],\"Files\":[\"config.json\"],\"Source\":\"" + src + "\",\"Destination\":\"" + dst + "\"}");
    update("a", ForkIdentity.PluginId);
    new LegacyLibraryMigration(source, destination, () => games, update).RecoverInterruptedMigration();
    Check(games[0].PluginId == ForkIdentity.PluginId && File.ReadAllText(Path.Combine(destination, "config.json")) == "committed" && !File.Exists(journal), "complete journal commits adoption instead of deleting copied data");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var running = new LegacyGameOwnership { GameId = "other", PluginId = Guid.NewGuid(), IsRunning = true };
    var (games, update) = Owners(NewLegacy(), running);
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(result.Success && games[0].PluginId == ForkIdentity.PluginId && games[1].PluginId == running.PluginId, "unrelated running games do not block adoption");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var installing = NewLegacy(); installing.IsInstalling = true;
    var (games, update) = Owners(installing);
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(!result.Success && games[0].PluginId == ForkIdentity.LegacyPluginId && !File.Exists(Path.Combine(destination, "config.json")), "installing legacy game blocks adoption before any write");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    Directory.CreateDirectory(Path.Combine(source, "mod-sync"));
    var linked = Path.Combine(source, "mod-sync", "external-link");
    Directory.CreateSymbolicLink(linked, root);
    var (games, update) = Owners(NewLegacy());
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(!result.Success && games[0].PluginId == ForkIdentity.LegacyPluginId && !File.Exists(Path.Combine(destination, "config.json")), "state containing a symlink/reparse target is refused without following it");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new-link"); Seed(source, Path.Combine(root, "new-real"));
    Directory.CreateSymbolicLink(destination, Path.Combine(root, "new-real"));
    var (games, update) = Owners(NewLegacy());
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, false);
    Check(!result.Success && games[0].PluginId == ForkIdentity.LegacyPluginId && !File.Exists(Path.Combine(root, "new-real", "config.json")), "destination symlink is refused without writing through it");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var games = new List<LegacyGameOwnership>();
    var copied = false;
    var result = new LegacyLibraryMigration(source, destination, () => games, (_, __) => { }, () => copied = true).Execute(true, false, false);
    Check(result.Success && result.Changed && result.GamesAdopted == 0 && copied && File.Exists(Path.Combine(destination, "config.json")), "explicit adoption copies legacy settings even when there are no legacy-owned games");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy());
    var result = new LegacyLibraryMigration(source, destination, () => games, update).Execute(true, false, true);
    Check(result.Success && !result.Changed && games[0].PluginId == ForkIdentity.LegacyPluginId, "separate-library choice keeps legacy ownership and data untouched");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(new LegacyGameOwnership { GameId = "a", PluginId = ForkIdentity.PluginId });
    var copied = Path.Combine(destination, "config.json"); File.WriteAllText(copied, "preserve");
    var journal = Path.Combine(destination, "legacy-migration-journal.json");
    File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new { Phase = "unknown", GameIds = new[] { "a" }, Files = new[] { "config.json" }, Source = source, Destination = destination }));
    var refused = false;
    try { new LegacyLibraryMigration(source, destination, () => games, update).RecoverInterruptedMigration(); }
    catch { refused = true; }
    Check(refused && File.Exists(journal) && File.ReadAllText(copied) == "preserve" && games[0].PluginId == ForkIdentity.PluginId, "unknown journal phase preserves all evidence without rollback");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(NewLegacy(), new LegacyGameOwnership { GameId = "b", PluginId = ForkIdentity.LegacyPluginId });
    void Fault(string id, Guid owner)
    {
        if (id == "b" && owner == ForkIdentity.PluginId) throw new IOException("injected adoption failure");
        if (id == "a" && owner == ForkIdentity.LegacyPluginId) throw new IOException("injected rollback failure");
        update(id, owner);
    }
    var result = new LegacyLibraryMigration(source, destination, () => games, Fault).Execute(true, false, false);
    Check(!result.Success && File.Exists(Path.Combine(destination, "config.json")) && File.Exists(Path.Combine(destination, "legacy-migration-journal.json")), "incomplete ownership rollback retains copied settings and journal for recovery");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var (games, update) = Owners(new LegacyGameOwnership { GameId = "a", PluginId = ForkIdentity.PluginId });
    var copied = Path.Combine(destination, "config.json"); File.WriteAllText(copied, "recover later");
    var journal = Path.Combine(destination, "legacy-migration-journal.json");
    File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new { Phase = "data-copied", GameIds = new[] { "a" }, Files = new[] { "config.json" }, Source = source, Destination = destination }));
    var refused = false;
    try { new LegacyLibraryMigration(source, destination, () => games, update).RecoverInterruptedMigration(true); }
    catch { refused = true; }
    Check(refused && File.Exists(journal) && File.ReadAllText(copied) == "recover later" && games[0].PluginId == ForkIdentity.PluginId, "startup recovery cannot mutate a loaded legacy extension");
}
{
    var root = Root(); var source = Path.Combine(root, "old"); var destination = Path.Combine(root, "new"); Seed(source, destination);
    var unrelated = new LegacyGameOwnership { GameId = "a", PluginId = Guid.NewGuid(), IsRunning = true };
    var games = new List<LegacyGameOwnership> { NewLegacy(), unrelated };
    void UpdateRelevant(string id, Guid owner) { games.Single(g => g.PluginId == ForkIdentity.LegacyPluginId || g.PluginId == ForkIdentity.PluginId).PluginId = owner; }
    var result = new LegacyLibraryMigration(source, destination, () => games, UpdateRelevant).Execute(true, false, false);
    Check(result.Success && games[0].PluginId == ForkIdentity.PluginId && games[1].PluginId == unrelated.PluginId, "another library can use the same remote GameId without blocking or being adopted");
}
Check(ForkIdentity.ResolveDisplayName(ForkIdentity.LegacyDefaultDisplayName) == ForkIdentity.DisplayName &&
    ForkIdentity.ResolveDisplayName(null) == ForkIdentity.DisplayName, "legacy default branding is resolved without changing stored settings");
Check(ForkIdentity.ResolveDisplayName("My custom library") == "My custom library", "custom library names are preserved by presentation mapping");
Console.WriteLine("RESULT legacy migration policy: " + passed + " passed");
