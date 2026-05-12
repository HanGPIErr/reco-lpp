using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using RecoTool.Infrastructure.Logging;
using Xunit;

namespace RecoTool.Tests.Infrastructure.Logging
{
    /// <summary>
    /// Smoke test for <see cref="LoggingSetup"/>. Verifies that:
    /// <list type="bullet">
    ///   <item>The factory produces a usable <see cref="ILogger{T}"/>.</item>
    ///   <item>Information messages are written to a daily-rolling file under
    ///   <c>%APPDATA%/RecoTool/logs</c>.</item>
    /// </list>
    ///
    /// <para>
    /// The test writes a single line containing a unique marker, disposes the factory
    /// (which flushes the async sink), and then scans today's log file for that marker.
    /// </para>
    /// </summary>
    public class SerilogSmokeTest
    {
        [Fact]
        public void CreateLoggerFactory_WritesInformationMessageToRollingFile()
        {
            // Arrange — a token guaranteed not to collide with any other log line.
            var token = "SmokeTest-" + Guid.NewGuid().ToString("N");

            // Act — build factory, log, dispose to flush async sink.
            var factory = LoggingSetup.CreateLoggerFactory();
            try
            {
                var logger = factory.CreateLogger<SerilogSmokeTest>();
                logger.IsEnabled(LogLevel.Information).Should().BeTrue(
                    "default min level for production is Information and tests run without UAT_ENV");

                logger.LogInformation("Serilog smoke test marker: {Token}", token);
            }
            finally
            {
                // Disposing the SerilogLoggerFactory disposes the underlying logger,
                // which flushes the wrapped async sink before returning.
                factory.Dispose();
            }

            // Assert — find today's rolling file and ensure our token landed in it.
            var logDir = LoggingSetup.GetLogDirectory();
            Directory.Exists(logDir).Should().BeTrue($"LoggingSetup should have created {logDir}");

            // Rolling daily files are named like "recotool-20260511.log".
            var candidates = Directory.EnumerateFiles(logDir, "recotool-*.log").ToList();
            candidates.Should().NotBeEmpty("at least one rolling log file must exist after a write");

            // Read all candidate files (typically just today's) and check for the token.
            // We tolerate sharing-violation retries; the async sink may still be releasing
            // the handle on slow CI.
            var found = candidates.Any(file =>
            {
                try
                {
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs))
                    {
                        return reader.ReadToEnd().Contains(token);
                    }
                }
                catch
                {
                    return false;
                }
            });

            found.Should().BeTrue($"the log marker '{token}' should appear in one of: {string.Join(", ", candidates)}");
        }
    }
}
