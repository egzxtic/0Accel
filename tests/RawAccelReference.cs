// .NET Framework reference harness. Uses only pure Profile/ManagedAccel methods
// from the official release. Never calls DriverConfig.Activate/GetActive/Deactivate.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
unsafe class RawAccelReference {
    [StructLayout(LayoutKind.Sequential)] struct Config {
        public double sensitivity,yx,acceleration,offset,capInput,capOutput,power,decay,limit;
        public uint mode,gain,cap,rotation;
    }
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl)] static extern void zero_ra_layout([Out] uint[] sizes);
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl)] static extern double zero_ra_response(ref Config c,double speed);
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl)] static extern uint zero_ra_default([Out] byte[] output,uint size,out uint written);
    [DllImport("0Accel.RawAccel.dll",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] static extern uint zero_ra_prepare(byte[] input,uint size,ref Config c,string id,string name,[Out] byte[] output,uint cap,out uint written);
    static void Check(bool condition,string label) { if (!condition) throw new Exception(label); }
    static void Near(double a,double b,string label) { Check(!Double.IsNaN(a) && !Double.IsNaN(b) && Math.Abs(a-b)<=1e-10*Math.Max(1,Math.Abs(b)),label+": "+a+" != "+b); }
    static int Main() {
        try {
            uint[] sizes=new uint[6]; zero_ra_layout(sizes);
            Check(Marshal.SizeOf(typeof(Profile))==sizes[3],"Official profile ABI");
            Check(Marshal.SizeOf(typeof(AccelArgs))==sizes[4],"Official accel_args ABI");
            Check(Marshal.SizeOf(typeof(DeviceConfig))==sizes[5],"Official device_config ABI");
            Check(Marshal.SizeOf(typeof(DeviceSettings))==sizes[2],"Official device_settings ABI");
            Type native=typeof(Profile).Assembly.GetType("rawaccel.modifier_settings",true);
            Check(Marshal.SizeOf(native)==sizes[1],"Official modifier_settings ABI");
            Config c=new Config { sensitivity=1.25,yx=1,acceleration=.05,offset=15,capInput=120,capOutput=1.5,power=2.4,decay=.1,limit=1.5 };
            int comparisons=0;
            for (uint mode=0;mode<4;mode++) for (uint gain=0;gain<2;gain++) for (uint cap=0;cap<3;cap++) {
                c.mode=mode;c.gain=gain;c.cap=cap;
                Profile p=new Profile(); p.name="test"; p.outputDPI=c.sensitivity*1000;
                AccelArgs args=p.argsX;
                args.mode=mode==0 ? AccelMode.noaccel : mode==3 ? AccelMode.natural : AccelMode.classic;
                args.gain=gain!=0; args.acceleration=c.acceleration; args.inputOffset=c.offset;
                args.exponentClassic=mode==1 ? 2 : c.power;args.decayRate=c.decay;args.limit=c.limit;
                args.cap.x=c.capInput;args.cap.y=c.capOutput;
                args.capMode=cap==0 ? CapMode.output : cap==1 ? CapMode.input : CapMode.in_out;
                p.argsX=args;p.argsY=args;
                using (ManagedAccel official=new ManagedAccel(p)) {
                    foreach (int speed in new[]{1,5,14,15,16,20,40,80,120,1000}) {
                        var result=official.Accelerate(speed,0,1,1);
                        Near(zero_ra_response(ref c,speed),result.Item1/speed,"Official curve parity");comparisons++;
                    }
                    // Obtain the official compiler's native prepared struct by reflection;
                    // NativeSettings is a pure copy; it does not open/write driver I/O.
                    IntPtr memory=Marshal.AllocHGlobal((int)sizes[1]);
                    try {
                        // C++/CLI exposes a hidden native return-buffer argument.
                        MethodInfo method=typeof(ManagedAccel).GetMethod("NativeSettings");
                        ParameterInfo[] parameters=method.GetParameters();
                        Check(parameters.Length==1 && parameters[0].ParameterType.IsPointer,"Native return buffer ABI");
                        method.Invoke(official,new object[]{Pointer.Box(memory.ToPointer(),parameters[0].ParameterType)});
                        byte[] reference=new byte[sizes[1]];Marshal.Copy(memory,reference,0,reference.Length);
                        byte[] header=new byte[40],frame=new byte[65536];uint n;
                        Check(zero_ra_default(header,40,out n)==0,"Default frame");
                        Check(zero_ra_prepare(header,40,ref c,"HID\\TEST","test",frame,(uint)frame.Length,out n)==0,"Prepare frame");
                        int start=40+(int)sizes[1];
                        // All meaningful prepared-data slots are flags, rotation vector,
                        // and doubles in the selected classic/natural/noaccel union.
                        int extra=(int)sizes[3];
                        for(int j=0;j<7;j++) Check(frame[start+extra+j]==reference[extra+j],"Prepared flags ABI");
                        for(int j=8;j<168;j+=8) Near(BitConverter.ToDouble(frame,start+extra+j),BitConverter.ToDouble(reference,extra+j),"Prepared data ABI");
                    } finally { Marshal.FreeHGlobal(memory); }
                }
            }
            Console.WriteLine("PASS: "+comparisons+" curve vectors and native prepared structs match original Raw Accel 1.7.1 wrapper.dll (MSVC); no driver I/O.");return 0;
        } catch(Exception e) { Console.Error.WriteLine(e);return 1; }
    }
}
