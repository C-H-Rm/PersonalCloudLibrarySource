using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySource
    {
        private void RegisterInlineInstallProgress()
        {
            AddCustomElementSupport(new AddCustomElementSupportArgs {
                SourceName = "CHRCL", ElementList = new List<string> { "InstallProgress" } });
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            return args?.Name == "InstallProgress" ? new InstallProgressView(InstallProgressState.Shared, Id) : null;
        }
    }

    // Supported theme extension point, not visual-tree injection into arbitrary themes.
    public sealed class InstallProgressView : PluginUserControl
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly InstallProgressState state;
        private readonly Guid pluginId;
        private readonly TextBlock title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock details = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        private readonly ProgressBar bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 7, Margin = new Thickness(0, 5, 0, 0) };
        private readonly Button cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 5, 8, 0) };
        private readonly Button retry = new Button { Content = "Retry mods", Margin = new Thickness(0, 5, 0, 0) };
        private bool listening;

        public InstallProgressView(InstallProgressState state, Guid pluginId)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.pluginId = pluginId;
            var content = new StackPanel { MinWidth = 200, MaxWidth = 600, Margin = new Thickness(0, 0, 0, 8) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            details.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            content.Children.Add(title); content.Children.Add(details); content.Children.Add(bar);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(cancel); buttons.Children.Add(retry); content.Children.Add(buttons);
            Content = content;
            Visibility = Visibility.Collapsed;
            Loaded += (_, __) => { if (!listening) { state.Changed += StateChanged; listening = true; } Render(); };
            Unloaded += (_, __) => { if (listening) { state.Changed -= StateChanged; listening = false; } };
            cancel.Click += Cancel;
            retry.Click += (_, __) => { if (GameContext != null) state.Retry(GameContext.Id); };
        }

        public override void GameContextChanged(Game oldContext, Game newContext) { Render(); }

        private void StateChanged(object sender, InstallProgressChangedEventArgs args)
        {
            // Read GameContext on its dispatcher, not the transfer's worker thread.
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() => { if (listening && GameContext?.Id == args.GameId) Render(); }));
        }

        private async void Cancel(object sender, RoutedEventArgs args)
        {
            var game = GameContext;
            var snapshot = game == null ? null : state.Get(game.Id);
            if (game == null || snapshot == null) return;
            cancel.IsEnabled = false;
            title.Text = "Cancelling…";
            try { await Task.Run(() => state.Cancel(game.Id, snapshot.OperationId)); }
            catch (Exception ex) { logger.Warn(ex, "Could not cancel the current installation."); }
            Render();
        }

        private void Render()
        {
            var game = GameContext;
            var progress = game != null && game.PluginId == pluginId ? state.Get(game.Id) : null;
            Visibility = progress == null ? Visibility.Collapsed : Visibility.Visible;
            if (progress == null) return;
            title.Text = string.IsNullOrWhiteSpace(progress.Message) ? PhaseName(progress.Phase) : progress.Message;
            details.Text = TransferText(progress);
            bar.IsIndeterminate = !progress.Percentage.HasValue && progress.Active;
            bar.Value = progress.Percentage ?? 0;
            bar.Visibility = progress.Active ? Visibility.Visible : Visibility.Collapsed;
            cancel.Visibility = progress.Active && (progress.Phase == "DownloadingMods" || progress.Phase == "BackgroundModSync") ? Visibility.Visible : Visibility.Collapsed;
            cancel.IsEnabled = progress.Active;
            retry.Visibility = progress.CanRetry ? Visibility.Visible : Visibility.Collapsed;
            retry.Content = progress.RetryLabel ?? "Retry mods";
        }

        private static string PhaseName(string phase)
        {
            switch (phase)
            {
                case "Inventory": return "Preparing download — checking file sizes";
                case "Downloading": return "Downloading game";
                case "Finishing": return "Checking installation";
                case "Retrying": return "Retrying download — original total unchanged";
                case "DownloadingMods": return "Downloading cloud mods";
                case "BackgroundModSync": return "Game installed — synchronizing mods in the background";
                case "Cancelled": return "Cancelled — completed files are retained";
                case "Failed": return "Installation needs attention";
                default: return phase ?? "Installing";
            }
        }

        private static string TransferText(InstallProgressSnapshot value)
        {
            const double gib = 1024.0 * 1024.0 * 1024.0;
            const double mib = 1024.0 * 1024.0;
            var parts = new List<string>();
            if (value.Percentage.HasValue) parts.Add(value.Percentage.Value.ToString("0.0", CultureInfo.CurrentCulture) + "%");
            if (value.TotalBytes.HasValue && value.Phase != "Retrying")
                parts.Add(string.Format(CultureInfo.CurrentCulture, "{0:0.00} / {1:0.00} GiB", value.CompletedBytes / gib, value.TotalBytes.Value / gib));
            if (value.BytesPerSecond > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, "{0:0.0} MiB/s", value.BytesPerSecond / mib));
            return string.Join(" · ", parts);
        }
    }
}
