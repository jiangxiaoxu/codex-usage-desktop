using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexUsage.Infrastructure.Collection;

internal interface ISourceIdentityReader
{
    SourceIdentity Read(FileStream stream, string filePath, long sizeBytes, long modifiedAtEpochMs);
}

internal sealed class WindowsSourceIdentityReader : ISourceIdentityReader
{
    public SourceIdentity Read(FileStream stream, string filePath, long sizeBytes, long modifiedAtEpochMs)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (OperatingSystem.IsWindows())
        {
            var info = new FileIdInfo();
            if (GetFileInformationByHandleEx(
                    stream.SafeFileHandle,
                    FileInfoByHandleClass.FileIdInfo,
                    out info,
                    checked((uint)Marshal.SizeOf<FileIdInfo>())))
            {
                return new SourceIdentity(
                    SourceIdentityKind.WindowsFileId,
                    $"{info.VolumeSerialNumber:x16}:{info.FileIdHigh:x16}{info.FileIdLow:x16}");
            }

            var error = Marshal.GetLastWin32Error();
            if (error is not (1 or 50 or 87))
                throw new Win32Exception(error, "Unable to read rollout source file identity.");
        }

        return new SourceIdentity(
            SourceIdentityKind.ConservativeStat,
            $"{Path.GetFullPath(filePath)}|{sizeBytes}|{modifiedAtEpochMs}");
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);
}
