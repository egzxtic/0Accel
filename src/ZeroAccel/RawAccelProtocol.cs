using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ZeroAccel;

internal sealed record RawAccelStatus(byte[] Configuration);
internal sealed record RawAccelSelection(bool Enabled, Settings? Settings);

// Pure serialization/curve operations only. Native code never opens a device.
internal static class RawAccelProtocol
{
    internal const string Release = "1.7.1", DriverVersion = "1.7.0";
    internal const string DriverSha256 = "8A62C4DEEF2774B43A7363B352EDA79897533A1080C9C26FFEFF0559E43358D7";
    internal const uint ReadIoctl = 0x88882220, WriteIoctl = 0x88882224, VersionIoctl = 0x88882228;
    internal const int HeaderSize = 40, MaxBytes = 1024 * 1024;
    internal static uint Abi => zero_ra_abi();
    internal static double Response(in CurveConfig config, double speed) => zero_ra_response(config,speed);
    internal static int FrameSize(byte[] header)
    {
        uint size=zero_ra_size(header,(uint)header.Length);
        if (size < HeaderSize || size > MaxBytes) throw new InvalidDataException("Raw Accel: invalid configuration length");
        return (int)size;
    }
    internal static RawAccelStatus Decode(byte[] bytes)
    {
        if (bytes.Length > MaxBytes || zero_ra_validate(bytes,(uint)bytes.Length)!=0)
            throw new InvalidDataException("Raw Accel: invalid configuration");
        return new RawAccelStatus((byte[])bytes.Clone());
    }
    internal static byte[] Default()
    {
        byte[] output=new byte[HeaderSize];
        Check(zero_ra_default(output,(uint)output.Length,out uint size));
        if (size!=HeaderSize) throw new InvalidDataException();
        return output;
    }
    internal static string DeviceId(string instance)
    {
        // Raw Accel uses BusQueryDeviceID, not the unique instance suffix.
        string[] parts=instance.Split('\\');
        if (parts.Length!=3 || !parts[0].Equals("HID",StringComparison.OrdinalIgnoreCase)
            || parts[1].Length==0 || parts[2].Length==0 || instance.Contains('\0') || instance.Contains('/')) return "";
        string id=parts[0]+"\\"+parts[1]; // preserve canonical PnP casing
        return id.Length<200 ? id : "";
    }
    internal static string InstanceFromRawPath(string path)
    {
        if (!path.StartsWith(@"\\?\",StringComparison.Ordinal)) return "";
        string[] parts=path[4..].Split('#');
        if (parts.Length!=4 || !Guid.TryParse(parts[3],out _)) return "";
        for (int i=0;i<3;++i)
            if (string.IsNullOrWhiteSpace(parts[i]) || parts[i].IndexOfAny(new[]{'\\','/','\0'})>=0) return "";
        string instance=string.Join("\\",parts,0,3);
        return instance.Length<256 ? instance:"";
    }
    internal static string ProfileName(string id) => "0Accel-" + Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(id)))[..24];
    internal static byte[] Prepare(RawAccelStatus current, Settings settings, string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length>=200 || id.Contains('\0')) throw new ArgumentException("Invalid target ID");
        if (settings.CurveMode is not ("off" or "linear" or "classic" or "natural")
            || settings.CapType is not ("Output" or "Input" or "Both")) throw new ArgumentException("Unsupported curve or cap mode");
        CurveConfig config=CurveConfig.From(settings);
        byte[] output=new byte[MaxBytes];
        Check(zero_ra_prepare(current.Configuration,(uint)current.Configuration.Length,config,id,ProfileName(id),
            output,(uint)output.Length,out uint written));
        if (written<HeaderSize || written>MaxBytes) throw new InvalidDataException();
        Array.Resize(ref output,(int)written);
        return Decode(output).Configuration;
    }
    internal static RawAccelSelection Inspect(RawAccelStatus current, string id)
    {
        uint result=zero_ra_inspect(current.Configuration,(uint)current.Configuration.Length,id,out CurveConfig c,out uint enabled);
        if (result==3) return new(enabled!=0,null);
        Check(result);
        return new(enabled!=0,new Settings {
            Sensitivity=c.Sensitivity,YxRatio=c.YxRatio,Acceleration=c.Acceleration,InputOffset=c.Offset,
            CapInput=c.CapInput,CapOutput=c.CapOutput,Power=c.Power,DecayRate=c.Decay,Limit=c.Limit,
            CurveMode=c.Mode switch {1=>"linear",2=>"classic",3=>"natural",_=>"off"},
            GainEnabled=c.Gain!=0,CapType=c.CapType switch {1=>"Input",2=>"Both",_=>"Output"},Rotation=(int)c.Rotation
        });
    }
    internal static bool Equivalent(Settings a, Settings b)
    {
        if (a.Sensitivity!=b.Sensitivity || a.YxRatio!=b.YxRatio || a.Rotation!=b.Rotation) return false;
        static string Mode(Settings s) {
            if ((s.CurveMode=="natural" && s.Limit==1)
                || (s.CurveMode is "linear" or "classic" && ((s.Acceleration==0 && s.CapType!="Both")
                    || (s.CapType!="Input" && s.CapOutput==1)))) return "off";
            return s.CurveMode=="classic" && s.Power==2 ? "linear" : s.CurveMode;
        }
        if (Mode(a)!=Mode(b)) return false;
        if (Mode(a)=="off") return true;
        if (a.GainEnabled!=b.GainEnabled || a.InputOffset!=b.InputOffset) return false;
        if (a.CurveMode=="natural") return a.DecayRate==b.DecayRate && a.Limit==b.Limit;
        return a.CapType==b.CapType && (a.CapType=="Both" || a.Acceleration==b.Acceleration)
            && (a.CapType=="Output" || a.CapInput==b.CapInput)
            && (a.CapType=="Input" || a.CapOutput==b.CapOutput)
            && (Mode(a)=="linear" || a.Power==b.Power);
    }
    private static void Check(uint result)
    {
        if (result!=0) throw new InvalidDataException("Raw Accel bridge: " + result);
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl)] private static extern uint zero_ra_abi();
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl)] private static extern double zero_ra_response(in CurveConfig config,double speed);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl)] private static extern uint zero_ra_size(byte[] input,uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl)] private static extern uint zero_ra_validate(byte[] input,uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl)] private static extern uint zero_ra_default([Out] byte[] output,uint cap,out uint written);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    private static extern uint zero_ra_prepare(byte[] input,uint size,in CurveConfig config,string id,string name,[Out] byte[] output,uint cap,out uint written);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]
    private static extern uint zero_ra_inspect(byte[] input,uint size,string id,out CurveConfig config,out uint enabled);
}
