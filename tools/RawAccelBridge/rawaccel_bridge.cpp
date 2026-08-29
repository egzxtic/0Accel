// 0Accel's user-mode adapter. No device handles, input hooks or kernel code.
// The unmodified upstream headers supply both curve evaluation and wire layout.
#define _USE_MATH_DEFINES
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <cmath>
#include <vector>
#include <limits>
namespace rawaccel {}
namespace ra = rawaccel;
#include <rawaccel.hpp>
#include <rawaccel-validate.hpp>
#include <rawaccel-version.h>

#define API extern "C" __declspec(dllexport)
struct curve_config {
    double sensitivity, yx, acceleration, offset, cap_input, cap_output, power, decay, limit;
    uint32_t mode, gain, cap_type, rotation;
};
static_assert(sizeof(wchar_t) == 2 && sizeof(bool) == 1 && sizeof(curve_config) == 88);
static_assert(sizeof(ra::io_base) == 40 && sizeof(ra::device_config) == 32);
constexpr uint32_t max_profiles = 32, max_devices = 128, max_bytes = 1024 * 1024;
enum { ok = 0, invalid = 1, capacity = 2, unsupported = 3, collision = 4 };

template<size_t N> bool terminated(const wchar_t (&s)[N]) {
    for (auto c : s) if (!c) return true;
    return false;
}
template<size_t N> bool assign(wchar_t (&out)[N], const wchar_t* s) {
    if (!s) return false;
    size_t n = 0;
    while (n < N && s[n]) ++n;
    if (n == 0 || n == N) return false;
    std::memset(out, 0, sizeof(out)); std::memcpy(out, s, n * 2); return true;
}
bool same(const wchar_t* a, const wchar_t* b) { return std::wcscmp(a,b) == 0; }
bool finite_range(double n, double lo, double hi) { return std::isfinite(n) && n >= lo && n <= hi; }
bool valid_config(const curve_config& c) {
    return finite_range(c.sensitivity,.1,24) && finite_range(c.yx,.25,8)
        && finite_range(c.acceleration,0,.2) && finite_range(c.offset,0,200)
        && finite_range(c.cap_input,.1,1600) && finite_range(c.cap_output,1,16)
        && finite_range(c.power,1.01,5) && finite_range(c.decay,.001,10)
        && finite_range(c.limit,1,16) && c.mode <= 3 && c.gain <= 1 && c.cap_type <= 2
        && (c.rotation == 0 || c.rotation == 90 || c.rotation == 180 || c.rotation == 270)
        && (!(c.mode == 1 || c.mode == 2) || c.cap_type == 0 || c.cap_input > c.offset);
}
bool make_modifier(const curve_config& c, const wchar_t* name, ra::modifier_settings& s) {
    if (!valid_config(c)) return false;
    s = {};
    if (!assign(s.prof.name,name)) return false;
    auto& p = s.prof;
    p.output_dpi = c.sensitivity * 1000;
    p.yx_output_dpi_ratio = c.yx; p.degrees_rotation = c.rotation;
    auto& a = p.accel_x;
    a.mode = c.mode == 3 ? ra::accel_mode::natural : c.mode ? ra::accel_mode::classic : ra::accel_mode::noaccel;
    // Avoid undefined flat-curve intermediates in upstream's constructors.
    if ((c.mode == 3 && c.limit == 1) || ((c.mode == 1 || c.mode == 2)
        && ((c.acceleration == 0 && c.cap_type != 2) || (c.cap_type != 1 && c.cap_output == 1))))
        a.mode = ra::accel_mode::noaccel;
    a.gain = c.gain != 0; a.input_offset = c.offset;
    a.acceleration = c.acceleration > 0 ? c.acceleration : .005;
    a.exponent_classic = c.mode == 1 ? 2 : c.power;
    a.decay_rate = c.decay; a.limit = c.limit;
    a.cap = {c.cap_input,c.cap_output};
    a.cap_mode = c.cap_type == 0 ? ra::cap_mode::out : c.cap_type == 1 ? ra::cap_mode::in : ra::cap_mode::io;
    p.accel_y = a;
    if (!ra::valid(p)) return false;
    ra::init_data(s);
    return true;
}

struct frame {
    ra::io_base base{};
    std::vector<ra::modifier_settings> profiles;
    std::vector<ra::device_settings> devices;
};
uint32_t frame_size(const ra::io_base& base) {
    if (base.modifier_data_size > max_profiles || base.device_data_size > max_devices
        || (base.modifier_data_size == 0 && base.device_data_size != 0)) return 0;
    auto size = sizeof(base) + base.modifier_data_size * sizeof(ra::modifier_settings)
        + base.device_data_size * sizeof(ra::device_settings);
    return size <= max_bytes ? static_cast<uint32_t>(size) : 0;
}
bool valid_device(const uint8_t* bytes) {
    if (bytes[0] > 1 || bytes[1] > 1 || bytes[2] > 1) return false;
    ra::device_config c; std::memcpy(&c,bytes,sizeof(c));
    return c.dpi >= 0 && c.dpi <= 100000 && c.polling_rate >= 0 && c.polling_rate <= 8000
        && finite_range(c.clamp.min,.000001,10000) && finite_range(c.clamp.max,c.clamp.min,10000);
}
bool valid_args(const uint8_t* bytes) {
    if (bytes[offsetof(ra::accel_args,gain)] > 1) return false;
    ra::accel_args a; std::memcpy(&a,bytes,sizeof(a));
    if (static_cast<unsigned>(a.mode) > 6 || static_cast<unsigned>(a.cap_mode) > 2
        || a.length < 0 || a.length > 514) return false;
    for (double v : {a.input_offset,a.output_offset,a.acceleration,a.decay_rate,a.gamma,a.motivity,
        a.exponent_classic,a.scale,a.exponent_power,a.limit,a.sync_speed,a.smooth,a.cap.x,a.cap.y})
        if (!std::isfinite(v)) return false;
    for (float v : a.data) if (!std::isfinite(v)) return false;
    return true;
}
bool decode(const uint8_t* bytes, uint32_t size, frame& f) {
    if (!bytes || size < sizeof(f.base) || !valid_device(bytes)) return false;
    std::memcpy(&f.base,bytes,sizeof(f.base));
    if (!frame_size(f.base) || frame_size(f.base) != size) return false;
    const auto* cursor = bytes + sizeof(f.base);
    f.profiles.resize(f.base.modifier_data_size);
    for (auto& s : f.profiles) {
        const auto* p = cursor; // verified ABI: profile is the first member
        if (!valid_args(p + offsetof(ra::profile,accel_x)) || !valid_args(p + offsetof(ra::profile,accel_y))
            || p[offsetof(ra::profile,speed_processor_args)] > 1) return false;
        const auto data_offset = reinterpret_cast<const uint8_t*>(&s.data) - reinterpret_cast<const uint8_t*>(&s);
        const auto* flags = cursor + data_offset;
        for (size_t k=0;k<sizeof(ra::modifier_flags);++k) if (flags[k] > 1) return false;
        std::memcpy(&s,cursor,sizeof(s)); cursor += sizeof(s);
        if (!terminated(s.prof.name) || !s.prof.name[0] || !ra::valid(s.prof)) return false;
        const auto& q = s.prof;
        for (double v : {q.domain_weights.x,q.domain_weights.y,q.range_weights.x,q.range_weights.y,
            q.output_dpi,q.yx_output_dpi_ratio,q.lr_output_dpi_ratio,q.ud_output_dpi_ratio,
            q.degrees_rotation,q.degrees_snap,q.speed_min,q.speed_max,q.speed_processor_args.lp_norm,
            q.speed_processor_args.input_speed_smooth_halflife,q.speed_processor_args.scale_smooth_halflife,
            q.speed_processor_args.output_speed_smooth_halflife}) if (!std::isfinite(v)) return false;
    }
    f.devices.resize(f.base.device_data_size);
    for (auto& d : f.devices) {
        if (!valid_device(cursor + offsetof(ra::device_settings,config))) return false;
        std::memcpy(&d,cursor,sizeof(d)); cursor += sizeof(d);
        if (!terminated(d.id) || !d.id[0] || !terminated(d.name) || !terminated(d.profile)) return false;
    }
    for (size_t i=0;i<f.profiles.size();++i)
        for (size_t j=0;j<i;++j) if (same(f.profiles[i].prof.name,f.profiles[j].prof.name)) return false;
    for (size_t i=0;i<f.devices.size();++i) {
        for (size_t j=0;j<i;++j) if (same(f.devices[i].id,f.devices[j].id)) return false;
        if (f.devices[i].profile[0]) {
            bool found=false;
            for (const auto& s : f.profiles) found |= same(s.prof.name,f.devices[i].profile);
            if (!found) return false;
        }
    }
    return true;
}
uint32_t encode(frame& f, uint8_t* output, uint32_t cap, uint32_t* written) {
    f.base.modifier_data_size=static_cast<uint32_t>(f.profiles.size());
    f.base.device_data_size=static_cast<uint32_t>(f.devices.size());
    uint32_t size=frame_size(f.base);
    if (!output || !written || size == 0 || cap < size) return capacity;
    std::memcpy(output,&f.base,sizeof(f.base));
    auto* cursor=output+sizeof(f.base);
    for (const auto& s : f.profiles) { std::memcpy(cursor,&s,sizeof(s)); cursor+=sizeof(s); }
    for (const auto& d : f.devices) { std::memcpy(cursor,&d,sizeof(d)); cursor+=sizeof(d); }
    *written=size; return ok;
}

API uint32_t zero_ra_abi() { return 1; }
API void zero_ra_layout(uint32_t* sizes) {
    if (!sizes) return;
    sizes[0]=sizeof(ra::io_base); sizes[1]=sizeof(ra::modifier_settings);
    sizes[2]=sizeof(ra::device_settings); sizes[3]=sizeof(ra::profile);
    sizes[4]=sizeof(ra::accel_args); sizes[5]=sizeof(ra::device_config);
}
API uint32_t zero_ra_size(const uint8_t* header, uint32_t bytes) {
    if (!header || bytes < sizeof(ra::io_base) || !valid_device(header)) return 0;
    ra::io_base base; std::memcpy(&base,header,sizeof(base)); return frame_size(base);
}
API uint32_t zero_ra_default(uint8_t* output, uint32_t cap, uint32_t* written) {
    frame f; return encode(f,output,cap,written);
}
API uint32_t zero_ra_validate(const uint8_t* input, uint32_t size) {
    try { frame f; return decode(input,size,f) ? ok : invalid; } catch (...) { return invalid; }
}
API double zero_ra_response(const curve_config* c, double speed) {
    if (!c || !std::isfinite(speed) || speed < 0) return std::numeric_limits<double>::quiet_NaN();
    ra::modifier_settings s;
    curve_config horizontal=*c; horizontal.rotation=0;
    if (!make_modifier(horizontal,L"0Accel preview",s)) return std::numeric_limits<double>::quiet_NaN();
    if (speed == 0) speed=1e-9;
    ra::modifier mod(s); ra::speed_processor processor; processor.init(s.prof.speed_processor_args);
    vec2d v{speed,0}; mod.modify(v,processor,s,1,1);
    // Plot horizontal sensitivity BEFORE optional axis rotation/Y scaling, like Raw Accel's default chart.
    return std::hypot(v.x,v.y) / speed;
}
API uint32_t zero_ra_prepare(const uint8_t* input, uint32_t size, const curve_config* c,
    const wchar_t* id, const wchar_t* profile_name,
    uint8_t* output, uint32_t cap, uint32_t* written) {
    if (written) *written=0;
    try {
        frame f;
        if (!decode(input,size,f) || !id || !profile_name || !c) return invalid;
        ra::device_settings target{};
        if (!assign(target.id,id) || !assign(target.name,L"0Accel")) return invalid;
        size_t device=f.devices.size();
        for (size_t i=0;i<f.devices.size();++i) if (same(f.devices[i].id,id)) device=i;
        if (device<f.devices.size()) target=f.devices[device];
        else target.config=f.base.default_dev_cfg;
        {
            ra::modifier_settings desired;
            if (!make_modifier(*c,profile_name,desired)) return invalid;
            // Keep the default profile and every other device/profile byte-for-byte.
            // An empty driver needs an identity default before adding our target.
            if (f.profiles.empty()) {
                ra::modifier_settings identity{}; ra::init_data(identity); f.profiles.push_back(identity);
                f.base.default_dev_cfg.disable=true;
            }
            size_t index=f.profiles.size();
            for (size_t i=0;i<f.profiles.size();++i) if (same(f.profiles[i].prof.name,profile_name)) index=i;
            if (index<f.profiles.size()) {
                if (index==0 || device==f.devices.size() || !same(target.profile,profile_name)) return collision;
                for (size_t i=0;i<f.devices.size();++i)
                    if (i!=device && same(f.devices[i].profile,profile_name)) return collision;
                f.profiles[index]=desired;
            } else f.profiles.push_back(desired);
            assign(target.profile,profile_name);
            // Our UI uses counts/ms: its DPI field is informational, not normalization.
            target.config={};
        }
        if (device==f.devices.size()) f.devices.push_back(target); else f.devices[device]=target;
        return encode(f,output,cap,written);
    } catch (...) { return invalid; }
}
API uint32_t zero_ra_inspect(const uint8_t* input, uint32_t size, const wchar_t* id,
    curve_config* config, uint32_t* enabled) {
    if (!config || !enabled || !id) return invalid;
    *config={1,1,.02,15,120,1.2,2,.1,1.5,0,1,0,0}; *enabled=0;
    try {
        frame f; if (!decode(input,size,f)) return invalid;
        if (f.profiles.empty()) return ok;
        ra::device_config dev=f.base.default_dev_cfg;
        const ra::profile* profile=&f.profiles.front().prof;
        for (const auto& d : f.devices) if (same(d.id,id)) {
            dev=d.config;
            for (const auto& s : f.profiles) if (same(s.prof.name,d.profile)) profile=&s.prof;
            break;
        }
        *enabled=dev.disable ? 0 : 1;
        const auto& p=*profile;
        const auto& a=p.accel_x;
        // Never silently flatten settings that this minimal UI cannot express.
        if (dev.dpi!=0 || dev.polling_rate!=0 || dev.poll_time_lock || dev.set_extra_info
            || dev.clamp.min!=ra::DEFAULT_TIME_MIN || dev.clamp.max!=ra::DEFAULT_TIME_MAX
            || !p.speed_processor_args.whole || p.speed_processor_args.lp_norm!=2
            || p.speed_processor_args.input_speed_smooth_halflife!=0 || p.speed_processor_args.scale_smooth_halflife!=0
            || p.speed_processor_args.output_speed_smooth_halflife!=0 || p.domain_weights.x!=1 || p.domain_weights.y!=1
            || p.range_weights.x!=1 || p.range_weights.y!=1 || p.lr_output_dpi_ratio!=1 || p.ud_output_dpi_ratio!=1
            || p.degrees_snap!=0 || p.speed_min!=0 || p.speed_max!=0
            || (p.degrees_rotation!=0 && p.degrees_rotation!=90 && p.degrees_rotation!=180 && p.degrees_rotation!=270)
            || (a.mode!=ra::accel_mode::noaccel && a.mode!=ra::accel_mode::classic && a.mode!=ra::accel_mode::natural)) return unsupported;
        *config={p.output_dpi/1000,p.yx_output_dpi_ratio,a.acceleration,a.input_offset,a.cap.x,a.cap.y,
            a.exponent_classic,a.decay_rate,a.limit,
            a.mode==ra::accel_mode::noaccel ? 0u : a.mode==ra::accel_mode::natural ? 3u : a.exponent_classic==2 ? 1u : 2u,
            a.gain ? 1u:0u,a.cap_mode==ra::cap_mode::out ? 0u : a.cap_mode==ra::cap_mode::in ? 1u:2u,
            static_cast<uint32_t>(p.degrees_rotation)};
        if (p.degrees_rotation != config->rotation || !valid_config(*config)) return unsupported;
        return ok;
    } catch (...) { return invalid; }
}
