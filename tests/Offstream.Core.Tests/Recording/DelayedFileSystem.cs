using System.IO.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Offstream.Core.Tests.Recording;

/// <summary>
/// Wraps a real <see cref="IFileSystem"/> and adds an artificial delay before
/// <see cref="IFileStreamFactory.New(string, FileMode, FileAccess, FileShare)"/> opens a file.
/// </summary>
/// <remarks>
/// Exists to widen the window in a race that, in reality, only ever opens for a few
/// microseconds of thread-pool scheduling latency — too narrow to reproduce deterministically
/// by simply racing real background threads even across hundreds of iterations. Stretching the
/// one step that sits before the recorder ever touches the shared capture buffer turns "usually
/// wins the race" into "always wins it," which is what a regression test needs.
/// </remarks>
internal sealed class DelayedFileSystem(IFileSystem inner, TimeSpan delay) : IFileSystem
{
    public IFile File => inner.File;

    public IDirectory Directory => inner.Directory;

    public IPath Path => inner.Path;

    public IFileInfoFactory FileInfo => inner.FileInfo;

    public IFileVersionInfoFactory FileVersionInfo => inner.FileVersionInfo;

    public IFileStreamFactory FileStream { get; } = new DelayedFileStreamFactory(inner.FileStream, delay);

    public IDirectoryInfoFactory DirectoryInfo => inner.DirectoryInfo;

    public IDriveInfoFactory DriveInfo => inner.DriveInfo;

    public IFileSystemWatcherFactory FileSystemWatcher => inner.FileSystemWatcher;

    private sealed class DelayedFileStreamFactory(IFileStreamFactory inner, TimeSpan delay) : IFileStreamFactory
    {
        public IFileSystem FileSystem => inner.FileSystem;

        public FileSystemStream New(string path, FileMode mode)
        {
            Thread.Sleep(delay);
            return inner.New(path, mode);
        }

        public FileSystemStream New(string path, FileMode mode, FileAccess access)
        {
            Thread.Sleep(delay);
            return inner.New(path, mode, access);
        }

        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share)
        {
            Thread.Sleep(delay);
            return inner.New(path, mode, access, share);
        }

        public FileSystemStream New(
            string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) =>
            inner.New(path, mode, access, share, bufferSize);

        public FileSystemStream New(
            string path,
            FileMode mode,
            FileAccess access,
            FileShare share,
            int bufferSize,
            FileOptions options) =>
            inner.New(path, mode, access, share, bufferSize, options);

        public FileSystemStream New(
            string path,
            FileMode mode,
            FileAccess access,
            FileShare share,
            int bufferSize,
            bool useAsync) =>
            inner.New(path, mode, access, share, bufferSize, useAsync);

        public FileSystemStream New(string path, FileStreamOptions options) => inner.New(path, options);

        public FileSystemStream New(SafeFileHandle handle, FileAccess access) => inner.New(handle, access);

        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize) =>
            inner.New(handle, access, bufferSize);

        public FileSystemStream New(
            SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) =>
            inner.New(handle, access, bufferSize, isAsync);

        public FileSystemStream Wrap(FileStream fileStream) => inner.Wrap(fileStream);
    }
}
