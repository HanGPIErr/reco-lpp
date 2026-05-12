using System;
using System.IO;
using FluentAssertions;
using RecoTool.Infrastructure.DataAccess;
using Xunit;

namespace RecoTool.Tests.Infrastructure
{
    /// <summary>
    /// Tests pour <see cref="DbConn"/> — construction des connection strings ACE/Jet.
    /// </summary>
    public class DbConnTests : IDisposable
    {
        private readonly string _tmpDir;

        public DbConnTests()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(), "RecoTool.Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, true); } catch { }
        }

        // ----- AceConn -----

        [Fact]
        public void AceConn_BuildsAce16ConnectionString()
        {
            DbConn.AceConn(@"C:\db.accdb")
                .Should().Be(@"Provider=Microsoft.ACE.OLEDB.16.0;Data Source=C:\db.accdb;");
        }

        [Fact]
        public void AceConnNetwork_AddsLockingHints()
        {
            var s = DbConn.AceConnNetwork(@"\\server\share\db.accdb");
            s.Should().Contain("Provider=Microsoft.ACE.OLEDB.16.0");
            s.Should().Contain(@"Data Source=\\server\share\db.accdb");
            s.Should().Contain("Locking Mode=1");
            s.Should().Contain("Mode=Share Deny None");
        }

        // ----- ResolveConnectionString -----

        [Fact]
        public void ResolveConnectionString_NullOrEmpty_Throws()
        {
            Action a1 = () => DbConn.ResolveConnectionString(null);
            Action a2 = () => DbConn.ResolveConnectionString("   ");
            a1.Should().Throw<ArgumentNullException>();
            a2.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ResolveConnectionString_ConnectionStringPassthrough()
        {
            var cs = "Provider=ACE;Data Source=foo;";
            DbConn.ResolveConnectionString(cs).Should().Be(cs);
        }

        [Fact]
        public void ResolveConnectionString_NonExistentFile_AssumesItIsConnectionString()
        {
            var notAFile = "this is just a string";
            DbConn.ResolveConnectionString(notAFile).Should().Be(notAFile);
        }

        [Fact]
        public void ResolveConnectionString_ExistingMdbFile_BuildsJetProvider()
        {
            var path = Path.Combine(_tmpDir, "test.mdb");
            File.WriteAllText(path, "fake");
            var cs = DbConn.ResolveConnectionString(path);
            cs.Should().Contain("Provider=Microsoft.Jet.OLEDB.4.0");
            cs.Should().Contain($"Data Source={path}");
        }

        [Fact]
        public void ResolveConnectionString_ExistingAccdbFile_BuildsAce12Provider()
        {
            var path = Path.Combine(_tmpDir, "test.accdb");
            File.WriteAllText(path, "fake");
            var cs = DbConn.ResolveConnectionString(path);
            cs.Should().Contain("Provider=Microsoft.ACE.OLEDB.12.0");
            cs.Should().Contain($"Data Source={path}");
        }

        [Fact]
        public void ResolveConnectionString_ExistingFileNonStandardExtension_FallsBackToAce12()
        {
            var path = Path.Combine(_tmpDir, "weird.xyz");
            File.WriteAllText(path, "fake");
            DbConn.ResolveConnectionString(path).Should().Contain("Provider=Microsoft.ACE.OLEDB.12.0");
        }
    }
}
