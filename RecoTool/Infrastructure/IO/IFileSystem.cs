using System;
using System.Collections.Generic;
using System.IO;

namespace RecoTool.Infrastructure.IO
{
    /// <summary>
    /// Minimal file-system abstraction used by services that want to remain
    /// unit-testable. Inspired by <c>System.IO.Abstractions</c> but kept tiny:
    /// only the members actually used by RecoTool services are exposed.
    ///
    /// <para>
    /// Production binding: <see cref="SystemFileSystem"/>. Tests inject a fake
    /// (e.g. an in-memory dictionary-backed implementation).
    /// </para>
    /// </summary>
    public interface IFileSystem
    {
        // ----- Existence / metadata -----
        bool FileExists(string path);
        bool DirectoryExists(string path);
        long GetFileSize(string path);
        DateTime GetLastWriteTimeUtc(string path);

        // ----- Reading / writing -----
        Stream OpenRead(string path);
        Stream OpenWrite(string path);
        Stream Create(string path);
        byte[] ReadAllBytes(string path);
        void WriteAllBytes(string path, byte[] bytes);
        string ReadAllText(string path);
        void WriteAllText(string path, string contents);

        // ----- Operations -----
        void Copy(string sourcePath, string destinationPath, bool overwrite);
        void Move(string sourcePath, string destinationPath);
        void Delete(string path);
        void CreateDirectory(string path);

        // ----- Enumeration -----
        IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);
    }

    /// <summary>
    /// Default production implementation that delegates to <see cref="System.IO"/>.
    /// Stateless and thread-safe.
    /// </summary>
    public sealed class SystemFileSystem : IFileSystem
    {
        public static readonly SystemFileSystem Instance = new SystemFileSystem();

        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public long GetFileSize(string path) => new FileInfo(path).Length;
        public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

        public Stream OpenRead(string path) => File.OpenRead(path);
        public Stream OpenWrite(string path) => File.OpenWrite(path);
        public Stream Create(string path) => File.Create(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

        public void Copy(string sourcePath, string destinationPath, bool overwrite)
            => File.Copy(sourcePath, destinationPath, overwrite);
        public void Move(string sourcePath, string destinationPath)
            => File.Move(sourcePath, destinationPath);
        public void Delete(string path) => File.Delete(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
            => Directory.EnumerateFiles(path, searchPattern, searchOption);
    }
}
