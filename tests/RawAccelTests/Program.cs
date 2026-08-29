using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ZeroAccel;

if (args.Length!=0) {
    if (!args.SequenceEqual(new[]{"--read-only-installed"})) throw new ArgumentException("Unknown option");
    // Explicit diagnostic opt-in. This branch only invokes the production reader;
    // target/write-backup callbacks fail if accidentally reached. No IDs are logged.
    var reader=new RawAccelClient(()=>new RawAccelTransport(),_=>throw new Exception("Unexpected target operation"),_=>throw new Exception("Unexpected backup/write"));
    var snapshot=await reader.ReadAsync();
    Console.WriteLine($"PASS: installed SYS matches pinned release {RawAccelProtocol.Release}; endpoint {RawAccelProtocol.DriverVersion}; configuration validated ({snapshot.Configuration.Length} bytes, {BinaryPrimitives.ReadUInt32LittleEndian(snapshot.Configuration.AsSpan(32))} profiles, {BinaryPrimitives.ReadUInt32LittleEndian(snapshot.Configuration.AsSpan(36))} device overrides). READ ONLY; no profile applied.");
    return;
}

const string target=@"HID\VID_0001&Col01";
var settings=new Settings { CurveMode="linear", Acceleration=.05, CapOutput=1.5, InputOffset=15 };
void Check(bool b,string message) { if (!b) throw new Exception(message); }
async Task Fails(Func<Task> action,string name) {
    try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or TimeoutException or ArgumentException) { return; }
    throw new Exception("Expected rejection: "+name);
}
Check(Marshal.SizeOf<CurveConfig>()==88 && RawAccelProtocol.Abi==1,"ABI");
Check(RawAccelProtocol.DeviceId(@"HID\VID_0001&Col01\7&instance")==target,"Canonical PnP case");
Check(RawAccelProtocol.DeviceId(@"USB\VID_0001\instance")=="","Non-HID rejected");
const string rawPath=@"\\?\HID#VID_0001&PID_0002&MI_00#7&ABC&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}";
Check(RawAccelProtocol.InstanceFromRawPath(rawPath)==@"HID\VID_0001&PID_0002&MI_00\7&ABC&0&0000","Raw Input path");
foreach (string bad in new[]{"",@"\\?\HID#DEVICE#INSTANCE#not-a-guid",@"\\?\HID#DEVICE#bad/path#{378de44c-56ef-11d1-bc8c-00a0c91405dd}"})
    Check(RawAccelProtocol.InstanceFromRawPath(bad)=="","Malformed Raw Input path rejected");
var initial=RawAccelProtocol.Decode(RawAccelProtocol.Default());
var applied=RawAccelProtocol.Decode(RawAccelProtocol.Prepare(initial,settings,target));
var selection=RawAccelProtocol.Inspect(applied,target);
Check(selection.Enabled && selection.Settings is not null && RawAccelProtocol.Equivalent(settings,selection.Settings),"Round trip");
Check(!RawAccelProtocol.Inspect(applied,@"HID\OTHER").Enabled,"Unselected devices remain disabled");
foreach (var flat in new[]{settings with { Acceleration=0 },settings with { CapOutput=1 },settings with { CurveMode="natural",Limit=1 }}) {
    var value=RawAccelProtocol.Inspect(RawAccelProtocol.Decode(RawAccelProtocol.Prepare(initial,flat,target)),target);
    Check(value.Settings is not null && RawAccelProtocol.Equivalent(flat,value.Settings),"Degenerate flat curve equivalence");
}
await Fails(()=>Task.Run(()=>RawAccelProtocol.Prepare(initial,settings with { CurveMode="jump" },target)),"Unsupported mode");
var fake=new Fake(); int backups=0,presence=0;
var client=new RawAccelClient(()=>fake,id=>{Check(id==target,"Wrong target");presence++;},_=>backups++);
var read=await client.ReadAsync();
Check(fake.Writes==0 && backups==0 && presence==0,"Read must never write or back up");
await client.ApplyAsync(read,settings,target);
Check(fake.Writes==1 && backups==1 && presence==2,"Explicit apply requires backup and two presence checks");
Check(RawAccelProtocol.Inspect(RawAccelProtocol.Decode(fake.Data),target).Enabled,"Apply readback");
await Fails(()=>client.ApplyAsync(read,settings,target),"Stale snapshot");
Check(fake.Writes==1,"Stale apply must not overwrite");
fake=new Fake { VersionMajor=9 };
client=new RawAccelClient(()=>fake,_=>{},_=>{});
await Fails(()=>client.ApplyAsync(initial,settings,target),"Version mismatch");
Check(fake.Writes==0 && fake.ConfigurationReads==0,"Unknown ABI must not read configuration or write");
fake=new Fake(); client=new RawAccelClient(()=>fake,_=>throw new InvalidOperationException("Detached"),_=>{});
await Fails(()=>client.ApplyAsync(initial,settings,target),"Detached device"); Check(fake.Writes==0,"Detached write");
fake=new Fake(); client=new RawAccelClient(()=>fake,_=>{},_=>throw new IOException("Disk full"));
await Fails(()=>client.ApplyAsync(initial,settings,target),"Failed backup"); Check(fake.Writes==0,"Backup failed write");
fake=new Fake { CorruptReadback=true }; client=new RawAccelClient(()=>fake,_=>{},_=>{});
await Fails(()=>client.ApplyAsync(initial,settings,target),"Readback mismatch"); Check(fake.Writes==1,"Must not retry/rollback automatically");
fake=new Fake { Data=new byte[40] }; client=new RawAccelClient(()=>fake,_=>{},_=>{});
await Fails(()=>client.ReadAsync(),"Malformed configuration"); Check(fake.Writes==0,"Malformed write");
fake=new Fake { DelayMs=5500 }; client=new RawAccelClient(()=>fake,_=>{},_=>{});
var inflight=client.ReadAsync();
await Fails(()=>client.ReadAsync(),"Concurrent operation");
await Fails(()=>inflight,"Timeout");
Check(!fake.Disposed,"In-flight native handle must remain owned by worker");
await Fails(()=>client.ReadAsync(),"No retry after timeout");
await Task.Delay(750); Check(fake.Disposed && fake.Writes==0,"Timeout completion cleanup");
Console.WriteLine("PASS: Raw Accel managed ABI, selected-device roundtrip, read-only startup, explicit apply, stale/version/detach/backup/readback/malformed/timeout guards; fake transport, no real driver I/O.");

sealed class Fake : IRawAccelTransport {
    public byte[] Data=RawAccelProtocol.Default();
    public int Writes,ConfigurationReads,VersionMajor=1,DelayMs;
    public bool CorruptReadback,Disposed;
    public void VerifyDriver() { }
    public byte[] Read(uint code,int size) {
        if (code==RawAccelProtocol.VersionIoctl) {
            if (DelayMs>0) Thread.Sleep(DelayMs);
            byte[] b=new byte[12]; BinaryPrimitives.WriteInt32LittleEndian(b,VersionMajor); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4),7); return b;
        }
        if (code!=RawAccelProtocol.ReadIoctl) throw new Exception("Unexpected IOCTL");
        ConfigurationReads++;
        return Data[..Math.Min(size,Data.Length)];
    }
    public void Write(byte[] data) { Writes++; if (!CorruptReadback) Data=(byte[])data.Clone(); }
    public void Dispose() { Disposed=true; }
}
