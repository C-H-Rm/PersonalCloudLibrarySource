using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySource
    {
        private readonly object downloadCenterGate = new object();
        private DownloadHistory downloadHistory;
        private Window downloadCenterWindow;
        private DispatcherTimer downloadCenterTimer;
        private bool downloadCenterDirty;
        private CHRCL.DownloadCenterView downloadCenterView;

        // Lifecycle integration calls this after InitializeLegacyMigration has completed.
        public void InitializeDownloadCenter()
        {
            lock (downloadCenterGate)
            {
                if (downloadHistory == null)
                {
                    downloadHistory = new DownloadHistory(InstallProgressState.Shared,
                        Path.Combine(GetPluginUserDataPath(), "download-history.json"));
                    downloadHistory.Changed += DownloadHistoryChanged;
                }
            }
            // A panel can be activated before application-started callbacks complete; refresh it now.
            DownloadHistoryChanged(this, EventArgs.Empty);
        }

        public void CloseDownloadCenter()
        {
            DownloadHistory history;
            lock (downloadCenterGate)
            {
                if (downloadHistory == null) return;
                history = downloadHistory;
                downloadHistory = null;
                history.Changed -= DownloadHistoryChanged;
                StopDownloadCenterTimer();
                if (downloadCenterWindow != null)
                {
                    downloadCenterWindow.Closed -= DownloadCenterWindowClosed;
                    downloadCenterWindow.Close();
                    downloadCenterWindow = null;
                }
                downloadCenterView = null;
            }
            history.Dispose();
        }

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            // Construct the icon lazily: Playnite may query extensions before migration/lifecycle startup.
            return new[] { new TopPanelItem
            {
                Title = "Downloads",
                Icon = new TextBlock { Text = "⇩", ToolTip = "Open download progress and history", FontSize = 16 },
                Visible = true,
                Activated = OpenDownloadCenter
            } };
        }

        private void OpenDownloadCenter()
        {
            lock (downloadCenterGate)
            {
                if (downloadCenterWindow != null)
                {
                    if (downloadCenterWindow.WindowState == WindowState.Minimized) downloadCenterWindow.WindowState = WindowState.Normal;
                    downloadCenterWindow.Activate();
                    return;
                }
                downloadCenterView = new CHRCL.DownloadCenterView();
                downloadCenterWindow = playniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false, ShowMaximizeButton = false, ShowCloseButton = true
                });
                downloadCenterWindow.Title = ForkIdentity.DisplayName + " — Downloads";
                downloadCenterWindow.Width = 620;
                downloadCenterWindow.Height = 520;
                downloadCenterWindow.MinWidth = 440;
                downloadCenterWindow.MinHeight = 320;
                downloadCenterWindow.Content = downloadCenterView.Root;
                downloadCenterWindow.Closed += DownloadCenterWindowClosed;
                downloadCenterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                downloadCenterTimer.Tick += DownloadCenterTick;
                downloadCenterTimer.Start();
                downloadCenterWindow.Show();
                RefreshDownloadCenter();
            }
        }

        private void DownloadCenterWindowClosed(object sender, EventArgs args)
        {
            lock (downloadCenterGate)
            {
                if (ReferenceEquals(downloadCenterWindow, sender)) downloadCenterWindow = null;
                StopDownloadCenterTimer();
                downloadCenterView = null;
            }
        }

        private void DownloadHistoryChanged(object sender, EventArgs args)
        {
            lock (downloadCenterGate) downloadCenterDirty = true;
        }

        private void DownloadCenterTick(object sender, EventArgs args)
        {
            lock (downloadCenterGate)
            {
                if (!downloadCenterDirty) return;
                downloadCenterDirty = false;
            }
            RefreshDownloadCenter();
        }

        private void StopDownloadCenterTimer()
        {
            if (downloadCenterTimer == null) return;
            downloadCenterTimer.Stop();
            downloadCenterTimer.Tick -= DownloadCenterTick;
            downloadCenterTimer = null;
        }

        private void RefreshDownloadCenter()
        {
            DownloadHistory history;
            CHRCL.DownloadCenterView view;
            lock (downloadCenterGate) { history = downloadHistory; view = downloadCenterView; }
            if (history == null || view == null) return;
            try { view.Refresh(history.GetSnapshot(), ResolveGameTitle, history.IsCorrupt); }
            catch (Exception ex) { logger.Warn(ex, "Could not refresh the download center view."); }
        }

        private string ResolveGameTitle(Guid gameId)
        {
            try { return playniteApi.Database.Games.Get(gameId)?.Name ?? gameId.ToString(); }
            catch { return gameId.ToString(); }
        }
    }
}

// UI controls live in the fork-owned namespace and inherit Playnite theme defaults.
namespace CHRCL
{
    using System.Globalization;
    using System.Windows;
    using System.Windows.Controls;
    using System.Collections.Generic;
    using System.Linq;

    public sealed class DownloadCenterView
    {
        private readonly StackPanel activeRows = new StackPanel();
        private readonly StackPanel historyRows = new StackPanel();
        private string historySignature;
        public UIElement Root { get; }

        public DownloadCenterView()
        {
            var tabs = new TabControl { Margin = new Thickness(12) };
            tabs.Items.Add(new TabItem { Header = "Active", Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = activeRows } });
            tabs.Items.Add(new TabItem { Header = "History", Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = historyRows } });
            Root = tabs;
        }

        public void Refresh(IList<PersonalCloudLibrarySource.DownloadHistoryRecord> records, Func<Guid, string> titleResolver, bool corruptHistory)
        {
            activeRows.Children.Clear();
            foreach (var record in records.Where(x => x.Active).OrderByDescending(x => x.UpdatedUtc))
                activeRows.Children.Add(CreateRow(record, record.GameName ?? titleResolver(record.GameId)));
            var completed = records.Where(x => !x.Active).OrderByDescending(x => x.UpdatedUtc).ToList();
            var signature = corruptHistory + ":" + string.Join("|", completed.Select(x => x.OperationId + ":" + x.UpdatedUtc.Ticks));
            if (signature != historySignature)
            {
                historySignature = signature;
                historyRows.Children.Clear();
                if (corruptHistory) historyRows.Children.Add(EmptyRow("Saved history could not be read and was preserved unchanged. New operations are shown in memory only."));
                foreach (var record in completed)
                    historyRows.Children.Add(CreateRow(record, record.GameName ?? titleResolver(record.GameId)));
                if (historyRows.Children.Count == 0) historyRows.Children.Add(EmptyRow("Completed and interrupted operations will appear here."));
            }
            if (activeRows.Children.Count == 0) activeRows.Children.Add(EmptyRow("No downloads are currently active."));
        }

        private static UIElement CreateRow(PersonalCloudLibrarySource.DownloadHistoryRecord record, string title)
        {
            var panel = new StackPanel { Margin = new Thickness(8, 7, 8, 7) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock { Text = record.OperationKind + " — " + record.Phase, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 4) });
            if (record.Active)
            {
                var unknown = !record.Percentage.HasValue;
                var progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 2, 0, 4), IsIndeterminate = unknown };
                if (!unknown) progress.Value = record.Percentage.Value;
                panel.Children.Add(progress);
                panel.Children.Add(new TextBlock { Text = FormatProgress(record), Opacity = 0.72, FontSize = 11 });
            }
            else
            {
                var count = record.TotalBytes.HasValue ? FormatBytes(record.CompletedBytes) + " / " + FormatBytes(record.TotalBytes.Value) : FormatBytes(record.CompletedBytes);
                panel.Children.Add(new TextBlock { Text = count + " · " + record.UpdatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Opacity = 0.72, FontSize = 11 });
            }
            return new Border { BorderBrush = SystemColors.ControlDarkBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = panel };
        }

        private static UIElement EmptyRow(string text) => new TextBlock { Text = text, Margin = new Thickness(12), Opacity = 0.7 };
        private static string FormatProgress(PersonalCloudLibrarySource.DownloadHistoryRecord record)
        {
            var value = record.Percentage.HasValue
                ? (Math.Floor(record.Percentage.Value * 10) / 10).ToString("0.0", CultureInfo.CurrentCulture) + "% · " : string.Empty;
            value += record.TotalBytes.HasValue ? FormatBytes(record.CompletedBytes) + " / " + FormatBytes(record.TotalBytes.Value) : FormatBytes(record.CompletedBytes);
            value += record.BytesPerSecond > 0 ? " · " + FormatBytes((long)record.BytesPerSecond) + "/s" : " · speed unavailable";
            return value;
        }
        private static string FormatBytes(long bytes)
        {
            var amount = (double)Math.Max(0, bytes);
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
            var unit = 0;
            while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
            return amount.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }
}
