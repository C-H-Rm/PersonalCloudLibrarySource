using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PersonalCloudLibrarySource;

internal static class Program
{
    private static int passed, failed;
    private static void Main()
    {
        Test("initial merge preserves unique mods on both sides", f => {
            f.Local("local.txt", "local"); f.Cloud("cloud.txt", "cloud"); f.Sync();
            Check(File.Exists(f.Remote("local.txt")) && File.Exists(f.Path("cloud.txt")), "merge missing files");
        });
        Test("ZIP upload does not transfer an unchanged local fingerprint", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("mod.bin", "abcd");
            var audit = System.IO.Path.Combine(f.Root, "zip-audit.txt");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "zip-audit-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/sh\nprintf '%s\\n' \"$@\" >> \"" + audit + "\"\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var service = new ModZipUploadService();
            var first = service.UploadFirst(f.Request);
            Check(first.Success && File.Exists(CloudZipPath(f)), first.Message);
            var before = File.ReadAllLines(audit).Length;
            var second = service.UploadIfChanged(f.Request);
            Check(second.Success && File.ReadAllLines(audit).Length == before, "unchanged content performed rclone work");
        });

        Test("ZIP upload catches equal-size/equal-time edits and deletions", f => {
            f.Local("mod.bin", "aaaa"); f.Local("remove.bin", "remove");
            var service = new ModZipUploadService();
            Check(service.UploadFirst(f.Request).Success, "initial upload failed");
            var stamp = File.GetLastWriteTimeUtc(f.Path("mod.bin"));
            File.WriteAllText(f.Path("mod.bin"), "bbbb"); File.SetLastWriteTimeUtc(f.Path("mod.bin"), stamp);
            File.Delete(f.Path("remove.bin"));
            var changed = service.UploadIfChanged(f.Request);
            Check(changed.Success, changed.Message);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(CloudZipPath(f))) {
                Check(archive.Entries.Count == 1 && archive.GetEntry("mod.bin") != null, "replacement ZIP did not reflect deletion");
                using (var reader = new StreamReader(archive.GetEntry("mod.bin").Open())) Check(reader.ReadToEnd() == "bbbb", "content edit was missed");
            }
        });
        Test("fresh manifest validation runs only for changed ZIP content", f => {
            f.Local("mod", "original");
            var calls = 0;
            f.Request.BeforeCloudWrite = () => { calls++; return true; };
            var service = new ModZipUploadService();
            Check(service.UploadFirst(f.Request).Success && calls == 1, "first upload skipped validation");
            Check(service.UploadIfChanged(f.Request).Success && calls == 1, "unchanged upload validated or used the network");
            f.Local("mod", "changed"); f.Request.BeforeCloudWrite = () => false;
            Check(!service.UploadIfChanged(f.Request).Success, "changed mapping uploaded");
        });
        Test("failed ZIP upload leaves baseline retryable", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("mod", "bytes");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "fail-copyto-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/sh\n[ \"$1\" = copyto ] && exit 77\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var service = new ModZipUploadService();
            Check(!service.UploadFirst(f.Request).Success && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "upload-baseline.txt")), "failed upload advanced baseline");
            f.Request.RcloneExecutable = real;
            var retried = service.UploadFirst(f.Request);
            Check(retried.Success && File.Exists(CloudZipPath(f)), retried.Message);
        });
        Test("successful install restore seed avoids unchanged re-upload", f => {
            f.Local("mod", "restored bytes");
            Directory.CreateDirectory(Path.GetDirectoryName(CloudZipPath(f)));
            System.IO.Compression.ZipFile.CreateFromDirectory(f.LocalRoot, CloudZipPath(f));
            new ModZipUploadService().SeedBaseline(f.Request);
            if (OperatingSystem.IsLinux()) {
                var wrapper = System.IO.Path.Combine(f.Root, "no-network-rclone.sh");
                File.WriteAllText(wrapper, "#!/bin/sh\nexit 88\n");
                File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                f.Request.RcloneExecutable = wrapper;
            }
            var result = new ModZipUploadService().UploadIfChanged(f.Request);
            Check(result.Success && result.Message.Contains("unchanged"), "restored bytes were uploaded instead of seeded: " + result.Message);
        });
        Test("ZIP upload refuses a mod folder under a linked ancestor", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("nested/mod", "bytes");
            var link = System.IO.Path.Combine(f.Root, "linked-parent");
            Directory.CreateSymbolicLink(link, f.LocalRoot);
            f.Request.LocalFolder = System.IO.Path.Combine(link, "nested");
            var result = new ModZipUploadService().UploadFirst(f.Request);
            Check(!result.Success && !File.Exists(CloudZipPath(f)), "linked ancestor was followed during upload");
        });
        Test("mod edits during upload stop pre-uninstall backup", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("mod", "older bytes");
            var service = new ModZipUploadService();
            Check(service.UploadFirst(f.Request).Success, "could not initialize the backup");
            f.Local("mod", "edited bytes");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "edit-during-upload.sh");
            File.WriteAllText(wrapper, "#!/bin/sh\n\"" + real + "\" \"$@\" || exit $?\n" +
                "if [ \"$1\" = copyto ]; then printf 'newer bytes' > \"" + f.Path("mod") + "\"; fi\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var result = service.UploadIfChanged(f.Request, beforeUninstall: true);
            Check(!result.Success && File.ReadAllText(f.Path("mod")) == "newer bytes", "new edits were cleared for uninstall");
            f.Request.RcloneExecutable = real;
            Check(service.UploadIfChanged(f.Request, beforeUninstall: true).Success, "retry did not publish newer mods");
            using var archive = System.IO.Compression.ZipFile.OpenRead(CloudZipPath(f));
            using var reader = new StreamReader(archive.GetEntry("mod").Open());
            Check(reader.ReadToEnd() == "newer bytes", "retry ZIP did not contain newer mods");
        });
        Test("unknown existing cloud ZIP is preserved", f => {
            f.Local("mod", "local"); File.WriteAllText(CloudZipPath(f), "unknown cloud data");
            var result = new ModZipUploadService().UploadFirst(f.Request);
            Check(!result.Success && File.ReadAllText(CloudZipPath(f)) == "unknown cloud data", "untracked cloud ZIP was overwritten");
        });
        Test("automatic backup cannot bypass consent for a missing first ZIP", f => {
            f.Local("mod", "keep local");
            var service = new ModZipUploadService();
            Check(service.UploadIfChanged(f.Request).Success && !File.Exists(CloudZipPath(f)), "interval uploaded without consent");
            Check(!service.UploadIfChanged(f.Request, beforeUninstall: true).Success && !File.Exists(CloudZipPath(f)), "uninstall uploaded or discarded mods without consent");
            Check(service.UploadFirst(f.Request).Success, "explicit upload failed");
            File.Delete(CloudZipPath(f));
            f.Local("mod", "newer local data");
            Check(!service.UploadIfChanged(f.Request).Success && !File.Exists(CloudZipPath(f)), "an old baseline recreated a missing ZIP without consent");
            Check(service.UploadFirst(f.Request).Success && File.Exists(CloudZipPath(f)), "explicit upload did not recreate the ZIP");
        });
        Test("empty initial folder never uploads an empty ZIP", f => {
            var service = new ModZipUploadService();
            Check(!service.UploadFirst(f.Request).Success && !File.Exists(CloudZipPath(f)), "empty folder uploaded");
        });
        foreach (var localExists in new[] { false, true })
        Test("uninstall does not need a backup for a " + (localExists ? "file-free folder" : "missing mod folder"), f => {
            if (localExists) Directory.CreateDirectory(f.Path("empty-subfolder"));
            else Directory.Delete(f.LocalRoot);
            var zipPath = CloudZipPath(f);
            File.WriteAllText(zipPath, "existing cloud backup");
            f.Request.RcloneExecutable = System.IO.Path.Combine(f.Root, "rclone-must-not-run");
            f.Request.BeforeCloudWrite = () => throw new Exception("uninstall attempted remote validation");
            var service = new ModZipUploadService();
            var result = service.UploadIfChanged(f.Request, beforeUninstall: true);
            Check(result.Success && File.ReadAllText(zipPath) == "existing cloud backup", "uninstall blocked or changed the backup: " + result.Message);
            File.Delete(zipPath);
            Check(service.UploadIfChanged(f.Request, beforeUninstall: true).Success && !File.Exists(zipPath), "uninstall with no cloud backup did not skip the upload");
        });
        Test("pre-uninstall backup still refuses a file at the configured folder path", f => {
            Directory.Delete(f.LocalRoot);
            File.WriteAllText(f.LocalRoot, "keep local");
            var result = new ModZipUploadService().UploadIfChanged(f.Request, beforeUninstall: true);
            Check(!result.Success && File.ReadAllText(f.LocalRoot) == "keep local", "invalid folder was treated as absent");
        });
        Test("local addition uploads", f => {
            f.Local("keep", "keep"); f.Sync(); f.Local("new.txt", "new"); f.Sync();
            Check(File.ReadAllText(f.Remote("new.txt")) == "new", "upload missing");
        });
        Test("cloud addition downloads", f => {
            f.Local("keep", "keep"); f.Sync(); f.Cloud("new.txt", "new"); f.Sync();
            Check(File.ReadAllText(f.Path("new.txt")) == "new", "download missing");
        });
        Test("local update uploads", f => {
            f.Local("a.txt", "old"); f.Sync(); f.Local("a.txt", "new-longer"); f.Sync();
            Check(File.ReadAllText(f.Remote("a.txt")) == "new-longer", "update missing");
        });
        Test("cloud update downloads", f => {
            f.Local("a.txt", "old"); f.Sync(); f.Cloud("a.txt", "remote-new-longer"); f.Sync();
            Check(File.ReadAllText(f.Path("a.txt")) == "remote-new-longer", "update missing");
        });
        Test("local deletion removes cloud file", f => {
            f.Local("a", "a"); f.Local("b", "b"); f.Local("c", "c"); f.Sync();
            File.Delete(f.Path("a")); f.Sync(); Check(!File.Exists(f.Remote("a")), "deleted mod resurrected");
        });
        Test("cloud deletion removes local file", f => {
            f.Local("a", "a"); f.Local("b", "b"); f.Local("c", "c"); f.Sync();
            File.Delete(f.Remote("a")); f.Sync(); Check(!File.Exists(f.Path("a")), "deleted mod resurrected");
        });
        Test("clearing existing folder asks and propagates", f => {
            f.Local("a", "a"); f.Sync(); File.Delete(f.Path("a"));
            var asked = false;
            var denied = f.Service.Synchronize(f.Request, message => { asked = true; return false; });
            Check(asked && !denied.Success && File.Exists(f.Remote("a")), "empty clear did not pause");
            f.Sync(); Check(!File.Exists(f.Remote("a")), "empty clear not propagated");
        });
        Test("remote empty-clear asks before local removal", f => {
            f.Local("a", "a"); f.Sync(); File.Delete(f.Remote("a"));
            var asked = false;
            var denied = f.Service.Synchronize(f.Request, message => { asked = true; return false; });
            Check(asked && !denied.Success && File.Exists(f.Path("a")), "remote clear did not pause");
            f.Sync(); Check(!File.Exists(f.Path("a")), "remote clear not propagated");
        });
        Test("missing local folder does not erase cloud", f => {
            f.Local("a", "a"); f.Sync(); Directory.Move(f.LocalRoot, f.LocalRoot + "-uninstalled");
            Check(!f.Service.Synchronize(f.Request, _ => true).Success, "missing folder accepted");
            Check(File.Exists(f.Remote("a")), "cloud erased");
        });
        Test("recreated local folder without marker does not erase cloud", f => {
            f.Local("a", "a"); f.Sync(); Directory.Move(f.LocalRoot, f.LocalRoot + "-old"); Directory.CreateDirectory(f.LocalRoot);
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && File.Exists(f.Remote("a")), "missing marker accepted");
        });
        Test("missing launch file refuses synchronization", f => {
            f.Local("a", "a"); f.Sync(); File.Delete(f.Request.LaunchFile); File.Delete(f.Path("a"));
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && File.Exists(f.Remote("a")), "uninstalled game erased cloud");
        });
        Test("missing cloud access marker stops synchronization", f => {
            f.Local("a", "a"); f.Sync();
            foreach (var p in Directory.GetFiles(f.RemoteRoot, ".pcls-*")) File.Delete(p);
            f.Local("new", "new");
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && !File.Exists(f.Remote("new")), "missing remote marker accepted");
        });
        Test("initial conflicting file is refused without overwrite", f => {
            f.Local("same", "local-value"); f.Cloud("same", "remote-value");
            var result = f.Service.Synchronize(f.Request, _ => true);
            Check(!result.Success && File.ReadAllText(f.Path("same")) == "local-value" && File.ReadAllText(f.Remote("same")) == "remote-value", "initial conflict overwrote data");
        });
        Test("normal conflict keeps both variants and reports it", f => {
            f.Local("same", "original"); f.Sync(); f.Local("same", "local-variant"); f.Cloud("same", "remote-variant-long");
            var result = f.Sync();
            var texts = Directory.GetFiles(f.LocalRoot).Select(File.ReadAllText).ToArray();
            Check(result.HasConflicts && texts.Contains("local-variant") && texts.Contains("remote-variant-long"), "conflict data lost or unreported");
        });
        Test("changed mapping is refused rather than silently reinitialized", f => {
            f.Local("a", "a"); f.Sync(); f.Request.RemoteFolder += "-different";
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && !Directory.Exists(f.RemoteRoot + "-different"), "mapping changed silently");
        });
        Test("initial linkage requires confirmation", f => {
            f.Local("a", "a"); var declined = f.Service.Synchronize(f.Request, _ => false);
            Check(!declined.Success && declined.Cancelled && !File.Exists(f.Remote("a")), "declined initial link was not distinct or wrote data");
        });
        Test("sync state may not live inside mod folder", f => {
            f.Request.StateDirectory = System.IO.Path.Combine(f.LocalRoot, "state");
            Check(!f.Service.Synchronize(f.Request, _ => true).Success, "overlapping state allowed");
        });
        Test("local mod folder may not include game launch file", f => {
            f.Request.LocalFolder = System.IO.Path.GetDirectoryName(f.Request.LaunchFile);
            Check(!f.Service.Synchronize(f.Request, _ => true).Success, "base game directory allowed");
        });
        Test("empty initial mod folders can be linked", f => { f.Sync(); });
        Test("empty-side synchronization conservatively asks even for a new addition", f => {
            f.Sync(); f.Local("first", "first");
            var asked = false;
            var result = f.Service.Synchronize(f.Request, _ => { asked = true; return false; });
            Check(asked && !result.Success && !File.Exists(f.Remote("first")), "empty-side guard bypassed");
            f.Sync(); Check(File.Exists(f.Remote("first")), "approved addition missing");
        });
        Test("stale persisted count cannot bypass whole-clear confirmation", f => {
            f.Sync(); f.Local("a", "a"); f.Sync();
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "last-file-count"), "0");
            File.Delete(f.Path("a")); var asked = false;
            var result = f.Service.Synchronize(f.Request, _ => { asked = true; return false; });
            Check(asked && !result.Success && File.Exists(f.Remote("a")), "stale count bypassed confirmation");
        });
        Test("initial pairing refuses Windows case-only overlaps without creating a baseline", f => {
            f.Local("mod.bin", "local-copy"); f.Cloud("MOD.BIN", "cloud-copy");
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(!result.Success && File.ReadAllText(f.Path("mod.bin")) == "local-copy" && File.ReadAllText(f.Remote("MOD.BIN")) == "cloud-copy", "case-ambiguous initial files were merged");
            Check(!File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt")), "ambiguous pairing reached initialization");
        });
        Test("Unix fixture refuses cloud names colliding on a Windows destination", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Cloud("mod.bin", "first"); f.Cloud("MOD.BIN", "second");
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(!result.Success && !File.Exists(f.Path("mod.bin")) && !File.Exists(f.Path("MOD.BIN")), "case-colliding cloud files were copied");
            Check(!File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt")), "cloud collision reached initialization");
        });
        Test("Unix content fallback also refuses Windows case-only overlaps", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("mod.bin", "local-copy"); f.Cloud("MOD.BIN", "cloud-copy");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "listing-unavailable.sh");
            File.WriteAllText(wrapper, "#!/bin/sh\nif [ \"$1\" = lsjson ]; then exit 1; fi\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(!result.Success && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt")), "content fallback bypassed Windows name-collision guard");
        });
        Test("native per-object hashes verify same-size same-time initial content", f => {
            f.Local("mod", "aaaa"); f.Cloud("mod", "bbbb");
            var stamp = File.GetLastWriteTimeUtc(f.Path("mod")); File.SetLastWriteTimeUtc(f.Remote("mod"), stamp);
            var result = f.Service.Synchronize(f.Request, _ => true);
            var log = File.ReadAllText(System.IO.Path.Combine(f.Request.StateDirectory, "diagnostics.log"));
            Check(!result.Success && File.ReadAllText(f.Path("mod")) == "aaaa" && File.ReadAllText(f.Remote("mod")) == "bbbb", "equal metadata content conflict was overwritten");
            Check(log.Contains("mode=native-per-object"), "usable native hash path was not recorded");
        });
        Test("mixed native hashes fall back to actual content checks", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("mod", "aaaa"); f.Cloud("mod", "bbbb");
            f.Local("other", "identical"); f.Cloud("other", "identical");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "no-hash-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/bash\nif [ \"$1\" = lsjson ]; then shift; \"" + real + "\" lsjson \"$@\" | sed -E '0,/\"Hashes\":\\{[^}]*\\}/s//\"Hashes\":{}/'; exit ${PIPESTATUS[0]}; fi\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var result = f.Service.Synchronize(f.Request, _ => true);
            var log = File.ReadAllText(System.IO.Path.Combine(f.Request.StateDirectory, "diagnostics.log"));
            Check(!result.Success && File.ReadAllText(f.Path("mod")) == "aaaa" && File.ReadAllText(f.Remote("mod")) == "bbbb", "fallback content conflict was overwritten");
            Check(log.Contains("mode=download-fallback phase=content-check"), "missing-hash fallback was not recorded");
        });
        Test("same-size same-time local edits are detected by checksum", f => {
            f.Local("mod", "aaaa"); f.Sync(); var stamp = File.GetLastWriteTimeUtc(f.Path("mod"));
            File.WriteAllText(f.Path("mod"), "bbbb"); File.SetLastWriteTimeUtc(f.Path("mod"), stamp); f.Sync();
            Check(File.ReadAllText(f.Remote("mod")) == "bbbb", "same-metadata local edit missed");
        });
        Test("same-size same-time cloud edits are detected by checksum", f => {
            f.Local("mod", "aaaa"); f.Sync(); var stamp = File.GetLastWriteTimeUtc(f.Remote("mod"));
            File.WriteAllText(f.Remote("mod"), "bbbb"); File.SetLastWriteTimeUtc(f.Remote("mod"), stamp); f.Sync();
            Check(File.ReadAllText(f.Path("mod")) == "bbbb", "same-metadata cloud edit missed");
        });
        Test("fresh mapping lookup failure pauses before sync", f => {
            f.Local("a", "a"); var blocked = false;
            try { ModSyncService.RequireCurrentMapping(f.Request, () => throw new IOException("manifest offline")); }
            catch (IOException) { blocked = true; }
            Check(blocked && !File.Exists(f.Remote("a")), "offline manifest allowed stale sync");
        });
        Test("changed live mod mapping is refused", f => {
            var changed = new ModSyncRequest { GameId = f.Request.GameId, LaunchFile = f.Request.LaunchFile,
                LocalFolder = f.LocalRoot + "-new", RemoteFolder = f.Request.RemoteFolder };
            var blocked = false;
            try { ModSyncService.RequireCurrentMapping(f.Request, () => changed); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "changed live mapping accepted");
        });
        Test("unchanged freshly loaded mapping is accepted", f => {
            Check(ModSyncService.RequireCurrentMapping(f.Request, () => f.Request) == f.Request, "fresh mapping refused");
        });
        Test("pause after uninstall prevents sync even if mod folder still exists", f => {
            f.Local("a", "a"); f.Sync(); f.Service.Pause(f.Request.StateDirectory); File.Delete(f.Path("a"));
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && File.Exists(f.Remote("a")), "paused sync erased cloud");
        });
        Test("install restore creates a new missing mod folder without deleting cloud mods", f => {
            f.Cloud("cloud.txt", "cloud"); Directory.Delete(f.LocalRoot, true);
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(result.Success && File.ReadAllText(f.Path("cloud.txt")) == "cloud" && File.Exists(f.Remote("cloud.txt")), result.Message);
            Check(File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "new install did not establish a real baseline");
        });
        Test("Unix install restore refuses linked ancestors before creating directories", f => {
            if (!OperatingSystem.IsLinux()) return;
            var external = System.IO.Path.Combine(f.Root, "external"); Directory.CreateDirectory(external);
            var link = System.IO.Path.Combine(f.Root, "linked"); Directory.CreateSymbolicLink(link, external);
            f.Request.LocalFolder = System.IO.Path.Combine(link, "new-mods");
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(!result.Success && !Directory.Exists(System.IO.Path.Combine(external, "new-mods")), "install restore followed an ancestor link while creating mods");
        });
        Test("install restore merges cloud and local mods then establishes ready baseline", f => {
            f.Cloud("cloud.txt", "cloud"); f.Local("installed.txt", "installed");
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(result.Success && File.ReadAllText(f.Path("cloud.txt")) == "cloud" && File.ReadAllText(f.Remote("installed.txt")) == "installed", result.Message);
            Check(File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "ready baseline missing");
            var phases = new System.Collections.Generic.List<string>(); f.Request.Progress = p => phases.Add(p.Phase);
            var second = f.Service.Synchronize(f.Request, _ => throw new Exception("ready sync prompted"));
            Check(second.Success && !phases.Contains("check"), "normal sync repeated initial comparison");
        });
        Test("install restore archives exact completed pairing and preserves cloud deletion intent", f => {
            f.Local("kept", "kept"); f.Local("deleted", "deleted"); f.Sync();
            f.Service.Pause(f.Request.StateDirectory); Directory.Move(f.LocalRoot, f.LocalRoot + "-old");
            Directory.CreateDirectory(f.LocalRoot); f.Local("fresh", "fresh");
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(result.Success && File.Exists(f.Path("kept")) && File.Exists(f.Path("deleted")) && File.Exists(f.Remote("fresh")), result.Message);
            Check(Directory.GetDirectories(f.Root, "state.archive-*").Length == 1, "completed state not archived");
            Check(File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "new baseline missing");
        });
        Test("install restore refuses differing local and cloud files", f => {
            f.Local("same", "local"); f.Cloud("same", "remote");
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(!result.Success && File.ReadAllText(f.Path("same")) == "local" && File.ReadAllText(f.Remote("same")) == "remote", "restore overwrote conflict");
        });
        Test("install restore does not reset interrupted initial pairing", f => {
            Directory.CreateDirectory(f.Request.StateDirectory);
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt"), f.LocalRoot + "\\n" + f.Request.RemoteFolder);
            f.Local("new", "new");
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(!result.Success && result.RequiresRelink && !File.Exists(f.Remote("new")), "interrupted pair was reset");
        });
        Test("install restore refuses mismatched state identity without archiving it", f => {
            f.Local("existing", "existing"); f.Sync(); f.Service.Pause(f.Request.StateDirectory);
            f.Request.RemoteFolder += "-changed";
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(!result.Success && Directory.GetDirectories(f.Root, "state.archive-*").Length == 0 && File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "mismatched pairing was archived or altered");
        });
        Test("install restore validates caller configuration", f => {
            f.Local("local", "local"); f.Request.RemoteFolder = " ";
            var result = f.Service.RestoreAfterInstall(f.Request);
            Check(!result.Success && !Directory.Exists(f.Request.StateDirectory), "unconfigured caller changed state");
        });
        Test("install restore honors cancellation and retains retryable state", f => {
            f.Local("new", "new");
            using (var cts = new CancellationTokenSource()) {
                cts.Cancel(); f.Request.CancellationToken = cts.Token;
                var result = f.Service.RestoreAfterInstall(f.Request);
                Check(result.Cancelled && !result.Success && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "cancel altered baseline");
            }
            f.Request.CancellationToken = CancellationToken.None;
            Check(f.Service.RestoreAfterInstall(f.Request).Success, "retry after cancellation failed");
        });
        Test("install-only metadata audit lists names without hashes or transfer mutations", f => {
            if (!OperatingSystem.IsLinux()) return;
            var audit = System.IO.Path.Combine(f.Root, "install-list-commands.txt");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "install-list-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/sh\nprintf '%s\\n' \"$@\" >> \"" + audit + "\"\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var result = f.Service.DownloadAfterInstall(f.Request, _ => new ModSyncResult { Success = true });
            Check(result.Success, result.Message);
            var commands = File.ReadAllLines(audit);
            Check(commands.Contains("lsjson") && commands.Contains("--recursive") && commands.Contains("--files-only"), "metadata-only list absent");
            Check(!commands.Contains("--hash") && !commands.Contains("check") &&
                !commands.Contains("bisync") && !commands.Contains("copy") && !commands.Contains("copyto") && !commands.Contains("mkdir"),
                "install listing invoked content comparison or mutation: " + string.Join(" ", commands));
        });
        Test("install-only treats only unpaired remote directory-not-found as a successful no-op", f => {
            if (!OperatingSystem.IsLinux()) return;
            Directory.Delete(f.RemoteRoot, true);
            var wrapper = System.IO.Path.Combine(f.Root, "missing-cloud-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/sh\n[ \"$1\" = lsjson ] && exit 3\nexit 90\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var result = f.Service.DownloadAfterInstall(f.Request, _ => new ModSyncResult { Success = true });
            Check(result.Success && !Directory.Exists(f.RemoteRoot), "missing unpaired cloud path was not a no-op or remote mkdir occurred");
            Check(!File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt")) && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "no-op initialized pairing");
        });
        Test("install-only does not treat missing cloud path as empty when old pairing exists", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("tracked", "tracked");
            Directory.CreateDirectory(f.Request.StateDirectory);
            var identity = System.IO.Path.GetFullPath(f.LocalRoot) + "\n" + f.Request.RemoteFolder;
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt"), identity);
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "ready"), "ready");
            Directory.Delete(f.RemoteRoot, true);
            var wrapper = System.IO.Path.Combine(f.Root, "missing-paired-cloud-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/sh\n[ \"$1\" = lsjson ] && exit 3\nexit 90\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var result = f.Service.DownloadAfterInstall(f.Request, _ => new ModSyncResult { Success = true });
            Check(!result.Success && File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "missing paired cloud path bypassed protection");
        });
        Test("install-only download preserves cloud and local additions without creating a ready baseline", f => {
            f.Cloud("cloud.txt", "cloud"); f.Local("local.txt", "local");
            var callbackCalled = false;
            var result = f.Service.DownloadAfterInstall(f.Request, request =>
            {
                callbackCalled = true;
                var settings = new PersonalCloudLibrarySourceSettings { RcloneRemoteName = "fixture", RcloneExecutablePath = request.RcloneExecutable };
                var copy = new RcloneFileCopier().CopyMissingRemoteDirectoryToLocalPath(settings, f.RemoteRoot, request.LocalFolder,
                    request.CancellationToken, p => request.Progress?.Invoke(new ModSyncProgress { Phase = p.Phase, Bytes = p.CompletedBytes, TotalBytes = p.TotalBytes ?? 0, Percentage = p.Percentage }));
                return new ModSyncResult { Success = copy.Succeeded, Cancelled = copy.Cancelled, Message = copy.Message };
            });
            Check(callbackCalled && result.Success, result.Message);
            Check(File.Exists(f.Path("cloud.txt")) && File.ReadAllText(f.Path("local.txt")) == "local", "one-way install changed or omitted local files");
            Check(File.ReadAllText(f.Remote("cloud.txt")) == "cloud" && !File.Exists(f.Remote("local.txt")), "install changed cloud contents or uploaded local additions");
            Check(!File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")) && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt")), "install created pairing evidence");
        });
        Test("install-only download pauses completed identity before callback and preserves pair", f => {
            f.Local("kept", "kept"); f.Sync();
            var pair = File.ReadAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt"));
            var wasPaused = false;
            var result = f.Service.DownloadAfterInstall(f.Request, _ =>
            {
                wasPaused = File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "paused"));
                return new ModSyncResult { Success = false, Message = "fixture failure" };
            });
            Check(wasPaused, "pair was not paused under lock before download");
            Check(!result.Success && File.ReadAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt")) == pair, "failure reset completed pairing");
            Check(File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "failure removed ready evidence");
        });
        Test("install-only rejects mismatched identity before callback or data changes", f => {
            f.Local("kept", "kept"); f.Sync(); f.Request.RemoteFolder += "-wrong";
            var called = false;
            var result = f.Service.DownloadAfterInstall(f.Request, _ => { called = true; return new ModSyncResult { Success = true }; });
            Check(!result.Success && !called && File.Exists(f.Remote("kept")), "mismatched identity reached downloader");
        });
        Test("install-only permits safe download with incomplete identity without resetting it", f => {
            Directory.CreateDirectory(f.Request.StateDirectory);
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt"), f.LocalRoot + "\n" + f.Request.RemoteFolder);
            f.Cloud("cloud", "cloud");
            var result = f.Service.DownloadAfterInstall(f.Request, request => {
                File.WriteAllText(System.IO.Path.Combine(request.LocalFolder, "cloud"), "cloud");
                return new ModSyncResult { Success = true };
            });
            Check(result.Success && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "incomplete state was promoted");
            Check(File.ReadAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt")) == f.LocalRoot + "\n" + f.Request.RemoteFolder, "incomplete identity changed");
        });
        Test("install-only failed and cancelled callbacks retain retryable state", f => {
            var failure = f.Service.DownloadAfterInstall(f.Request, _ => new ModSyncResult { Success = false, Message = "retry" });
            Check(!failure.Success && !failure.Cancelled && failure.Message == "retry" && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")), "failure result/state incorrect: success=" + failure.Success + " cancelled=" + failure.Cancelled + " message=" + failure.Message + " ready=" + File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")));
            using (var cts = new CancellationTokenSource()) {
                f.Request.CancellationToken = cts.Token;
                var cancelled = f.Service.DownloadAfterInstall(f.Request, _ => { cts.Cancel(); return new ModSyncResult { Success = false, Cancelled = true }; });
                Check(cancelled.Cancelled && !cancelled.Success, "cancelled callback not reported");
            }
        });
        Test("install-only rejects a Windows case alias before callback", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Cloud("MOD.BIN", "cloud"); f.Local("mod.bin", "local");
            var result = f.Service.DownloadAfterInstall(f.Request, _ => new ModSyncResult { Success = true });
            Check(!result.Success && File.ReadAllText(f.Path("mod.bin")) == "local", "case alias changed original local file");
        });
        Test("composed install inventory overlaps native staged transfer and preserves modified originals", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("same.bin", "user-modified-original"); f.Cloud("same.bin", "remote-version-different-size");
            f.Cloud("fresh.bin", new string('x', 4 * 1024 * 1024));
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var listingDone = System.IO.Path.Combine(f.Root, "listing-done");
            var wrapper = System.IO.Path.Combine(f.Root, "delayed-inventory-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/bash\nif [ \"$1\" = lsjson ]; then\n  out=\"" + f.Root + "/inventory.json\"\n  \"" + real + "\" \"$@\" > \"$out\" || exit $?\n  sleep 3\n  cat \"$out\"\n  touch \"" + listingDone + "\"\n  exit 0\nfi\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var sawStagedBytesBeforeListing = false;
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var serviceTask = Task.Run(() => f.Service.DownloadAfterInstall(f.Request, request => {
                var settings = new PersonalCloudLibrarySourceSettings { RcloneRemoteName = "fixture", RcloneExecutablePath = request.RcloneExecutable };
                var copy = new RcloneFileCopier().CopyMissingRemoteDirectoryToLocalPath(settings, f.RemoteRoot, request.LocalFolder,
                    request.DownloadCompareDirectory, request.CancellationToken, _ => { });
                return new ModSyncResult { Success = copy.Succeeded, Cancelled = copy.Cancelled, Message = copy.Message };
            }));
            var parent = System.IO.Path.GetDirectoryName(f.LocalRoot);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && !serviceTask.IsCompleted && !File.Exists(listingDone)) {
                var staged = Directory.Exists(parent) ? Directory.GetDirectories(parent, "Mods.pcls-stage-*") : new string[0];
                if (staged.SelectMany(path => Directory.GetFiles(path, "fresh.bin", SearchOption.AllDirectories)).Any(path => new FileInfo(path).Length > 0))
                    sawStagedBytesBeforeListing = true;
                Check(File.ReadAllText(f.Path("same.bin")) == "user-modified-original", "original changed while full listing was pending");
                Thread.Sleep(25);
            }
            var result = serviceTask.GetAwaiter().GetResult();
            Check(sawStagedBytesBeforeListing, "native staged bytes did not arrive before the delayed complete lsjson inventory");
            Check(Process.GetCurrentProcess().TotalProcessorTime - cpuBefore < TimeSpan.FromSeconds(1.5),
                "completed fan-in task caused CPU busy-wait during the delayed inventory");
            Check(result.Success, result.Message);
            Check(File.ReadAllText(f.Path("same.bin")) == "user-modified-original", "modified original was downloaded or overwritten");
            Check(File.ReadAllText(f.Path("fresh.bin")) == new string('x', 4 * 1024 * 1024), "validated missing file was not committed");
        });
        Test("late case collision rejects composed staged bytes without changing any original user file", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("mod.bin", "original-case"); f.Local("keep.bin", "keep-original");
            f.Cloud("MOD.BIN", "case-alias"); f.Cloud("new.bin", "must-not-commit");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "delayed-collision-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/bash\nif [ \"$1\" = lsjson ]; then\n  out=\"" + f.Root + "/inventory.json\"\n  \"" + real + "\" \"$@\" > \"$out\" || exit $?\n  sleep 1\n  cat \"$out\"\n  exit 0\nfi\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var result = f.Service.DownloadAfterInstall(f.Request, request => {
                var settings = new PersonalCloudLibrarySourceSettings { RcloneRemoteName = "fixture", RcloneExecutablePath = request.RcloneExecutable };
                var copy = new RcloneFileCopier().CopyMissingRemoteDirectoryToLocalPath(settings, f.RemoteRoot,
                    request.LocalFolder, request.DownloadCompareDirectory, request.CancellationToken, _ => { });
                return new ModSyncResult { Success = copy.Succeeded, Cancelled = copy.Cancelled, Message = copy.Message };
            });
            Check(!result.Success, "late case collision was accepted");
            Check(File.ReadAllText(f.Path("mod.bin")) == "original-case" && File.ReadAllText(f.Path("keep.bin")) == "keep-original" &&
                !File.Exists(f.Path("new.bin")) && !File.Exists(f.Path("MOD.BIN")), "ambiguous inventory changed original user data");
            Check(Directory.GetDirectories(System.IO.Path.GetDirectoryName(f.LocalRoot), "Mods.pcls-stage-*.quarantine-*").Length == 1,
                "ambiguous stage was not quarantined");
        });
        Test("cancelled partial native staging remains retryable without touching originals", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Local("keep.bin", "user-data"); f.Cloud("large.bin", new string('z', 2 * 1024 * 1024));
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "slow-stage-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/bash\nif [ \"$1\" = copy ]; then exec \"" + real + "\" \"$@\" --bwlimit 64k; fi\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            using (var cts = new CancellationTokenSource()) {
                f.Request.CancellationToken = cts.Token;
                var pending = Task.Run(() => f.Service.DownloadAfterInstall(f.Request, request => {
                    var settings = new PersonalCloudLibrarySourceSettings { RcloneRemoteName = "fixture", RcloneExecutablePath = request.RcloneExecutable };
                    var copy = new RcloneFileCopier().CopyMissingRemoteDirectoryToLocalPath(settings, f.RemoteRoot,
                        request.LocalFolder, request.DownloadCompareDirectory, request.CancellationToken, _ => { });
                    return new ModSyncResult { Success = copy.Succeeded, Cancelled = copy.Cancelled, Message = copy.Message };
                }));
                var stageRoot = System.IO.Path.GetDirectoryName(f.LocalRoot);
                var until = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < until && !pending.IsCompleted) {
                    var staged = Directory.GetDirectories(stageRoot, "Mods.pcls-stage-*")
                        .SelectMany(path => Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                        .Where(path => !path.EndsWith(".pcls-stage-owner", StringComparison.Ordinal)).FirstOrDefault(path => new FileInfo(path).Length > 0);
                    if (staged != null) { cts.Cancel(); break; }
                    Thread.Sleep(25);
                }
                var cancelled = pending.GetAwaiter().GetResult();
                Check(cancelled.Cancelled && !cancelled.Success, "real staged transfer did not report cancellation");
                Check(File.ReadAllText(f.Path("keep.bin")) == "user-data" && !File.Exists(f.Path("large.bin")), "cancelled stage changed user files");
            }
            f.Request.CancellationToken = CancellationToken.None;
            f.Request.RcloneExecutable = real;
            var retried = f.Service.DownloadAfterInstall(f.Request, request => {
                var settings = new PersonalCloudLibrarySourceSettings { RcloneRemoteName = "fixture", RcloneExecutablePath = request.RcloneExecutable };
                var copy = new RcloneFileCopier().CopyMissingRemoteDirectoryToLocalPath(settings, f.RemoteRoot,
                    request.LocalFolder, request.DownloadCompareDirectory, request.CancellationToken, _ => { });
                return new ModSyncResult { Success = copy.Succeeded, Cancelled = copy.Cancelled, Message = copy.Message };
            });
            Check(retried.Success && File.ReadAllText(f.Path("large.bin")) == new string('z', 2 * 1024 * 1024), "retry did not repair/reuse the retained owned stage: " + retried.Message);
            Check(File.ReadAllText(f.Path("keep.bin")) == "user-data", "retry altered original user data");
        });
        Test("install staging rejects an unowned pre-existing sibling directory", f => {
            var local = System.IO.Path.GetFullPath(f.LocalRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var stage = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(local), System.IO.Path.GetFileName(local) + ".pcls-stage-" +
                ModSyncService.Key(f.Request.GameId + "\n" + local + "\n" + f.Request.RemoteFolder).Substring(0, 24));
            Directory.CreateDirectory(stage); File.WriteAllText(System.IO.Path.Combine(stage, "unowned.txt"), "do not adopt");
            var called = false;
            var result = f.Service.DownloadAfterInstall(f.Request, _ => { called = true; return new ModSyncResult { Success = true }; });
            Check(!result.Success && !called && File.ReadAllText(System.IO.Path.Combine(stage, "unowned.txt")) == "do not adopt",
                "unowned stage was adopted or altered");
        });
        Test("native metadata size mismatch quarantines stage without committing", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Cloud("fresh.bin", "native-bytes");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = System.IO.Path.Combine(f.Root, "wrong-size-inventory-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/bash\nif [ \"$1\" = lsjson ]; then\n  out=\"" + f.Root + "/inventory.json\"\n  \"" + real + "\" \"$@\" > \"$out\" || exit $?\n  sed -E 's/\"Size\"[[:space:]]*:[[:space:]]*[0-9]+/\"Size\":99999999/g' \"$out\"\n  exit 0\nfi\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Request.RcloneExecutable = wrapper;
            var result = f.Service.DownloadAfterInstall(f.Request, request => {
                var settings = new PersonalCloudLibrarySourceSettings { RcloneRemoteName = "fixture", RcloneExecutablePath = request.RcloneExecutable };
                var copy = new RcloneFileCopier().CopyMissingRemoteDirectoryToLocalPath(settings, f.RemoteRoot,
                    request.LocalFolder, request.DownloadCompareDirectory, request.CancellationToken, _ => { });
                return new ModSyncResult { Success = copy.Succeeded, Cancelled = copy.Cancelled, Message = copy.Message };
            });
            Check(!result.Success && !File.Exists(f.Path("fresh.bin")), "size-mismatched staged bytes were committed");
            Check(Directory.GetDirectories(System.IO.Path.GetDirectoryName(f.LocalRoot), "Mods.pcls-stage-*.quarantine-*").Length == 1,
                "size-mismatched stage was not quarantined");
        });
        Test("install-only refuses successful copier output missing an inventoried file", f => {
            f.Cloud("missing.bin", "required bytes");
            var result = f.Service.DownloadAfterInstall(f.Request, _ => new ModSyncResult { Success = true });
            Check(!result.Success && !File.Exists(f.Path("missing.bin")), "missing required staged file was reported as installed");
        });
        Test("install-only rejects literal backslashes without repairing the remote path", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Cloud("bad\\name.bin", "unsafe filename");
            var result = f.Service.DownloadAfterInstall(f.Request, _ => new ModSyncResult { Success = true });
            Check(!result.Success && result.Message.Contains("unsafe Windows"), "unsafe remote name was repaired instead of refused: " + result.Message);
        });
        Test("install-only revalidates a destination replaced by a link during download", f => {
            if (!OperatingSystem.IsLinux()) return;
            f.Cloud("fresh.bin", "outside must stay untouched");
            var external = System.IO.Path.Combine(f.Root, "external-after-start"); Directory.CreateDirectory(external);
            var result = f.Service.DownloadAfterInstall(f.Request, request => {
                var settings = new PersonalCloudLibrarySourceSettings { RcloneRemoteName = "fixture", RcloneExecutablePath = request.RcloneExecutable };
                var copy = new RcloneFileCopier().CopyMissingRemoteDirectoryToLocalPath(settings, f.RemoteRoot,
                    request.LocalFolder, request.DownloadCompareDirectory, request.CancellationToken, _ => { });
                Directory.Move(f.LocalRoot, f.LocalRoot + "-retained");
                Directory.CreateSymbolicLink(f.LocalRoot, external);
                return new ModSyncResult { Success = copy.Succeeded, Message = copy.Message };
            });
            Check(!result.Success && !File.Exists(System.IO.Path.Combine(external, "fresh.bin")), "late destination link redirected the staged merge");
        });
        Test("install-only validates launch and rejects linked ancestors before mkdir", f => {
            if (!OperatingSystem.IsLinux()) return;
            var external = System.IO.Path.Combine(f.Root, "external-install"); Directory.CreateDirectory(external);
            var link = System.IO.Path.Combine(f.Root, "linked-install"); Directory.CreateSymbolicLink(link, external);
            f.Request.LocalFolder = System.IO.Path.Combine(link, "new-mods");
            var result = f.Service.DownloadAfterInstall(f.Request, _ => new ModSyncResult { Success = true });
            Check(!result.Success && !Directory.Exists(System.IO.Path.Combine(external, "new-mods")), "linked ancestor was followed");
        });
        Test("explicit relink restores cloud mods after reinstall", f => {
            f.Local("a", "a"); f.Sync(); f.Service.Pause(f.Request.StateDirectory);
            Directory.Move(f.LocalRoot, f.LocalRoot + "-old");
            var result = f.Service.Relink(f.Request, _ => true);
            Check(result.Success && File.ReadAllText(f.Path("a")) == "a" && File.Exists(f.Remote("a")), result.Message);
            Check(Directory.GetDirectories(f.Root, "state.archive-*").Length == 1, "old state not archived");
        });
        Test("declined relink retains paused state", f => {
            f.Local("a", "a"); f.Sync(); f.Service.Pause(f.Request.StateDirectory);
            Check(!f.Service.Relink(f.Request, _ => false).Success && File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "paused")), "declined reset changed state");
        });
        Test("interrupted initial baseline is not automatically resynced", f => {
            Directory.CreateDirectory(f.Request.StateDirectory);
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt"), f.Request.LocalFolder + "\n" + f.Request.RemoteFolder);
            f.Local("a", "a");
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && !File.Exists(f.Remote("a")), "pending initial state accepted");
        });
        Test("concurrent sync operation is refused", f => {
            f.Local("a", "a"); f.Sync();
            using (new FileStream(f.Request.StateDirectory + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!f.Service.Synchronize(f.Request, _ => true).Success, "second operation accepted");
        });
        Test("sequential PCs propagate additions and deletions without resurrection", f => {
            f.Local("a", "a"); f.Local("b", "b"); f.Local("c", "c"); f.Sync();
            using (var other = new Fixture())
            {
                other.Request.RemoteFolder = f.Request.RemoteFolder;
                other.Sync(); Check(File.Exists(other.Path("a")), "second PC did not download");
                File.Delete(f.Path("a")); f.Sync(); other.Sync();
                Check(!File.Exists(other.Path("a")), "second PC resurrected deleted mod");
                other.Local("pc-two", "from pc two"); other.Sync(); f.Sync();
                Check(File.ReadAllText(f.Path("pc-two")) == "from pc two", "second PC addition not propagated");
            }
        });
        if (Environment.OSVersion.Platform == PlatformID.Unix)
        Test("symlinks in a mod folder are refused", f => {
            f.Local("a", "a");
            File.CreateSymbolicLink(f.Path("link"), f.Request.LaunchFile);
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && !File.Exists(f.Remote("a")), "link followed");
        });
        Test("invalid mod-state location does not create diagnostic files", f => {
            f.Request.StateDirectory = System.IO.Path.Combine(f.LocalRoot, "rejected-state");
            var result = f.Service.Synchronize(f.Request, _ => true);
            Check(!result.Success && !Directory.Exists(f.Request.StateDirectory), "invalid configuration wrote diagnostics inside mods");
        });
        Test("diagnostics rotate without touching pairing state and record phase duration", f => {
            f.Local("a", "a"); Directory.CreateDirectory(f.Request.StateDirectory);
            var path = System.IO.Path.Combine(f.Request.StateDirectory, "diagnostics.log");
            File.WriteAllText(path, new string('x', 5 * 1024 * 1024));
            f.Sync();
            Check(File.Exists(path + ".previous") && new FileInfo(path).Length < 1024 * 1024,
                "diagnostics were not bounded");
            Check(File.ReadAllText(path).Contains("elapsed_ms=") && File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "ready")),
                "phase duration or ready state missing");
        });
        Test("real throttled rclone can be cancelled during transfer", f => {
            var source = System.IO.Path.Combine(f.Root, "cancel-source.bin");
            var destination = System.IO.Path.Combine(f.Root, "cancel-destination.bin");
            File.WriteAllBytes(source, new byte[1024 * 1024]);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4))) {
                var transferred = false;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = new ModSyncProcess().Run(f.Request.RcloneExecutable, 30, cts.Token,
                    progress => { if (progress.Bytes > 0) { transferred = true; cts.Cancel(); } },
                    "copyto", source, destination, "--bwlimit", "64k", "--use-json-log", "--stats", "100ms", "--stats-log-level", "NOTICE");
                Check(transferred && result.Cancelled && watch.Elapsed < TimeSpan.FromSeconds(5),
                    "real rclone was not cancelled promptly after reporting transfer progress");
            }
        });
        Test("pre-cancelled sync does not start rclone or create pairing", f => {
            f.Local("a", "a");
            using (var cts = new CancellationTokenSource()) {
            cts.Cancel(); f.Request.CancellationToken = cts.Token;
            var result = f.Service.Synchronize(f.Request, _ => true);
            Check(result.Cancelled && !result.Success && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt")), "pre-cancel was not distinct or changed state");
            }
        });
        Test("cancelled initial check can be explicitly retried without stale lock", f => {
            f.Local("kept", "local");
            using (var cts = new CancellationTokenSource()) {
                f.Request.CancellationToken = cts.Token;
                f.Request.Progress = p => { if (p.Phase == "check") cts.Cancel(); };
                var cancelled = f.Service.Synchronize(f.Request, _ => true);
                Check(cancelled.Cancelled && !cancelled.Success && !File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "paused")), "operation cancellation was not distinct");
            }
            f.Request.CancellationToken = CancellationToken.None; f.Request.Progress = null;
            var retried = f.Service.Synchronize(f.Request, _ => true);
            Check(retried.Success && File.Exists(f.Remote("kept")), "explicit retry failed after cancellation");
        });
        Test("runner cancellation kills and drains a labelled shell fixture", f => {
            if (!OperatingSystem.IsLinux()) return;
            var fixture = System.IO.Path.Combine(f.Root, "slow-rclone-fixture.sh");
            File.WriteAllText(fixture, "#!/bin/sh\n echo '{\"stats\":{\"bytes\":7,\"totalBytes\":10,\"checks\":1,\"totalChecks\":2,\"speed\":3}}' >&2\n sleep 30\n");
            File.SetUnixFileMode(fixture, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (var cts = new CancellationTokenSource()) {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                ModSyncProgress observed = null;
                var task = System.Threading.Tasks.Task.Run(() => new ModSyncProcess().Run(fixture, 60, cts.Token, p => observed = p, "fixture"));
                Thread.Sleep(300); cts.Cancel();
                var result = task.GetAwaiter().GetResult();
                Check(result.Cancelled && watch.Elapsed < TimeSpan.FromSeconds(5), "runner did not cancel promptly");
                Check(observed != null && observed.Phase == "fixture" && observed.Bytes == 7 && observed.TotalBytes == 10 && observed.Checks == 1 && observed.TotalChecks == 2 && observed.BytesPerSecond == 3, "root stats were not parsed accurately");
            }
        });
        Test("runner kills process when progress callback throws", f => {
            if (!OperatingSystem.IsLinux()) return;
            var fixture = System.IO.Path.Combine(f.Root, "callback-rclone-fixture.sh");
            File.WriteAllText(fixture, "#!/bin/sh\n echo '{\"stats\":{\"bytes\":7}}' >&2\n sleep 30\n");
            File.SetUnixFileMode(fixture, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var threw = false;
            try { new ModSyncProcess().Run(fixture, 60, CancellationToken.None, p => { if (p.Bytes > 0) throw new Exception("fixture callback"); }); }
            catch (Exception ex) { threw = ex.Message.Contains("callback"); }
            Check(threw, "runner swallowed callback failure");
        });
        Test("sync reports actual named phases and bounded progress", f => {
            f.Local("a", "a"); var phases = new System.Collections.Generic.List<string>();
            f.Request.Progress = p => { phases.Add(p.Phase); Check(p.Bytes >= 0 && p.TotalBytes >= 0 && p.Checks >= 0 && p.TotalChecks >= 0, "negative stats"); };
            var result = f.Service.Synchronize(f.Request, _ => true);
            Check(result.Success && !string.IsNullOrEmpty(result.DiagnosticPath) && File.Exists(result.DiagnosticPath), "diagnostic path missing");
            Check(phases.Contains("bisync") && phases.Contains("check"), "actual command phase progress missing");
        });
        Test("paused and incomplete pairing results expose explicit recovery", f => {
            Directory.CreateDirectory(f.Request.StateDirectory);
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt"), f.Request.LocalFolder + "\n" + f.Request.RemoteFolder);
            f.Local("a", "a");
            var incomplete = f.Service.Synchronize(f.Request, _ => true);
            Check(incomplete.RequiresRelink, "incomplete initial pairing did not offer relink");
            f.Service.Pause(f.Request.StateDirectory);
            var paused = f.Service.Synchronize(f.Request, _ => true);
            Check(paused.RequiresRelink, "paused result did not offer relink");
        });
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action<Fixture> test)
    {
        try { using (var f = new Fixture()) test(f); Console.WriteLine("PASS " + name); passed++; }
        catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string CloudZipPath(Fixture fixture) => fixture.RemoteRoot + ".zip";
    private sealed class Fixture : IDisposable
    {
        public readonly string Root, LocalRoot, RemoteRoot;
        public readonly ModSyncService Service = new ModSyncService();
        public readonly ModSyncRequest Request;
        private int tick;
        public Fixture()
        {
            Root = System.IO.Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? System.IO.Path.GetTempPath(), "pcls-test-" + Guid.NewGuid().ToString("N"));
            LocalRoot = System.IO.Path.Combine(Root, "game", "Mods"); RemoteRoot = System.IO.Path.Combine(Root, "cloud", "games", "mods", "Game Name");
            Directory.CreateDirectory(LocalRoot); Directory.CreateDirectory(RemoteRoot);
            var launch = System.IO.Path.Combine(Root, "game", "game.exe"); File.WriteAllText(launch, "test fixture only");
            var config = System.IO.Path.Combine(Root, "rclone.conf"); File.WriteAllText(config, "[fixture]\ntype = local\n");
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", config);
            Request = new ModSyncRequest { GameId = "fixture-game", LocalFolder = LocalRoot, RemoteFolder = ":local:" + RemoteRoot,
                LaunchFile = launch, StateDirectory = System.IO.Path.Combine(Root, "state"),
                RcloneExecutable = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone", TimeoutSeconds = 60 };
        }
        public string Path(string name) => System.IO.Path.Combine(LocalRoot, name);
        public string Remote(string name) => System.IO.Path.Combine(RemoteRoot, name);
        public void Local(string name, string content) => Write(Path(name), content);
        public void Cloud(string name, string content) => Write(Remote(name), content);
        private void Write(string p, string content)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)); File.WriteAllText(p, content);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(++tick));
        }
        public ModSyncResult Sync()
        {
            var result = Service.Synchronize(Request, _ => true);
            Check(result.Success, result.Message); return result;
        }
        public void Dispose() { /* Preserve isolated fixtures for inspecting failures. */ }
    }
}

namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibrarySourceSettings
    {
        public string RcloneExecutablePath = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
        public string RcloneRemoteName = "fixture";
        public string RcloneManifestPath = "manifest.json";
        public int RcloneTimeoutSeconds = 5, RcloneTransferInactivityTimeoutSeconds = 5;
    }
}
