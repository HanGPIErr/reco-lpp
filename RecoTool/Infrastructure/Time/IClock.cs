using System;

namespace RecoTool.Infrastructure.Time
{
    /// <summary>
    /// Abstraction over <see cref="DateTime"/> static accessors so that all
    /// time-dependent logic in the application can be unit-tested with a
    /// controllable clock (<c>FakeClock</c> in the test project).
    ///
    /// <para>
    /// Production binding: <see cref="SystemClock"/>. Tests inject a fake.
    /// </para>
    /// </summary>
    public interface IClock
    {
        /// <summary>Wall-clock current local time (== <see cref="DateTime.Now"/>).</summary>
        DateTime Now { get; }

        /// <summary>Wall-clock current UTC time (== <see cref="DateTime.UtcNow"/>).</summary>
        DateTime UtcNow { get; }

        /// <summary>Today's date with time component zeroed (== <see cref="DateTime.Today"/>).</summary>
        DateTime Today { get; }
    }

    /// <summary>
    /// Default production implementation backed by the OS clock. Stateless,
    /// thread-safe, no allocation per call.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTime Now => DateTime.Now;
        public DateTime UtcNow => DateTime.UtcNow;
        public DateTime Today => DateTime.Today;
    }
}
