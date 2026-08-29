using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace ZeroAccel;

internal sealed record MouseDevice(IntPtr Handle, string Name, string InstanceId = "");

/* Passive observation only, registered for a measurement or the opt-in live marker.
 * No input hooks, SendInput, injected movement, game handles or background sink. */
internal sealed class MouseProbe : IDisposable
{
    private const uint RidInput = 0x10000003;
    private readonly HwndSource source;
    private IntPtr selected;
    private long first, last, bestSpan;
    private int count, bestCount;
    private readonly MotionSampler motion = new(Stopwatch.Frequency);
    internal bool IsMeasuring { get; private set; }
    internal bool IsTracking { get; private set; }
    internal bool IsRegistered { get; private set; }
    internal event Action<double>? MotionSampled;
    internal MouseProbe(IntPtr window)
    {
        source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException();
        source.AddHook(Hook);
    }

    internal static List<MouseDevice> Enumerate()
    {
        var result = new List<MouseDevice>();
        uint count = 0;
        uint size = (uint)Marshal.SizeOf<DeviceList>();
        if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count > 128) return result;
        var list = new DeviceList[count];
        uint actual = GetRawInputDeviceList(list, ref count, size);
        if (actual == uint.MaxValue) return result;
        for (int i = 0; i < actual; i++)
        {
            if (list[i].Type != 0) continue;
            uint length = 0;
            GetRawInputDeviceInfo(list[i].Device, 0x20000007, null, ref length);
            if (length == 0 || length > 4096) continue;
            var path = new StringBuilder((int)length);
            if (GetRawInputDeviceInfo(list[i].Device, 0x20000007, path, ref length) == uint.MaxValue) continue;
            string name = "Mysz HID";
            using (var handle = CreateFile(path.ToString(), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                var product = new StringBuilder(128);
                if (!handle.IsInvalid && HidD_GetProductString(handle, product, 256) && !string.IsNullOrWhiteSpace(product.ToString()))
                    name = product.ToString().Trim();
            }
            if (result.Exists(d => d.Name == name)) name += " · " + (result.Count+1);
            string instance=RawAccelProtocol.InstanceFromRawPath(path.ToString());
            if (CM_Locate_DevNodeW(out uint devNode,instance,0)==0)
            {
                var canonical=new StringBuilder(256);
                if (CM_Get_Device_IDW(devNode,canonical,canonical.Capacity,0)==0) instance=canonical.ToString();
                else instance="";
            }
            else instance="";
            result.Add(new MouseDevice(list[i].Device, name, instance));
        }
        return result;
    }

    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint devNode,string id,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint devNode,StringBuilder id,int length,uint flags);

    internal bool Start(IntPtr device)
    {
        Stop();
        if (!EnsureRegistered(device)) return false;
        first = last = bestSpan = 0; count = bestCount = 0;
        IsMeasuring = true;
        return true;
    }

    internal double? Stop()
    {
        KeepBest();
        IsMeasuring = false;
        ReleaseIfUnused();
        return bestCount >= 30 && bestSpan >= Stopwatch.Frequency/4
            ? (bestCount-1) * (double)Stopwatch.Frequency / bestSpan : null;
    }

    internal bool StartTracking(IntPtr device)
    {
        if (IsTracking && selected == device) return true;
        StopTracking();
        if (!EnsureRegistered(device)) return false;
        motion.Reset(); IsTracking = true;
        return true;
    }

    internal void StopTracking()
    {
        IsTracking = false; motion.Reset();
        ReleaseIfUnused();
    }

    private bool EnsureRegistered(IntPtr device)
    {
        if (device == IntPtr.Zero) return false;
        if (IsRegistered) return selected == device;
        // Flags=0: foreground only; do not suppress legacy events or use INPUTSINK.
        var input = new[] { new Registration { UsagePage = 1, Usage = 2, Target = source.Handle } };
        if (!RegisterRawInputDevices(input, 1, (uint)Marshal.SizeOf<Registration>())) return false;
        selected = device; IsRegistered = true;
        return true;
    }

    private void ReleaseIfUnused()
    {
        if (!IsRegistered || IsMeasuring || IsTracking) return;
        var remove = new[] { new Registration { UsagePage = 1, Usage = 2, Flags = 1, Target = IntPtr.Zero } };
        if (RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<Registration>())) IsRegistered = false;
    }

    private void KeepBest()
    {
        long span = last-first;
        if (count >= 2 && span > bestSpan) { bestSpan = span; bestCount = count; }
    }

    private unsafe IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message != 0xFF || (!IsMeasuring && !IsTracking)) return IntPtr.Zero;
        byte* buffer = stackalloc byte[64];
        uint size = 64;
        uint read = GetRawInputData(lparam, RidInput, (IntPtr)buffer, ref size, (uint)Marshal.SizeOf<InputHeader>());
        if (read < sizeof(RawMouseInput) || read > 64) return IntPtr.Zero;
        var input = *(RawMouseInput*)buffer;
        if (input.Header.Type != 0 || input.Header.Device != selected || (input.Mouse.Flags & 1) != 0
            || (input.Mouse.X == 0 && input.Mouse.Y == 0)) return IntPtr.Zero;
        long now = Stopwatch.GetTimestamp();
        if (IsMeasuring)
        {
            if (count > 0 && now-last > Stopwatch.Frequency/50) { KeepBest(); first = 0; count = 0; }
            if (count == 0) first = now;
            last = now; count++;
        }
        if (IsTracking && motion.Add(input.Mouse.X, input.Mouse.Y, now, out double speed)) MotionSampled?.Invoke(speed);
        return IntPtr.Zero; // leave foreground WM_INPUT cleanup to DefWindowProc
    }

    public void Dispose() { IsTracking = false; motion.Reset(); Stop(); MotionSampled = null; source.RemoveHook(Hook); }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceList { public IntPtr Device; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] private struct Registration { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct InputHeader { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public ushort Flags; public uint Buttons, RawButtons; public int X, Y; public uint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct RawMouseInput { public InputHeader Header; public MouseData Mouse; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([In, Out] DeviceList[]? list, ref uint count, uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] Registration[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr data, uint command, IntPtr buffer, ref uint size, uint headerSize);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle device, StringBuilder buffer, uint length);
}
