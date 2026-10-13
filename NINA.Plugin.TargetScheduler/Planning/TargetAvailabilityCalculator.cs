using NINA.Astrometry;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.TargetScheduler.Astrometry;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NINA.Plugin.TargetScheduler.Planning {

    /// <summary>
    /// Determine when each active target could be imaged over a span of nights.  Each night is sampled from sunset to
    /// sunrise and, at each sample time, the same suitability filters used by the planner (completion, visibility,
    /// moon avoidance, twilight) are applied.  Scoring is not applied: the result describes what is possible, not what
    /// the planner would choose.  Humidity is ignored since it can't be predicted.
    /// </summary>
    public class TargetAvailabilityCalculator {
        public const int DEFAULT_SAMPLE_MINUTES = 5;
        private const int MOON_SAMPLE_STRIDE = 6;

        private readonly IProfile profile;
        private readonly ProfilePreference profilePreference;
        private readonly ObserverInfo observerInfo;
        private readonly int sampleMinutes;

        public TargetAvailabilityCalculator(IProfile profile, ProfilePreference profilePreference, int sampleMinutes = DEFAULT_SAMPLE_MINUTES) {
            this.profile = profile;
            this.profilePreference = profilePreference;
            this.sampleMinutes = sampleMinutes;
            this.observerInfo = new ObserverInfo {
                Latitude = profile.AstrometrySettings.Latitude,
                Longitude = profile.AstrometrySettings.Longitude,
                Elevation = profile.AstrometrySettings.Elevation,
            };
        }

        /// <summary>
        /// Calculate availability for all incomplete targets in the provided projects.
        /// </summary>
        /// <param name="projects">planning projects, as returned by SchedulerPlanLoader</param>
        /// <param name="firstNight">date on which the first night starts</param>
        /// <param name="nights">number of nights</param>
        /// <param name="progress">optional, reports the (1-based) night being calculated</param>
        /// <param name="token"></param>
        /// <returns></returns>
        public List<TargetAvailability> Calculate(List<IProject> projects, DateTime firstNight, int nights, IProgress<int> progress = null, CancellationToken token = default) {
            List<TargetAvailability> results = new List<TargetAvailability>();
            if (Common.IsEmpty(projects)) { return results; }

            List<ITarget> targets = projects.SelectMany(p => p.Targets).Where(t => t.ExposurePlans.Count > 0).ToList();
            if (targets.Count == 0) { return results; }

            Dictionary<ITarget, TargetAvailability> byTarget = new Dictionary<ITarget, TargetAvailability>();
            targets.ForEach(t => {
                TargetAvailability ta = new TargetAvailability(t);
                byTarget[t] = ta;
                results.Add(ta);
            });

            IWeatherDataMediator weather = new DisconnectedWeatherDataMediator();

            for (int n = 0; n < nights; n++) {
                token.ThrowIfCancellationRequested();
                progress?.Report(n + 1);

                DateTime nightOf = firstNight.Date.AddDays(n);
                TwilightCircumstances twilight = new TwilightCircumstances(observerInfo, nightOf);
                DateTime? dusk = twilight.CivilTwilightStart;
                DateTime? dawn = twilight.CivilTwilightEnd;

                if (dusk == null || dawn == null || dawn <= dusk) {
                    TSLogger.Warning($"no night time found for {nightOf:d}, skipping availability for that night");
                    targets.ForEach(t => byTarget[t].Nights.Add(NightAvailability.NoNight(nightOf)));
                    continue;
                }

                List<DateTime> sampleTimes = new List<DateTime>();
                for (DateTime t = dusk.Value; t < dawn.Value; t = t.AddMinutes(sampleMinutes)) {
                    sampleTimes.Add(t);
                }

                Dictionary<ITarget, bool[]> ready = targets.ToDictionary(t => t, t => new bool[sampleTimes.Count]);
                Dictionary<ITarget, Dictionary<string, int>> reasons = targets.ToDictionary(t => t, t => new Dictionary<string, int>());

                for (int i = 0; i < sampleTimes.Count; i++) {
                    token.ThrowIfCancellationRequested();
                    DateTime atTime = sampleTimes[i];
                    ResetRejections(projects);

                    Planner planner = new Planner(atTime, profile, profilePreference, weather, false, true, projects);
                    List<IProject> filtered = planner.FilterForIncomplete(projects);
                    filtered = planner.FilterForVisibility(filtered);
                    filtered = planner.FilterForMoonAvoidance(filtered);
                    filtered = planner.FilterForTwilight(filtered);
                    HashSet<ITarget> readyNow = new HashSet<ITarget>(planner.GetTargetsReadyNow(filtered));

                    foreach (ITarget target in targets) {
                        if (readyNow.Contains(target)) {
                            ready[target][i] = true;
                        } else {
                            string reason = target.RejectedReason ?? target.Project.RejectedReason ?? Reasons.TargetNotVisible;
                            reasons[target].TryGetValue(reason, out int count);
                            reasons[target][reason] = count + 1;
                        }
                    }
                }

                double?[] moonAltitudes = new double?[sampleTimes.Count];
                foreach (ITarget target in targets) {
                    token.ThrowIfCancellationRequested();
                    string mainReason = reasons[target].Count > 0 ? reasons[target].OrderByDescending(r => r.Value).First().Key : null;
                    NightAvailability night = new NightAvailability(nightOf, sampleTimes.ToArray(), ready[target], sampleMinutes, dawn.Value, mainReason);
                    night.MoonSeparation = GetClosestMoonSeparation(target, sampleTimes, ready[target], moonAltitudes);
                    byTarget[target].Nights.Add(night);
                }
            }

            ResetRejections(projects);
            return results;
        }

        /// <summary>
        /// Merge consecutive true samples into time intervals.  Each sample is taken to represent the span up to the next
        /// sample (clipped to the end of the night).
        /// </summary>
        public static List<TimeInterval> ToIntervals(DateTime[] sampleTimes, bool[] flags, int sampleMinutes, DateTime nightEnd) {
            List<TimeInterval> intervals = new List<TimeInterval>();
            int start = -1;

            for (int i = 0; i <= flags.Length; i++) {
                bool on = i < flags.Length && flags[i];
                if (on && start < 0) {
                    start = i;
                } else if (!on && start >= 0) {
                    DateTime end = sampleTimes[i - 1].AddMinutes(sampleMinutes);
                    intervals.Add(new TimeInterval(sampleTimes[start], end > nightEnd ? nightEnd : end));
                    start = -1;
                }
            }

            return intervals;
        }

        /// <summary>
        /// Find the closest approach of the moon to the target, while the moon is above the horizon, over the times the
        /// target is imagable.  Only every MOON_SAMPLE_STRIDE-th imagable sample (plus the last) is checked since the
        /// separation changes slowly.  Returns null if the moon is down throughout (or the target is never imagable).
        /// Moon altitudes are cached across targets since they only depend on time.
        /// </summary>
        private double? GetClosestMoonSeparation(ITarget target, List<DateTime> sampleTimes, bool[] ready, double?[] moonAltitudes) {
            List<int> readyIndexes = Enumerable.Range(0, ready.Length).Where(i => ready[i]).ToList();
            if (readyIndexes.Count == 0) { return null; }

            List<int> checkIndexes = readyIndexes.Where((_, n) => n % MOON_SAMPLE_STRIDE == 0).ToList();
            if (checkIndexes.Last() != readyIndexes.Last()) { checkIndexes.Add(readyIndexes.Last()); }

            double? closest = null;
            foreach (int i in checkIndexes) {
                moonAltitudes[i] ??= AstroUtil.GetMoonAltitude(sampleTimes[i], observerInfo);
                if (moonAltitudes[i] <= 0) { continue; }

                double separation = AstrometryUtils.GetMoonSeparationAngle(observerInfo, sampleTimes[i], target.Coordinates);
                closest = closest == null ? separation : Math.Min(closest.Value, separation);
            }

            return closest;
        }

        private static void ResetRejections(List<IProject> projects) {
            foreach (IProject project in projects) {
                project.Rejected = false;
                project.RejectedReason = null;
                foreach (ITarget target in project.Targets) {
                    target.Rejected = false;
                    target.RejectedReason = null;
                    target.ScoringResults = null;

                    foreach (IExposure exposure in target.ExposurePlans) {
                        exposure.Rejected = false;
                        exposure.RejectedReason = null;
                        exposure.MoonAvoidanceScore = MoonAvoidanceExpert.SCORE_OFF;
                    }
                }
            }
        }
    }

    public class TargetAvailability {
        public ITarget Target { get; }
        public List<NightAvailability> Nights { get; } = new List<NightAvailability>();

        public TargetAvailability(ITarget target) {
            Target = target;
        }
    }

    public class NightAvailability {
        public DateTime NightOf { get; }
        public DateTime[] SampleTimes { get; }
        public bool[] Ready { get; }
        public int SampleMinutes { get; }
        public DateTime NightEnd { get; }
        public bool HasNight { get; }

        /// <summary>
        /// The most common reason the target was not imagable during the night (null if always imagable).
        /// </summary>
        public string MainUnavailableReason { get; }

        public List<TimeInterval> Intervals { get; }

        /// <summary>
        /// Closest moon-target separation (degrees) while the moon is up during the imagable times, or null if the moon
        /// is down throughout (or the target isn't imagable).
        /// </summary>
        public double? MoonSeparation { get; internal set; }

        public double Minutes => Intervals.Sum(i => i.Duration) / 60.0;

        public NightAvailability(DateTime nightOf, DateTime[] sampleTimes, bool[] ready, int sampleMinutes, DateTime nightEnd, string mainUnavailableReason) {
            NightOf = nightOf;
            SampleTimes = sampleTimes;
            Ready = ready;
            SampleMinutes = sampleMinutes;
            NightEnd = nightEnd;
            MainUnavailableReason = mainUnavailableReason;
            HasNight = true;
            Intervals = TargetAvailabilityCalculator.ToIntervals(sampleTimes, ready, sampleMinutes, nightEnd);
        }

        private NightAvailability(DateTime nightOf) {
            NightOf = nightOf;
            SampleTimes = new DateTime[0];
            Ready = new bool[0];
            HasNight = false;
            Intervals = new List<TimeInterval>();
        }

        public static NightAvailability NoNight(DateTime nightOf) {
            return new NightAvailability(nightOf);
        }
    }
}
