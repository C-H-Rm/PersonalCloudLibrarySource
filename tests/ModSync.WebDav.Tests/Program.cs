using System;
using System.IO;
using PersonalCloudLibrarySource;

var root = Environment.GetEnvironmentVariable("PCLS_WEBDAV_ROOT");
var local = Path.Combine(root, "local", "Mods");
var cloud = Path.Combine(root, "cloud", "games", "mods", "Game Name");
Directory.CreateDirectory(local);
Directory.CreateDirectory(cloud);
var launch = Path.Combine(root, "local", "game.exe");
File.WriteAllText(launch, "fixture");
var service = new ModSyncService();
var request = new ModSyncRequest {
    GameId = "webdav-fixture", LocalFolder = local, RemoteFolder = "pclstest:games/mods/Game Name",
    StateDirectory = Path.Combine(root, "state"), LaunchFile = launch,
    RcloneExecutable = Environment.GetEnvironmentVariable("PCLS_RCLONE"), TimeoutSeconds = 60
};
var count = 0;
void Sync() { var r = service.Synchronize(request, _ => true); if (!r.Success) throw new Exception(r.Message); }
void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS WebDAV " + label); }
File.WriteAllText(Path.Combine(local, "keep"), "keep");
File.WriteAllText(Path.Combine(local, "mod"), "initial mod");
Sync(); Check(File.ReadAllText(Path.Combine(cloud, "mod")) == "initial mod", "upload");
File.WriteAllText(Path.Combine(local, "mod"), "updated local mod");
File.SetLastWriteTimeUtc(Path.Combine(local, "mod"), DateTime.UtcNow.AddMinutes(1));
Sync(); Check(File.ReadAllText(Path.Combine(cloud, "mod")) == "updated local mod", "local update");
File.WriteAllText(Path.Combine(cloud, "remote"), "from server");
Sync(); Check(File.ReadAllText(Path.Combine(local, "remote")) == "from server", "download");
File.Delete(Path.Combine(local, "mod"));
Sync(); Check(!File.Exists(Path.Combine(cloud, "mod")), "local deletion propagated");
File.Delete(Path.Combine(cloud, "remote"));
Sync(); Check(!File.Exists(Path.Combine(local, "remote")), "cloud deletion propagated");
File.Delete(Path.Combine(local, "keep"));
var denied = service.Synchronize(request, _ => false);
Check(!denied.Success && File.Exists(Path.Combine(cloud, "keep")), "whole clear cancelled safely");
Sync(); Check(!File.Exists(Path.Combine(cloud, "keep")), "whole clear confirmed");
File.WriteAllText(Path.Combine(local, "restored"), "preserved"); Sync();
service.Pause(request.StateDirectory);
Directory.Move(local, local + "-removed");
Check(!service.Synchronize(request, _ => true).Success && File.Exists(Path.Combine(cloud, "restored")), "uninstall preserves cloud");
var restored = service.Relink(request, _ => true);
Check(restored.Success && File.ReadAllText(Path.Combine(local, "restored")) == "preserved", "explicit relink restores mods");
Console.WriteLine("RESULT WebDAV: " + count + " passed");
