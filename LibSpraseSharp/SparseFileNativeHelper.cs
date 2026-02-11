using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibSparseSharp;

public static class SparseFileNativeHelper
{
    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    /// <summary>
    /// 在 Windows 上将文件标记为稀疏文件，类似于 Linux 的稀疏文件支持
    /// </summary>
    public static void MarkAsSparse(FileStream fs)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
    }
}