using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ZeroAccel;

internal interface IRawAccelTransport : IDisposable
{
    void VerifyDriver();
    byte[] Read(uint code,int outputSize);
    void Write(byte[] configuration);
}

// No startup writes, service management, elevation, driver install or fallback I/O.
internal sealed class RawAccelClient
{
    private readonly Func<IRawAccelTransport> factory;
    private readonly Action<string> requirePresent;
    private readonly Action<byte[]> backup;
    private int busy;
    private volatile bool timedOut;
    internal RawAccelClient(Func<IRawAccelTransport> factory,Action<string> requirePresent,Action<byte[]> backup)
    { this.factory=factory; this.requirePresent=requirePresent; this.backup=backup; }

    internal Task<RawAccelStatus> ReadAsync() => RunAsync(t => Read(t));
    internal Task<RawAccelStatus> ApplyAsync(RawAccelStatus expected,Settings settings,string id) => RunAsync(t => {
        requirePresent(id);
        var before=Read(t);
        if (!before.Configuration.AsSpan().SequenceEqual(expected.Configuration))
            throw new InvalidOperationException("Raw Accel settings changed. Refresh before applying; do not use both panels at once.");
        byte[] request=RawAccelProtocol.Prepare(before,settings,id);
        if (request.AsSpan().SequenceEqual(before.Configuration)) return before;
        backup(before.Configuration); // Failure to back up stops the write.
        requirePresent(id);
        t.Write(request); // Upstream's one-second delay is not bypassed.
        var after=Read(t);
        if (!request.AsSpan().SequenceEqual(after.Configuration))
            throw new InvalidDataException("Raw Accel readback differs. Profile state is unknown; no automatic retry or rollback.");
        return after;
    });
    private async Task<RawAccelStatus> RunAsync(Func<IRawAccelTransport,RawAccelStatus> operation)
    {
        if (timedOut) throw new InvalidOperationException("Restart the panel after a driver timeout.");
        if (Interlocked.CompareExchange(ref busy,1,0)!=0) throw new InvalidOperationException("Driver operation already in progress.");
        var work=Task.Run(() => {
            using var transport=factory();
            transport.VerifyDriver();
            return actionWithVersion(transport);
        });
        RawAccelStatus actionWithVersion(IRawAccelTransport t) {
            byte[] version=t.Read(RawAccelProtocol.VersionIoctl,12);
            if (version.Length!=12 || BinaryPrimitives.ReadInt32LittleEndian(version)!=1
                || BinaryPrimitives.ReadInt32LittleEndian(version.AsSpan(4))!=7
                || BinaryPrimitives.ReadInt32LittleEndian(version.AsSpan(8))!=0)
                throw new InvalidDataException("Only the original Raw Accel driver 1.7.0 from release 1.7.1 is supported.");
            return operation(t);
        }
        try { return await work.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) {
            timedOut=true;
            // Worker retains its handle and buffers until native completion.
            _=work.ContinueWith(t => _=t.Exception,TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
        finally { Volatile.Write(ref busy,0); }
    }
    private static RawAccelStatus Read(IRawAccelTransport t)
    {
        byte[] header=t.Read(RawAccelProtocol.ReadIoctl,RawAccelProtocol.HeaderSize);
        int required=RawAccelProtocol.FrameSize(header);
        byte[] data=required==header.Length ? header : t.Read(RawAccelProtocol.ReadIoctl,required);
        // A concurrent writer may change the size; fail rather than retry forever.
        return RawAccelProtocol.Decode(data);
    }
}

internal sealed class RawAccelTransport : IRawAccelTransport
{
    private SafeFileHandle? handle;
    public void VerifyDriver()
    {
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\rawaccel");
        if (key?.GetValue("ImagePath") is not string image) throw new Win32Exception(2);
        image=Environment.ExpandEnvironmentVariables(image.Trim('"'));
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (image.StartsWith(@"\SystemRoot\",StringComparison.OrdinalIgnoreCase)) image=Path.Combine(windows,image[12..]);
        else if (image.StartsWith(@"\??\",StringComparison.Ordinal)) image=image[4..];
        else if (image.StartsWith(@"System32\",StringComparison.OrdinalIgnoreCase)) image=Path.Combine(windows,image);
        if (!Path.IsPathFullyQualified(image) || !File.Exists(image)) throw new InvalidDataException("Raw Accel image path unavailable.");
        using (var stream=File.OpenRead(image)) {
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(RawAccelProtocol.DriverSha256,StringComparison.Ordinal))
                throw new InvalidDataException("Installed SYS does not match the original signed Raw Accel 1.7.1 package.");
        }
        // Prevent multiple mouse filters from processing the same input stack.
        foreach (string service in new[]{"ZeroAccel","ZeroAccelBypass"})
            if (IsRunning(service)) throw new InvalidOperationException("A conflicting 0Accel driver service is running. Remove it before using Raw Accel.");
        handle=CreateFile(@"\\.\rawaccel",0,0,IntPtr.Zero,3,0,IntPtr.Zero);
        if (handle.IsInvalid) { int code=Marshal.GetLastWin32Error(); handle.Dispose(); handle=null; throw new Win32Exception(code); }
    }
    public byte[] Read(uint code,int outputSize)
    {
        if (code is not (RawAccelProtocol.ReadIoctl or RawAccelProtocol.VersionIoctl)) outputSize=0;
        if (handle is null || outputSize<1 || outputSize>RawAccelProtocol.MaxBytes) throw new InvalidOperationException();
        byte[] output=new byte[outputSize];
        if (!DeviceIoControl(handle,code,null,0,output,(uint)output.Length,out uint count,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (count>output.Length) throw new InvalidDataException("Invalid Raw Accel reply size");
        Array.Resize(ref output,(int)count); return output;
    }
    public void Write(byte[] configuration)
    {
        if (handle is null) throw new InvalidOperationException();
        RawAccelProtocol.Decode(configuration);
        if (!DeviceIoControl(handle,RawAccelProtocol.WriteIoctl,configuration,(uint)configuration.Length,null,0,out _,IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() { handle?.Dispose(); handle=null; }
    private static bool IsRunning(string name)
    {
        IntPtr scm=OpenSCManager(null,null,1);
        if (scm==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            IntPtr service=OpenService(scm,name,4);
            if (service==IntPtr.Zero) { int error=Marshal.GetLastWin32Error(); if (error==1060) return false; throw new Win32Exception(error); }
            try {
                if (!QueryServiceStatus(service,out ServiceStatus status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return status.State!=1;
            } finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(scm); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { internal uint Type,State,Controls,Win32Exit,ServiceExit,Checkpoint,WaitHint; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenService(IntPtr scm,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service,out ServiceStatus status);
    [DllImport("advapi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[]? input,uint inputSize,[Out] byte[]? output,uint outputSize,out uint returned,IntPtr overlapped);
}
