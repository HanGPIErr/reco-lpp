using System;
using FluentAssertions;
using RecoTool.Infrastructure.Time;
using Xunit;

namespace RecoTool.Tests.Infrastructure.Time
{
    /// <summary>
    /// Smoke tests for <see cref="SystemClock"/>. The implementation is a thin
    /// passthrough — these tests just confirm it returns "current" values.
    /// </summary>
    public class SystemClockTests
    {
        [Fact]
        public void Instance_IsSingleton()
        {
            SystemClock.Instance.Should().BeSameAs(SystemClock.Instance);
        }

        [Fact]
        public void Now_IsCloseToDateTimeNow()
        {
            var diff = (DateTime.Now - SystemClock.Instance.Now).Duration();
            diff.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void UtcNow_IsCloseToDateTimeUtcNow()
        {
            var diff = (DateTime.UtcNow - SystemClock.Instance.UtcNow).Duration();
            diff.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void Today_HasZeroTimeComponent()
        {
            SystemClock.Instance.Today.TimeOfDay.Should().Be(TimeSpan.Zero);
            SystemClock.Instance.Today.Should().Be(DateTime.Today);
        }
    }

    /// <summary>
    /// Reusable fake clock for unit tests. Implements <see cref="IClock"/> with
    /// a controllable "current time".
    /// </summary>
    public sealed class FakeClock : IClock
    {
        private DateTime _now;

        public FakeClock(DateTime initial) { _now = initial; }
        public FakeClock() : this(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Local)) { }

        public DateTime Now => _now;
        public DateTime UtcNow => _now.ToUniversalTime();
        public DateTime Today => _now.Date;

        public void Advance(TimeSpan delta) { _now = _now.Add(delta); }
        public void SetTime(DateTime instant) { _now = instant; }
    }

    public class FakeClockTests
    {
        [Fact]
        public void FakeClock_AdvanceMovesNowAndToday()
        {
            var c = new FakeClock(new DateTime(2024, 1, 1, 23, 30, 0));
            c.Advance(TimeSpan.FromHours(1));
            c.Now.Should().Be(new DateTime(2024, 1, 2, 0, 30, 0));
            c.Today.Should().Be(new DateTime(2024, 1, 2));
        }

        [Fact]
        public void FakeClock_SetTime_ReplacesValue()
        {
            var c = new FakeClock();
            var target = new DateTime(2030, 6, 15, 9, 0, 0);
            c.SetTime(target);
            c.Now.Should().Be(target);
        }
    }
}
