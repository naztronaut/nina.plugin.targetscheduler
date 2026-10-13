using FluentAssertions;
using Moq;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Test.Astrometry;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.TargetScheduler.Test.Planning {

    [TestFixture]
    public class TargetAvailabilityCalculatorTest {

        [Test]
        public void testToIntervals() {
            DateTime start = new DateTime(2024, 12, 1, 20, 0, 0);
            DateTime[] times = Enumerable.Range(0, 8).Select(i => start.AddMinutes(i * 5)).ToArray();
            DateTime nightEnd = start.AddMinutes(38);

            TargetAvailabilityCalculator.ToIntervals(times, new bool[8], 5, nightEnd).Should().BeEmpty();

            bool[] flags = { false, true, true, false, false, true, true, true };
            List<TimeInterval> intervals = TargetAvailabilityCalculator.ToIntervals(times, flags, 5, nightEnd);
            intervals.Count.Should().Be(2);
            intervals[0].StartTime.Should().Be(start.AddMinutes(5));
            intervals[0].EndTime.Should().Be(start.AddMinutes(15));
            intervals[1].StartTime.Should().Be(start.AddMinutes(25));

            // Last interval is clipped to the end of the night
            intervals[1].EndTime.Should().Be(nightEnd);
        }

        [Test]
        public void testNightAvailability() {
            DateTime start = new DateTime(2024, 12, 1, 20, 0, 0);
            DateTime[] times = Enumerable.Range(0, 4).Select(i => start.AddMinutes(i * 5)).ToArray();
            NightAvailability sut = new NightAvailability(start.Date, times, new[] { true, true, false, true }, 5, start.AddMinutes(20), Reasons.TargetMoonAvoidance);

            sut.HasNight.Should().BeTrue();
            sut.Intervals.Count.Should().Be(2);
            sut.Minutes.Should().Be(15);
            sut.MainUnavailableReason.Should().Be(Reasons.TargetMoonAvoidance);

            NightAvailability none = NightAvailability.NoNight(start.Date);
            none.HasNight.Should().BeFalse();
            none.Intervals.Should().BeEmpty();
            none.Minutes.Should().Be(0);
        }

        [Test]
        public void testCalculateNoTargets() {
            IProfile profile = PlanMocks.GetMockProfileService(TestData.Pittsboro_NC).Object.ActiveProfile;
            TargetAvailabilityCalculator sut = new TargetAvailabilityCalculator(profile, new ProfilePreference("abcd-1234"));

            sut.Calculate(null, new DateTime(2024, 12, 1), 3).Should().BeEmpty();
            sut.Calculate(new List<IProject>(), new DateTime(2024, 12, 1), 3).Should().BeEmpty();

            // Complete targets (no remaining exposure plans) are excluded
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("pp1", ProjectState.Active);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("M42", TestData.M42);
            PlanMocks.AddMockPlanTarget(pp, pt);
            sut.Calculate(PlanMocks.ProjectsList(pp.Object), new DateTime(2024, 12, 1), 3).Should().BeEmpty();
        }
    }
}
