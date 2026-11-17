using System;
using PersonalCloudLibrarySource;

var passed = 0;
void Check(bool value, string description) {
    if (!value) throw new Exception(description);
    Console.WriteLine("PASS " + description); passed++;
}
var operation = new ModSyncUserInteraction(false);
Check(!operation.Confirm("First link"), "confirmation is deferred until progress closes");
Check(!operation.ShouldRetry(false, true, _ => false) && operation.Declined,
      "dismissing a question cancels only this operation");
var retry = new ModSyncUserInteraction(false);
Check(!retry.Confirm("First link") && !retry.Declined, "new attempt has no permanent dismissed/locked state");
Check(retry.ShouldRetry(false, true, _ => true), "approved question retries the operation");
retry.BeginAttempt();
Check(retry.Confirm("First link"), "same operation remembers exact approval");
Check(!retry.Confirm("Delete all mods"), "one approval does not authorize a different destructive question");
Check(!retry.ShouldRetry(false, true, _ => false), "new destructive question still requires explicit consent");
var recover = new ModSyncUserInteraction(false);
Check(recover.ShouldRetry(true, true, _ => throw new Exception("unexpected UI")) && recover.Relink,
      "manual Sync can offer the existing explicit recovery flow");
Check(!recover.Confirm("Archive old pairing and merge cloud mods?"), "recovery has not been auto-approved");
Check(!recover.ShouldRetry(true, true, _ => false), "dismissed recovery cannot reset a baseline");
var automatic = new ModSyncUserInteraction(false);
Check(!automatic.ShouldRetry(true, false, _ => true) && !automatic.Relink,
      "automatic sync never enters relink/reset flow");
Check(!new ModSyncUserInteraction(true).ShouldRetry(true, true, _ => true),
      "failed recovery is not retried forever");
Check(ModSyncUserInteraction.ProgressText("Checking cloud files", 0, 0, 0, 0, 0) == "Checking cloud files",
      "unknown totals do not invent a percentage or transfer speed");
var text = ModSyncUserInteraction.ProgressText("Syncing mods", 1048576, 2097152, 12, 24, 524288);
Check(ModSyncUserInteraction.ProgressText("check", 0, 0, 0, 0, 0) == "Comparing initial mod folders",
      "real command phases have readable user-facing labels");
Check(text.Contains("1.0 / 2.0 MiB") && text.Contains("12 / 24") && text.Contains("0.5 MiB/s"),
      "progress displays actual bytes, checks and speed");
Check(!ModSyncUserInteraction.ProgressText("Checking", 0, 0, 0, 0, double.NaN).Contains("NaN"),
      "invalid speed does not pollute progress UI");
Console.WriteLine("RESULT UX: " + passed + " passed");
