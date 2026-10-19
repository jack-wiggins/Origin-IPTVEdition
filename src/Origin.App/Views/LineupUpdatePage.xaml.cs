using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using Origin.Core.Diagnostics;
using Origin.Core.Guide;
using Origin.Core.Lineup;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;

namespace Origin.Views
{
    /// <summary>
    /// Tasks › update lineup: downloads the M3U, fetches the Schedules Direct lineup, copies the
    /// playlist into Origin's lineup, matches every channel and downloads the guide, then shows
    /// what happened.
    /// </summary>
    public partial class LineupUpdatePage : OriginPage
    {
        private readonly LineupUpdateOptions _options;
        private CancellationTokenSource _cancel;
        private bool _running;
        private bool _started;
        private bool _cancelledByUser;

        public LineupUpdatePage(IShell shell, LineupUpdateOptions options) : base(shell)
        {
            InitializeComponent();
            _options = options;
        }

        public override string Title => "update lineup";

        public override void OnNavigatedTo(bool isBack)
        {
            if (!_started)
            {
                _started = true;
                Run();
            }
        }

        public override void OnNavigatedFrom()
        {
            _cancel?.Cancel();
        }

        public override void FocusDefault()
        {
            if (ResultPanel.Visibility == Visibility.Visible)
            {
                (RetryButton.Visibility == Visibility.Visible ? RetryButton : DoneButton).Focus();
            }
        }

        public override bool HandleCommand(RemoteCommand command)
        {
            if (command == RemoteCommand.Back && _running)
            {
                _cancelledByUser = true;
                _cancel?.Cancel();
                StageText.Text = "Cancelling…";
                return true;
            }

            return false;
        }

        private async void Run()
        {
            _running = true;
            _cancelledByUser = false;
            _cancel = new CancellationTokenSource();
            ShowWorking();

            var progress = new Progress<LineupUpdateProgress>(p =>
            {
                StageText.Text = p.Stage + "…";
                DetailText.Text = p.Detail ?? string.Empty;
            });

            try
            {
                var result = await Services.Lineup.UpdateAsync(_options, progress, _cancel.Token);
                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                Shell.ShowToast("Lineup update cancelled. Your previous lineup is unchanged.", ToastKind.Info);
                if (_cancelledByUser)
                {
                    Shell.GoBack();
                }
            }
            catch (LineupUpdateException ex)
            {
                ShowFailure(ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("Lineup update failed", ex);
                ShowFailure("Something unexpected went wrong: " + ex.GetBaseException().Message);
            }
            finally
            {
                _running = false;
            }
        }

        private void ShowWorking()
        {
            ResultPanel.Visibility = Visibility.Collapsed;
            WorkingPanel.Visibility = Visibility.Visible;
            Anim.FromTo(WorkingPanel, OpacityProperty, 0, 1, 300);
            StageText.Text = "Getting ready…";
            DetailText.Text = string.Empty;
        }

        private void ShowResult(LineupUpdateResult result)
        {
            ResultTitle.Text = "Your lineup is ready";

            var source = result.Lineup.PlaylistSource != null ? " from " + result.Lineup.PlaylistSource : string.Empty;
            var onDemand = result.MovieEntries + result.SeriesEntries;
            var extras = onDemand == 0 ? string.Empty
                : onDemand == 1 ? " (1 movie or episode is set aside for later)"
                : " (" + onDemand.ToString("N0") + " movies and episodes are set aside for later)";
            ResultText.Text = "Copied " + result.Channels.ToString("N0") + " live channels" + source + extras
                              + (result.Lineup.SchedulesDirectLineupName != null
                                  ? " and matched them against " + result.Lineup.SchedulesDirectLineupName + "."
                                  : ".")
                              + DescribeGuide(result.Guide);

            ChannelsValue.Text = result.Channels.ToString("N0");
            MappedValue.Text = result.Mapped.ToString("N0");
            UnmappedValue.Text = result.Unmapped.ToString("N0");
            StatsGrid.Visibility = Visibility.Visible;
            WarningList.ItemsSource = result.Warnings;

            RetryButton.Visibility = Visibility.Collapsed;
            MappingButton.Visibility = Visibility.Visible;
            GroupsButton.Visibility = Visibility.Visible;
            Reveal();

            Shell.ShowToast("Lineup updated: " + result.Channels.ToString("N0") + " channels, " + result.Mapped.ToString("N0") + " matched.", ToastKind.Success);
        }

        /// <summary>" The guide has 14 days of listings for 212 stations (1,204 programs updated)."</summary>
        private static string DescribeGuide(GuideUpdateResult guide)
        {
            if (guide == null || guide.StationsWithListings == 0)
            {
                return string.Empty;
            }

            var stations = guide.StationsWithListings == 1 ? "1 station" : guide.StationsWithListings.ToString("N0") + " stations";
            var updated = guide.ProgramsDownloaded == 0 ? "nothing had changed"
                : guide.ProgramsDownloaded == 1 ? "1 program updated"
                : guide.ProgramsDownloaded.ToString("N0") + " programs updated";
            return " The guide has " + GuideDownloader.DaysAhead + " days of listings for " + stations + " (" + updated + ").";
        }

        private void ShowFailure(string message)
        {
            ResultTitle.Text = "The lineup wasn't updated";
            ResultText.Text = "Your previous lineup is unchanged.";
            StatsGrid.Visibility = Visibility.Collapsed;
            WarningList.ItemsSource = new List<string> { message };

            RetryButton.Visibility = Visibility.Visible;
            MappingButton.Visibility = Visibility.Collapsed;
            GroupsButton.Visibility = Visibility.Collapsed;
            Reveal();
        }

        private void Reveal()
        {
            WorkingPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
            Anim.FromTo(ResultPanel, OpacityProperty, 0, 1, 350);
            Dispatcher.BeginInvoke(new Action(FocusDefault), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnDone(object sender, RoutedEventArgs e) => Shell.GoHome();

        private void OnRetry(object sender, RoutedEventArgs e) => Run();

        private void OnReviewMatches(object sender, RoutedEventArgs e) => Shell.Replace(new ChannelMappingPage(Shell));

        private void OnChooseGroups(object sender, RoutedEventArgs e) => Shell.Replace(new ChannelGroupsPage(Shell));
    }
}
