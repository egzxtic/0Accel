using System.Runtime.InteropServices;

namespace ZeroAccel;

[StructLayout(LayoutKind.Sequential)]
internal struct CurveConfig
{
    internal double Sensitivity, YxRatio, Acceleration, Offset, CapInput, CapOutput, Power, Decay, Limit;
    internal uint Mode, Gain, CapType, Rotation;

    internal static CurveConfig From(Settings s) => new()
    {
        Sensitivity=s.Sensitivity, YxRatio=s.YxRatio, Acceleration=s.Acceleration, Offset=s.InputOffset,
        CapInput=s.CapInput, CapOutput=s.CapOutput, Power=s.Power, Decay=s.DecayRate, Limit=s.Limit,
        Mode=s.CurveMode switch { "linear"=>1u, "classic"=>2u, "natural"=>3u, _=>0u },
        Gain=s.GainEnabled ? 1u:0u,
        CapType=s.CapType switch { "Input"=>1u, "Both"=>2u, _=>0u },
        Rotation=(uint)s.Rotation
    };
}
