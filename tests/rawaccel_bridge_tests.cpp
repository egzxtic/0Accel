// Offline only: includes the actual adapter, with no Windows device operations.
#undef NDEBUG
#include "../tools/RawAccelBridge/rawaccel_bridge.cpp"
#include <cassert>
#include <cstdio>
#include <random>

static curve_config cfg{1,1,.05,15,120,1.5,2,.1,1.5,1,1,0,0};
static std::vector<uint8_t> bytes(frame f) {
    std::vector<uint8_t> out(max_bytes); uint32_t count=0;
    assert(encode(f,out.data(),static_cast<uint32_t>(out.size()),&count)==ok);
    out.resize(count); assert(zero_ra_validate(out.data(),count)==ok); return out;
}
static std::vector<uint8_t> prepare(const std::vector<uint8_t>& before,const wchar_t* id,const wchar_t* name) {
    std::vector<uint8_t> out(max_bytes); uint32_t count=0;
    assert(zero_ra_prepare(before.data(),static_cast<uint32_t>(before.size()),&cfg,id,name,out.data(),max_bytes,&count)==ok);
    out.resize(count); assert(zero_ra_validate(out.data(),count)==ok); return out;
}
static frame unpack(const std::vector<uint8_t>& b) { frame f; assert(decode(b.data(),static_cast<uint32_t>(b.size()),f)); return f; }
int main() {
    assert(zero_ra_abi()==1);
    uint32_t layout[6]; zero_ra_layout(layout);
    std::printf("ABI: base=%u modifier=%u device=%u profile=%u args=%u device_config=%u\n",layout[0],layout[1],layout[2],layout[3],layout[4],layout[5]);
    auto empty=bytes({}); auto first=prepare(empty,L"HID\\VID_0001&Col01",L"0Accel-test");
    frame f=unpack(first);
    assert(f.profiles.size()==2 && f.devices.size()==1 && f.base.default_dev_cfg.disable);
    assert(f.profiles[0].prof.accel_x.mode==ra::accel_mode::noaccel);
    assert(!f.devices[0].config.disable && f.devices[0].config.dpi==0 && f.devices[0].config.polling_rate==0);
    curve_config read{}; uint32_t enabled=0;
    assert(zero_ra_inspect(first.data(),static_cast<uint32_t>(first.size()),L"HID\\VID_0001&Col01",&read,&enabled)==ok && enabled);
    assert(read.acceleration==.05 && read.cap_output==1.5 && read.offset==15 && read.mode==1 && read.gain==1);
    f.devices[0].config.disable=true;
    auto disabled=bytes(f);
    read={}; enabled=1;
    assert(zero_ra_inspect(disabled.data(),static_cast<uint32_t>(disabled.size()),L"HID\\VID_0001&Col01",&read,&enabled)==ok && !enabled);
    assert(read.acceleration==.05 && read.cap_output==1.5 && read.offset==15 && read.mode==1 && read.gain==1);
    f.devices[0].config.disable=false;
    // Another user's global profile, device and padding must remain byte-for-byte.
    f.base.default_dev_cfg.disable=false;
    f.profiles[0].prof.output_dpi=1300; ra::init_data(f.profiles[0]);
    ra::device_settings other{}; assign(other.id,L"HID\\OTHER"); assign(other.profile,L"default"); other.config.dpi=1600;
    f.devices.push_back(other);
    auto original=bytes(f); f=unpack(original); cfg.sensitivity=1.8;
    auto after=unpack(prepare(original,L"HID\\VID_0001&Col01",L"0Accel-test"));
    assert(std::memcmp(&after.base,&f.base,sizeof(f.base))==0);
    assert(std::memcmp(&after.profiles[0],&f.profiles[0],sizeof(ra::modifier_settings))==0);
    assert(std::memcmp(&after.devices[1],&f.devices[1],sizeof(other))==0);
    auto rejected=[&](frame value,uint32_t expected) {
        auto b=bytes(value); std::vector<uint8_t> out(max_bytes,0xCC); uint32_t written=999;
        assert(zero_ra_prepare(b.data(),static_cast<uint32_t>(b.size()),&cfg,L"HID\\VID_0001&Col01",L"0Accel-test",out.data(),max_bytes,&written)==expected);
        assert(written==0 && out[0]==0xCC);
    };
    assign(f.devices[1].profile,L"0Accel-test"); rejected(f,collision);
    assign(f.devices[1].profile,L"default");
    // Refuse to claim that advanced/unsupported profiles can be shown faithfully.
    f.profiles[1].prof.speed_processor_args.whole=false; ra::init_data(f.profiles[1]);
    auto advanced=bytes(f);
    assert(zero_ra_inspect(advanced.data(),static_cast<uint32_t>(advanced.size()),L"HID\\VID_0001&Col01",&read,&enabled)==unsupported && enabled);
    f.profiles[1].prof.speed_processor_args.whole=true;
    f.profiles[1].prof.degrees_rotation=1e100; ra::init_data(f.profiles[1]);
    advanced=bytes(f);
    assert(zero_ra_inspect(advanced.data(),static_cast<uint32_t>(advanced.size()),L"HID\\VID_0001&Col01",&read,&enabled)==unsupported);
    assert(zero_ra_validate(nullptr,0)==invalid);
    for (size_t n=0;n<first.size();n+=7) assert(zero_ra_validate(first.data(),static_cast<uint32_t>(n))==invalid);
    auto invalid_bool=first; invalid_bool[0]=2;
    assert(zero_ra_validate(invalid_bool.data(),static_cast<uint32_t>(invalid_bool.size()))==invalid);
    auto invalid_count=empty; std::memset(invalid_count.data()+32,255,8);
    assert(zero_ra_size(invalid_count.data(),40)==0);
    std::mt19937 rng(42);
    for (int i=0;i<10000;++i) {
        auto b=first;
        b[rng()%b.size()]=static_cast<uint8_t>(rng());
        if (zero_ra_validate(b.data(),static_cast<uint32_t>(b.size()))==ok)
            (void)zero_ra_inspect(b.data(),static_cast<uint32_t>(b.size()),L"HID\\VID_0001&Col01",&read,&enabled);
    }
    cfg.sensitivity=1;
    assert(std::abs(zero_ra_response(&cfg,40)-1.28125)<1e-12);
    assert(std::abs(zero_ra_response(&cfg,80)-1.390625)<1e-12);
    for (uint32_t mode=0;mode<4;++mode) for (uint32_t gain=0;gain<2;++gain) for (uint32_t cap=0;cap<3;++cap) {
        cfg.mode=mode; cfg.gain=gain; cfg.cap_type=cap;
        for (double accel : {0.,.05,.2}) for (double limit : {1.,1.5,16.}) {
            cfg.acceleration=accel; cfg.limit=cfg.cap_output=limit;
            ra::modifier_settings prepared; assert(make_modifier(cfg,L"test",prepared));
            ra::modifier m(prepared); ra::speed_processor s; s.init(prepared.prof.speed_processor_args);
            for (double speed : {0.,.001,14.99,15.,15.01,40.,80.,120.,1000.}) {
                double value=zero_ra_response(&cfg,speed); assert(std::isfinite(value) && value>=.99);
                vec2d v{speed,speed}; m.modify(v,s,prepared,1,1);
                assert(std::isfinite(v.x) && std::isfinite(v.y));
            }
        }
    }
    cfg.sensitivity=std::numeric_limits<double>::quiet_NaN();
    assert(std::isnan(zero_ra_response(&cfg,1)));
    std::puts("PASS: Raw Accel frame isolation, malformed buffers, 10000 mutations, flat curves and prepared curve execution (offline).");
}
