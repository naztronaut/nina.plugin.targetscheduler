using CommunityToolkit.Mvvm.Input;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Plugin.TargetScheduler.Controls.Util;
using NINA.Plugin.TargetScheduler.Database;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace NINA.Plugin.TargetScheduler.Controls.Availability {

    /// <summary>
    /// Shows when each active target can be imaged over the coming nights and lets the user select targets to
    /// prioritize (with a per-target expiration date).
    /// </summary>
    public class TargetAvailabilityViewVM : BaseVM {
        private SchedulerDatabaseInteraction database;

        public TargetAvailabilityViewVM(IProfileService profileService) : base(profileService) {
            database = new SchedulerDatabaseInteraction();

            NightCountChoices = new List<int> { 1, 2, 3, 5, 7, 10, 14 };
            nightCount = 7;
            StartDate = DateTime.Now.Date;
            SelectedProfileId = profileService.ActiveProfile.Id.ToString();
            ProfileChoices = GetProfileChoices();

            profileService.ProfileChanged += ProfileService_ProfileChanged;

            CalculateCommand = new AsyncRelayCommand(Calculate);
            SaveCommand = new RelayCommand(Save);
            RevertCommand = new RelayCommand(Revert);
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            SelectedProfileId = profileService.ActiveProfile.Id.ToString();
            ProfileChoices = GetProfileChoices();
        }

        public ICommand CalculateCommand { get; private set; }
        public ICommand SaveCommand { get; private set; }
        public ICommand RevertCommand { get; private set; }

        private AsyncObservableCollection<KeyValuePair<string, string>> profileChoices;

        public AsyncObservableCollection<KeyValuePair<string, string>> ProfileChoices {
            get => profileChoices;
            set {
                profileChoices = value;
                RaisePropertyChanged(nameof(ProfileChoices));
            }
        }

        private string selectedProfileId;

        public string SelectedProfileId {
            get => selectedProfileId;
            set {
                selectedProfileId = value;
                ClearResults();
                RaisePropertyChanged(nameof(SelectedProfileId));
            }
        }

        private DateTime startDate;

        public DateTime StartDate {
            get => startDate;
            set {
                startDate = value.Date;
                RaisePropertyChanged(nameof(StartDate));
            }
        }

        public List<int> NightCountChoices { get; private set; }

        private int nightCount;

        public int NightCount {
            get => nightCount;
            set {
                nightCount = value;
                RaisePropertyChanged(nameof(NightCount));
            }
        }

        /// <summary>
        /// The expiration date applied when a target is newly prioritized: the last night shown in the current
        /// results (each row's date can then be changed).
        /// </summary>
        public DateTime DefaultPrioritizeThrough { get; private set; }

        private bool isCalculating;

        public bool IsCalculating {
            get => isCalculating;
            set {
                isCalculating = value;
                RaisePropertyChanged(nameof(IsCalculating));
            }
        }

        private string statusText;

        public string StatusText {
            get => statusText;
            set {
                statusText = value;
                RaisePropertyChanged(nameof(StatusText));
            }
        }

        private List<TargetAvailabilityRow> rows = new List<TargetAvailabilityRow>();

        public List<TargetAvailabilityRow> Rows {
            get => rows;
            set {
                rows = value;
                RaisePropertyChanged(nameof(Rows));
                RaisePropertyChanged(nameof(HasResults));
            }
        }

        public bool HasResults => Rows.Count > 0;

        private List<string> nightHeaders = new List<string>();

        public List<string> NightHeaders {
            get => nightHeaders;
            set {
                nightHeaders = value;
                RaisePropertyChanged(nameof(NightHeaders));
            }
        }

        private List<AvailabilityCell> coverageCells = new List<AvailabilityCell>();

        /// <summary>
        /// Per night: when the currently prioritized targets can be imaged.
        /// </summary>
        public List<AvailabilityCell> CoverageCells {
            get => coverageCells;
            set {
                coverageCells = value;
                RaisePropertyChanged(nameof(CoverageCells));
            }
        }

        public bool HasChanges => Rows.Any(r => r.IsChanged);

        internal void RowChanged() {
            CoverageCells = BuildCoverageCells();
            RaisePropertyChanged(nameof(HasChanges));
        }

        private async Task Calculate() {
            if (SelectedProfileId == null) { return; }

            if (HasChanges && MyMessageBox.Show("Discard unsaved priority changes and recalculate?", "Unsaved Changes", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes) {
                return;
            }

            IsCalculating = true;
            ClearResults();

            DateTime firstNight = StartDate;
            int nights = NightCount;
            IProgress<int> progress = new Progress<int>(n => StatusText = $"Calculating night {n} of {nights} ...");

            try {
                IProfile profile = GetProfile(SelectedProfileId);
                (List<TargetAvailability> availability, Dictionary<int, DateTime?> priorities) = await Task.Run(() => {
                    SchedulerPlanLoader loader = new SchedulerPlanLoader(profile);
                    ProfilePreference profilePreference = loader.GetProfilePreferences(database.GetContext());
                    List<IProject> projects = loader.LoadActiveProjects(database.GetContext());
                    if (Common.IsEmpty(projects)) {
                        return (new List<TargetAvailability>(), new Dictionary<int, DateTime?>());
                    }

                    projects.ForEach(p => p.Targets.ForEach(t => { t.IsPreview = true; }));
                    List<TargetAvailability> results = new TargetAvailabilityCalculator(profile, profilePreference).Calculate(projects, firstNight, nights, progress);

                    using (var context = database.GetContext()) {
                        Dictionary<int, DateTime?> dates = context.GetAllProjectsReadOnly(profile.Id.ToString())
                            .SelectMany(p => p.Targets)
                            .ToDictionary(t => t.Id, t => t.PrioritizeThrough);
                        return (results, dates);
                    }
                });

                if (availability.Count == 0) {
                    StatusText = "No active projects with incomplete, enabled targets were found for this profile.";
                    return;
                }

                DefaultPrioritizeThrough = firstNight.AddDays(nights - 1);
                NightHeaders = Enumerable.Range(0, nights).Select(n => firstNight.AddDays(n).ToString("ddd MMM d")).ToList();
                Rows = availability
                    .Select(a => new TargetAvailabilityRow(this, a, priorities.TryGetValue(a.Target.DatabaseId, out DateTime? d) ? d : null))
                    .OrderBy(r => r.ProjectName)
                    .ThenBy(r => r.TargetName)
                    .ToList();
                CoverageCells = BuildCoverageCells();
                RaisePropertyChanged(nameof(HasChanges));
                StatusText = $"{Rows.Count} targets, {nights} night{(nights == 1 ? "" : "s")} starting {firstNight:d}.  Times are when each target passes the planner's visibility, moon avoidance and twilight checks (scoring and humidity are not considered).";
            } catch (Exception ex) {
                TSLogger.Error($"failed to calculate target availability: {ex.Message} {ex.StackTrace}");
                StatusText = "Failed to calculate availability - see the TS log for details.";
            } finally {
                IsCalculating = false;
            }
        }

        private void Save() {
            List<TargetAvailabilityRow> changed = Rows.Where(r => r.IsChanged).ToList();
            if (changed.Count == 0) { return; }

            int failed = 0;
            using (var context = database.GetContext()) {
                foreach (TargetAvailabilityRow row in changed) {
                    if (context.SetTargetPriority(row.TargetId, row.PrioritizeThrough)) {
                        row.AcceptChange();
                    } else {
                        failed++;
                    }
                }
            }

            RaisePropertyChanged(nameof(HasChanges));
            if (failed > 0) {
                Notification.ShowError($"Failed to save priority for {failed} target(s) (see log for details)");
            } else {
                Notification.ShowSuccess($"Saved priority for {changed.Count} target(s)");
            }
        }

        private void Revert() {
            Rows.ForEach(r => r.RevertChange());
        }

        private void ClearResults() {
            Rows = new List<TargetAvailabilityRow>();
            NightHeaders = new List<string>();
            CoverageCells = new List<AvailabilityCell>();
            StatusText = null;
            RaisePropertyChanged(nameof(HasChanges));
        }

        /// <summary>
        /// For each night, find when at least one prioritized (as currently edited) target can be imaged.
        /// </summary>
        private List<AvailabilityCell> BuildCoverageCells() {
            List<AvailabilityCell> cells = new List<AvailabilityCell>();
            if (Rows.Count == 0) { return cells; }

            int nights = Rows[0].Availability.Nights.Count;
            for (int n = 0; n < nights; n++) {
                NightAvailability reference = Rows[0].Availability.Nights[n];
                if (!reference.HasNight) {
                    cells.Add(new AvailabilityCell("no night", null, false));
                    continue;
                }

                DateTime[] times = reference.SampleTimes;
                bool[] covered = new bool[times.Length];
                bool anyPrioritized = false;

                foreach (TargetAvailabilityRow row in Rows) {
                    DateTime? expiration = Target.GetPriorityExpiration(row.PrioritizeThrough);
                    if (expiration == null || expiration <= times[0]) { continue; }

                    anyPrioritized = true;
                    bool[] ready = row.Availability.Nights[n].Ready;
                    for (int i = 0; i < times.Length; i++) {
                        if (ready[i] && times[i] < expiration) { covered[i] = true; }
                    }
                }

                if (!anyPrioritized) {
                    cells.Add(new AvailabilityCell("no prioritized targets", null, false));
                    continue;
                }

                List<TimeInterval> intervals = TargetAvailabilityCalculator.ToIntervals(times, covered, reference.SampleMinutes, reference.NightEnd);
                if (intervals.Count == 0) {
                    cells.Add(new AvailabilityCell("prioritized targets\nnot available", "Prioritized targets can't be imaged this night, so other targets will be imaged instead.", false));
                } else {
                    string text = FormatIntervals(intervals);
                    cells.Add(new AvailabilityCell(text, $"Prioritized targets can be imaged {text.Replace("\n", ", ")}.  Other targets will be imaged outside of these times.", true));
                }
            }

            return cells;
        }

        internal static string FormatIntervals(List<TimeInterval> intervals) {
            return string.Join("\n", intervals.Select(i => $"{i.StartTime:t}–{i.EndTime:t}"));
        }

        internal static string FormatMinutes(double minutes) {
            int total = (int)Math.Round(minutes);
            return total >= 60 ? $"{total / 60}h {total % 60:00}m" : $"{total}m";
        }

        private AsyncObservableCollection<KeyValuePair<string, string>> GetProfileChoices() {
            AsyncObservableCollection<KeyValuePair<string, string>> choices = new AsyncObservableCollection<KeyValuePair<string, string>>();
            foreach (ProfileMeta profile in profileService.Profiles) {
                choices.Add(new KeyValuePair<string, string>(profile.Id.ToString(), profile.Name));
            }

            return choices;
        }

        private IProfile GetProfile(string profileId) {
            foreach (ProfileMeta profileMeta in profileService.Profiles) {
                if (profileMeta.Id.ToString() == profileId) {
                    return ProfileLoader.Load(profileService, profileMeta);
                }
            }

            throw new Exception($"failed to get profile for ID={profileId}");
        }
    }

    public class TargetAvailabilityRow : BaseINPC {
        private readonly TargetAvailabilityViewVM parent;

        public TargetAvailability Availability { get; }
        public int TargetId { get; }
        public string ProjectName { get; }
        public string TargetName { get; }
        public double PercentComplete { get; }
        public List<AvailabilityCell> NightCells { get; }

        private DateTime? originalPrioritizeThrough;

        public TargetAvailabilityRow(TargetAvailabilityViewVM parent, TargetAvailability availability, DateTime? prioritizeThrough) {
            this.parent = parent;
            Availability = availability;
            TargetId = availability.Target.DatabaseId;
            ProjectName = availability.Target.Project.Name;
            TargetName = availability.Target.Name;
            PercentComplete = Math.Round(availability.Target.Project.ExposureCompletionHelper.PercentComplete(availability.Target));
            NightCells = availability.Nights.Select(BuildCell).ToList();

            originalPrioritizeThrough = prioritizeThrough?.Date;
            this.prioritizeThrough = originalPrioritizeThrough;
            lastPrioritizeThrough = originalPrioritizeThrough;

            TogglePriorityCommand = new RelayCommand(() => IsPrioritized = !IsPrioritized);
        }

        public ICommand TogglePriorityCommand { get; private set; }

        private DateTime? prioritizeThrough;

        // The most recent date set on this row, restored if priority is unchecked and then checked again
        private DateTime? lastPrioritizeThrough;

        public DateTime? PrioritizeThrough {
            get => prioritizeThrough;
            set {
                DateTime? date = value?.Date;
                if (date == prioritizeThrough) { return; }

                prioritizeThrough = date;
                if (date != null) { lastPrioritizeThrough = date; }
                RaisePropertyChanged(nameof(PrioritizeThrough));
                RaisePropertyChanged(nameof(IsPrioritized));
                RaisePropertyChanged(nameof(IsChanged));
                parent.RowChanged();
            }
        }

        /// <summary>
        /// Checking restores the row's previous date (if it hasn't already passed), otherwise applies the default
        /// expiration date.  Unchecking clears the date.
        /// </summary>
        public bool IsPrioritized {
            get => prioritizeThrough != null;
            set {
                if (value == IsPrioritized) { return; }
                if (!value) {
                    PrioritizeThrough = null;
                    return;
                }

                bool lastStillValid = lastPrioritizeThrough != null && lastPrioritizeThrough >= DateTime.Now.Date;
                PrioritizeThrough = lastStillValid ? lastPrioritizeThrough : parent.DefaultPrioritizeThrough;
            }
        }

        public bool IsChanged => prioritizeThrough != originalPrioritizeThrough;

        public void AcceptChange() {
            originalPrioritizeThrough = prioritizeThrough;
            RaisePropertyChanged(nameof(IsChanged));
        }

        public void RevertChange() {
            PrioritizeThrough = originalPrioritizeThrough;
        }

        private static AvailabilityCell BuildCell(NightAvailability night) {
            if (!night.HasNight) {
                return new AvailabilityCell("no night", null, false);
            }

            if (night.Intervals.Count == 0) {
                string reason = night.MainUnavailableReason ?? Reasons.TargetNotVisible;
                return new AvailabilityCell("—", $"Not imagable this night: {reason}", false);
            }

            string intervals = TargetAvailabilityViewVM.FormatIntervals(night.Intervals);
            StringBuilder tip = new StringBuilder($"Imagable {intervals.Replace("\n", ", ")} ({TargetAvailabilityViewVM.FormatMinutes(night.Minutes)})");
            if (night.MainUnavailableReason != null) {
                tip.Append($".  Otherwise mostly unavailable due to: {night.MainUnavailableReason}");
            }

            return new AvailabilityCell($"{intervals}\n{TargetAvailabilityViewVM.FormatMinutes(night.Minutes)}", tip.ToString(), true);
        }
    }

    public class AvailabilityCell {
        public string Text { get; }
        public string ToolTip { get; }
        public bool IsAvailable { get; }

        public AvailabilityCell(string text, string toolTip, bool isAvailable) {
            Text = text;
            ToolTip = toolTip;
            IsAvailable = isAvailable;
        }
    }
}
