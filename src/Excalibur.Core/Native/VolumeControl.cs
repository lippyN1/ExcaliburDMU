using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Excalibur.Core.Native;

public static class VolumeControl
{
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

    public sealed class LockedVolume : IDisposable
    {
        public string MountPoint { get; }
        private readonly SafeFileHandle _handle;
        private bool _locked;

        internal LockedVolume(string mountPoint, SafeFileHandle handle)
        {
            MountPoint = mountPoint;
            _handle = handle;
            _locked = true;
        }

        public void Dispose()
        {
            if (_locked && !_handle.IsInvalid)
            {
                try
                {
                    NativeMethods.DeviceIoControl(_handle, FSCTL_UNLOCK_VOLUME,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                }
                catch { }
                _locked = false;
            }
            _handle.Dispose();
        }
    }

    public static LockedVolume LockAndDismount(string mountPoint)
    {
        var drive = mountPoint.TrimEnd('\\');
        if (!drive.EndsWith(":")) drive += ":";

        var handle = NativeMethods.CreateFileW(
            @"\\.\" + drive,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new IOException("Cannot open volume " + drive + " for lock, Win32Error=" + Marshal.GetLastWin32Error());

        bool ok = NativeMethods.DeviceIoControl(handle, FSCTL_LOCK_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("FSCTL_LOCK_VOLUME failed for " + drive + ", Win32Error=" + err + " (volume in use?)");
        }

        ok = NativeMethods.DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            NativeMethods.DeviceIoControl(handle, FSCTL_UNLOCK_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            handle.Dispose();
            throw new IOException("FSCTL_DISMOUNT_VOLUME failed for " + drive + ", Win32Error=" + err);
        }

        return new LockedVolume(drive, handle);
    }
}