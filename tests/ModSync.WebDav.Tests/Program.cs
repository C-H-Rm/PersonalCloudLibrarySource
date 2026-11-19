using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
File.WriteAllText(Path.Combine(cloud, "install-cloud"), "from cloud after reinstall");
service.Pause(request.StateDirectory);
Directory.Move(local, local + "-old");
Directory.CreateDirectory(local);
File.WriteAllText(Path.Combine(local, "install-local"), "from installer");
var installRestore = service.RestoreAfterInstall(request);
Check(installRestore.Success && File.ReadAllText(Path.Combine(local, "install-cloud")) == "from cloud after reinstall" && File.Exists(Path.Combine(cloud, "install-local")), "install restore merges both sides");
var downloadRequest = new ModSyncRequest {
    GameId = request.GameId, LocalFolder = Path.Combine(root, "local", "DownloadOnlyMods"),
    RemoteFolder = request.RemoteFolder, StateDirectory = Path.Combine(root, "download-state"),
    LaunchFile = launch, RcloneExecutable = request.RcloneExecutable, TimeoutSeconds = 60
};
Directory.CreateDirectory(downloadRequest.LocalFolder);
File.WriteAllText(Path.Combine(downloadRequest.LocalFolder, "only-local-download"), "keep local until background");
var downloaded = service.DownloadAfterInstall(downloadRequest, r => {
    var copy = new ModSyncProcess().Run(r.RcloneExecutable, r.TimeoutSeconds, r.CancellationToken, r.Progress,
        "copy", r.RemoteFolder, r.LocalFolder, "--ignore-existing", "--exclude", ".pcls-modsync-*");
    return new ModSyncResult { Success = copy.ExitCode == 0, Cancelled = copy.Cancelled, Message = copy.Log };
});
Check(downloaded.Success && File.Exists(Path.Combine(downloadRequest.LocalFolder, "install-cloud")) &&
    !File.Exists(Path.Combine(cloud, "only-local-download")) && !File.Exists(Path.Combine(downloadRequest.StateDirectory, "ready")),
    "install download only reads cloud and does not initialize two-way state");
var zipLocal = Path.Combine(root, "local", "ModsForZip"); Directory.CreateDirectory(zipLocal);
File.WriteAllText(Path.Combine(zipLocal, "mod.bin"), "first version");
var zipRequest = new ModSyncRequest { GameId = "webdav-zip-fixture", LocalFolder = zipLocal,
    RemoteFolder = request.RemoteFolder + "-zip-fixture", StateDirectory = Path.Combine(root, "zip-state"),
    LaunchFile = launch, RcloneExecutable = request.RcloneExecutable, TimeoutSeconds = 60 };
var zipService = new ModZipUploadService();
var zipOne = zipService.UploadFirst(zipRequest);
Check(zipOne.Success, "ZIP upload to WebDAV: " + zipOne.Message);
var publishedZip = Path.Combine(root, "cloud", "games", "mods", "Game Name-zip-fixture.zip");
File.WriteAllText(Path.Combine(zipLocal, "mod.bin"), "replacement version");
var zipTwo = zipService.UploadIfChanged(zipRequest);
Check(zipTwo.Success, "ZIP replacement via WebDAV moveto: " + zipTwo.Message);
using (var archive = ZipFile.OpenRead(publishedZip))
using (var reader = new StreamReader(archive.GetEntry("mod.bin").Open()))
    Check(reader.ReadToEnd() == "replacement version", "ZIP replacement contents match the changed files");
var preserved = File.ReadAllBytes(publishedZip);
File.WriteAllText(Path.Combine(zipLocal, "mod.bin"), "failed replacement");
if (OperatingSystem.IsLinux()) {
    var wrapper = Path.Combine(root, "fail-publish-rclone.sh");
    File.WriteAllText(wrapper, "#!/bin/sh\n[ \"$1\" = moveto ] && exit 79\nexec \"" + request.RcloneExecutable + "\" \"$@\"\n");
    File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    zipRequest.RcloneExecutable = wrapper;
    var failedPublish = zipService.UploadIfChanged(zipRequest);
    Check(!failedPublish.Success && File.ReadAllBytes(publishedZip).SequenceEqual(preserved), "failed publish preserves the previous WebDAV ZIP");
    zipRequest.RcloneExecutable = request.RcloneExecutable;
    var retryPublish = zipService.UploadIfChanged(zipRequest);
    Check(retryPublish.Success, "retry after failed publish succeeds: " + retryPublish.Message);
    using (var archive = ZipFile.OpenRead(publishedZip))
    using (var reader = new StreamReader(archive.GetEntry("mod.bin").Open()))
        Check(reader.ReadToEnd() == "failed replacement", "retry publishes the previously failed content");
}
var background = service.RestoreAfterInstall(downloadRequest);
Check(background.Success && File.Exists(Path.Combine(cloud, "only-local-download")) &&
    File.Exists(Path.Combine(downloadRequest.StateDirectory, "ready")), "separate background phase initializes two-way synchronization");
Console.WriteLine("RESULT WebDAV: " + count + " passed");
