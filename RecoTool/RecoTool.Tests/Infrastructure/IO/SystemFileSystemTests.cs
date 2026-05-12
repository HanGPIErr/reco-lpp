using System;
using System.IO;
using System.Text;
using FluentAssertions;
using RecoTool.Infrastructure.IO;
using Xunit;

namespace RecoTool.Tests.Infrastructure.IO
{
    /// <summary>
    /// Tests for <see cref="SystemFileSystem"/>. Uses a per-test temp directory
    /// (cleaned up via <see cref="IDisposable"/>) so tests don't interfere.
    /// </summary>
    public class SystemFileSystemTests : IDisposable
    {
        private readonly string _tmp;

        public SystemFileSystemTests()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "SysFs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); } catch { }
        }

        private string P(string name) => Path.Combine(_tmp, name);

        [Fact]
        public void FileExists_FalseForMissing_TrueForExisting()
        {
            var sut = SystemFileSystem.Instance;
            sut.FileExists(P("nope")).Should().BeFalse();

            File.WriteAllText(P("a.txt"), "x");
            sut.FileExists(P("a.txt")).Should().BeTrue();
        }

        [Fact]
        public void DirectoryExists_FalseForMissing()
        {
            SystemFileSystem.Instance.DirectoryExists(P("dir_missing")).Should().BeFalse();
            SystemFileSystem.Instance.DirectoryExists(_tmp).Should().BeTrue();
        }

        [Fact]
        public void WriteAllText_ReadAllText_RoundTrip()
        {
            var sut = SystemFileSystem.Instance;
            sut.WriteAllText(P("x.txt"), "hello");
            sut.ReadAllText(P("x.txt")).Should().Be("hello");
        }

        [Fact]
        public void WriteAllBytes_ReadAllBytes_RoundTrip()
        {
            var sut = SystemFileSystem.Instance;
            var data = Encoding.UTF8.GetBytes("binary");
            sut.WriteAllBytes(P("b.bin"), data);
            sut.ReadAllBytes(P("b.bin")).Should().BeEquivalentTo(data);
        }

        [Fact]
        public void Copy_ThenDelete()
        {
            var sut = SystemFileSystem.Instance;
            File.WriteAllText(P("src.txt"), "source");
            sut.Copy(P("src.txt"), P("dst.txt"), overwrite: false);
            sut.FileExists(P("dst.txt")).Should().BeTrue();

            sut.Delete(P("dst.txt"));
            sut.FileExists(P("dst.txt")).Should().BeFalse();
        }

        [Fact]
        public void Copy_ExistingTarget_Overwrite()
        {
            var sut = SystemFileSystem.Instance;
            File.WriteAllText(P("src.txt"), "v1");
            File.WriteAllText(P("dst.txt"), "old");
            sut.Copy(P("src.txt"), P("dst.txt"), overwrite: true);
            File.ReadAllText(P("dst.txt")).Should().Be("v1");
        }

        [Fact]
        public void Move_RemovesSourceAndCreatesTarget()
        {
            var sut = SystemFileSystem.Instance;
            File.WriteAllText(P("src.txt"), "x");
            sut.Move(P("src.txt"), P("dst.txt"));
            sut.FileExists(P("src.txt")).Should().BeFalse();
            sut.FileExists(P("dst.txt")).Should().BeTrue();
        }

        [Fact]
        public void CreateDirectory_Idempotent()
        {
            var sut = SystemFileSystem.Instance;
            sut.CreateDirectory(P("sub"));
            sut.DirectoryExists(P("sub")).Should().BeTrue();
            sut.CreateDirectory(P("sub")); // again — no exception
        }

        [Fact]
        public void GetFileSize_ReturnsBytes()
        {
            var sut = SystemFileSystem.Instance;
            File.WriteAllText(P("x.txt"), "12345"); // 5 bytes ASCII
            sut.GetFileSize(P("x.txt")).Should().Be(5);
        }

        [Fact]
        public void GetLastWriteTimeUtc_IsRecent()
        {
            var sut = SystemFileSystem.Instance;
            File.WriteAllText(P("x.txt"), "x");
            var now = DateTime.UtcNow;
            sut.GetLastWriteTimeUtc(P("x.txt")).Should().BeCloseTo(now, TimeSpan.FromMinutes(1));
        }

        [Fact]
        public void EnumerateFiles_ReturnsMatching()
        {
            var sut = SystemFileSystem.Instance;
            File.WriteAllText(P("a.txt"), "");
            File.WriteAllText(P("b.txt"), "");
            File.WriteAllText(P("c.log"), "");

            var txt = sut.EnumerateFiles(_tmp, "*.txt", SearchOption.TopDirectoryOnly);
            txt.Should().HaveCount(2);
        }

        [Fact]
        public void OpenWrite_StreamWritesContent()
        {
            var sut = SystemFileSystem.Instance;
            using (var s = sut.OpenWrite(P("s.txt")))
            {
                var b = Encoding.UTF8.GetBytes("streamed");
                s.Write(b, 0, b.Length);
            }
            File.ReadAllText(P("s.txt")).Should().Be("streamed");
        }
    }
}
