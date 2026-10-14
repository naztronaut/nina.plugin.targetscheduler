using CommunityToolkit.Mvvm.Input;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Plugin.TargetScheduler.Astrometry;
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

namespace NINA.Plugin.TargetScheduler.Controls.PrioritizationOverrides {

    /// <summary>
    /// Target Prioritization Overrides: lets the user prioritize targets (with a per-target expiration date) over the
    /// scheduler's normal scoring, showing when each active target can be imaged over the coming nights.
    /// </summary>
    public class PrioritizationOverridesViewVM : BaseVM {
        private SchedulerDatabaseInteraction database;
        private readonly Action<int> openTarget;

        /// <param name="profileService"></param>
        /// <param name="openTarget">opens the target (by database ID) in Target Management</param>
        public PrioritizationOverridesViewVM(IProfileService profileService, Action<int> openTarget) : base(profileService) {
            database = new SchedulerDatabaseInteraction();
            this.openTarget = openTarget;

            NightCountChoices = new List<int> { 1, 2, 3, 4, 5, 6, 7};
            nightCount = 3;
            StartDate = DateTime.Now.Date;
            SelectedProfileId = profileService.ActiveProfile.Id.ToString();
            ProfileChoices = GetProfileChoices();

            profileService.ProfileChanged += ProfileService_ProfileChanged;

            CalculateCommand = new AsyncRelayCommand(Calculate);
            SaveCommand = new AsyncRelayCommand(Save);
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

        private List<PrioritizationOverrideRow> rows = new List<PrioritizationOverrideRow>();

        public List<PrioritizationOverrideRow> Rows {
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
                NightHeaders = Enumerable.Range(0, nights).Select(n => NightHeader(firstNight.AddDays(n))).ToList();
                Rows = availability
                    .Select(a => new PrioritizationOverrideRow(this, a, priorities.TryGetValue(a.Target.DatabaseId, out DateTime? d) ? d : null))
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

        private async Task Save() {
            List<PrioritizationOverrideRow> changed = Rows.Where(r => r.IsChanged).ToList();
            if (changed.Count == 0) { return; }

            // Skipping filters can change when a target is imagable (e.g. moon avoidance differs by filter)
            bool filtersChanged = changed.Any(r => r.FiltersChanged);

            int failed = 0;
            using (var context = database.GetContext()) {
                foreach (PrioritizationOverrideRow row in changed) {
                    if (context.SetTargetOverrides(row.TargetId, row.PrioritizeThrough, row.SkippedExposurePlanIds)) {
                        row.AcceptChange();
                    } else {
                        failed++;
                    }
                }
            }

            RaisePropertyChanged(nameof(HasChanges));
            if (failed > 0) {
                Notification.ShowError($"Failed to save overrides for {failed} target(s) (see log for details)");
                return;
            }

            Notification.ShowSuccess($"Saved overrides for {changed.Count} target(s)");
            if (filtersChanged) {
                await Calculate();
            }
        }

        private void Revert() {
            Rows.ForEach(r => r.RevertChange());
        }

        private void ClearResults() {
            Rows = new List<PrioritizationOverrideRow>();
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

                foreach (PrioritizationOverrideRow row in Rows) {
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

        internal void OpenTarget(int targetId) {
            openTarget?.Invoke(targetId);
        }

        /// <summary>
        /// Night date plus moon illumination at the midnight following the start of the night.
        /// </summary>
        private static string NightHeader(DateTime nightOf) {
            double illumination = AstrometryUtils.GetMoonIllumination(nightOf.AddDays(1));
            return $"{nightOf:ddd MMM d}\nmoon {illumination * 100:0}%";
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

    public class PrioritizationOverrideRow : BaseINPC {
        private readonly PrioritizationOverridesViewVM parent;

        public TargetAvailability Availability { get; }
        public int TargetId { get; }
        public string ProjectName { get; }
        public string TargetName { get; }
        public double PercentComplete { get; }
        public List<AvailabilityCell> NightCells { get; }

        private DateTime? originalPrioritizeThrough;

        public PrioritizationOverrideRow(PrioritizationOverridesViewVM parent, TargetAvailability availability, DateTime? prioritizeThrough) {
            this.parent = parent;
            Availability = availability;
            TargetId = availability.Target.DatabaseId;
            ProjectName = availability.Target.Project.Name;
            TargetName = availability.Target.Name;
            PercentComplete = Math.Round(availability.Target.Project.ExposureCompletionHelper.PercentComplete(availability.Target));
            NightCells = availability.Nights.Select(BuildCell).ToList();
            Filters = BuildFilterToggles(availability.Target);

            originalPrioritizeThrough = prioritizeThrough?.Date;
            this.prioritizeThrough = originalPrioritizeThrough;
            lastPrioritizeThrough = originalPrioritizeThrough;

            TogglePriorityCommand = new RelayCommand(() => IsPrioritized = !IsPrioritized);
            OpenTargetCommand = new RelayCommand(() => parent.OpenTarget(TargetId));
        }

        public ICommand TogglePriorityCommand { get; private set; }
        public ICommand OpenTargetCommand { get; private set; }

        /// <summary>
        /// The target's remaining (incomplete, enabled) exposure plans, which can be skipped while prioritized.
        /// </summary>
        public List<FilterToggle> Filters { get; }

        public bool FiltersChanged => Filters.Any(f => f.IsChanged);

        public List<int> SkippedExposurePlanIds => Filters.Where(f => !f.IsOn).Select(f => f.ExposurePlanId).ToList();

        internal void ToggleFilter(FilterToggle filter) {
            if (filter.IsOn && Filters.Count(f => f.IsOn) == 1) {
                Notification.ShowInformation("At least one filter must remain on for a prioritized target");
                return;
            }

            filter.IsOn = !filter.IsOn;

            // Filter skips only apply while prioritized, so skipping a filter prioritizes the target
            if (!filter.IsOn && !IsPrioritized) {
                IsPrioritized = true;
            }

            RaisePropertyChanged(nameof(IsChanged));
            parent.RowChanged();
        }

        private List<FilterToggle> BuildFilterToggles(ITarget target) {
            List<IExposure> plans = target.ExposurePlans;
            return plans.Select(ep => {
                // Distinguish plans that share a filter by exposure length
                bool duplicate = plans.Count(p => p.FilterName == ep.FilterName) > 1;
                string label = duplicate ? $"{ep.FilterName} {ep.ExposureLength:0.#}s" : ep.FilterName;
                return new FilterToggle(this, ep.DatabaseId, label, !ep.SkipWhilePrioritized);
            }).ToList();
        }

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

                // Filter skips only apply while prioritized: clearing priority turns all filters back on
                if (date == null) { Filters.ForEach(f => f.IsOn = true); }

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

        public bool IsChanged => prioritizeThrough != originalPrioritizeThrough || FiltersChanged;

        public void AcceptChange() {
            originalPrioritizeThrough = prioritizeThrough;
            Filters.ForEach(f => f.AcceptChange());
            RaisePropertyChanged(nameof(IsChanged));
        }

        public void RevertChange() {
            PrioritizeThrough = originalPrioritizeThrough;
            Filters.ForEach(f => f.RevertChange());
            RaisePropertyChanged(nameof(IsChanged));
            parent.RowChanged();
        }

        private static AvailabilityCell BuildCell(NightAvailability night) {
            if (!night.HasNight) {
                return new AvailabilityCell("no night", null, false);
            }

            if (night.Intervals.Count == 0) {
                string reason = night.MainUnavailableReason ?? Reasons.TargetNotVisible;
                return new AvailabilityCell("—", $"Not imagable this night: {reason}", false);
            }

            string intervals = PrioritizationOverridesViewVM.FormatIntervals(night.Intervals);
            string moon = night.MoonSeparation == null ? "moon down" : $"moon {night.MoonSeparation:0}°";
            StringBuilder tip = new StringBuilder($"Imagable {intervals.Replace("\n", ", ")} ({PrioritizationOverridesViewVM.FormatMinutes(night.Minutes)})");
            tip.Append(night.MoonSeparation == null
                ? ".  The moon is below the horizon while the target is imagable"
                : $".  Closest moon approach while the moon is up: {night.MoonSeparation:0}°");
            if (night.MainUnavailableReason != null) {
                tip.Append($".  Otherwise mostly unavailable due to: {night.MainUnavailableReason}");
            }

            return new AvailabilityCell($"{intervals}\n{PrioritizationOverridesViewVM.FormatMinutes(night.Minutes)} · {moon}", tip.ToString(), true);
        }
    }

    /// <summary>
    /// One of a target's exposure plans, which can be switched off (skipped) while the target is prioritized.
    /// </summary>
    public class FilterToggle : BaseINPC {
        private readonly PrioritizationOverrideRow row;
        private bool originalIsOn;

        public int ExposurePlanId { get; }
        public string Label { get; }

        public FilterToggle(PrioritizationOverrideRow row, int exposurePlanId, string label, bool isOn) {
            this.row = row;
            ExposurePlanId = exposurePlanId;
            Label = label;
            this.isOn = isOn;
            originalIsOn = isOn;
            ToggleCommand = new RelayCommand(() => row.ToggleFilter(this));
        }

        public ICommand ToggleCommand { get; private set; }

        public string ToolTip => IsOn
            ? $"{Label} will be imaged.  Click to skip it while the target is prioritized."
            : $"{Label} is skipped while the target is prioritized.  Click to image it.";

        private bool isOn;

        public bool IsOn {
            get => isOn;
            set {
                if (value == isOn) { return; }
                isOn = value;
                RaisePropertyChanged(nameof(IsOn));
                RaisePropertyChanged(nameof(ToolTip));
            }
        }

        public bool IsChanged => isOn != originalIsOn;

        public void AcceptChange() {
            originalIsOn = isOn;
        }

        public void RevertChange() {
            IsOn = originalIsOn;
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
