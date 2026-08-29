#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <setupapi.h>
#include <wintrust.h>
#include <softpub.h>
#include <shellapi.h>
#include <shlobj.h>

#include <algorithm>
#include <array>
#include <cwchar>
#include <cstring>
#include <exception>
#include <fstream>
#include <stdexcept>
#include <string>
#include <vector>

// Installation behavior follows the MIT-licensed Raw Accel v1.7.1 installer.
// The signed upstream driver is verified and copied without modification.

namespace {
constexpr wchar_t ServiceName[] = L"rawaccel";
constexpr wchar_t ServiceImage[] = L"\\SystemRoot\\System32\\drivers\\rawaccel.sys";
constexpr wchar_t ExpectedHash[] = L"8a62c4deef2774b43a7363b352eda79897533a1080c9c26ffeff0559e43358d7";
constexpr GUID MouseClass = {0x4d36e96f,0xe325,0x11ce,{0xbf,0xc1,0x08,0x00,0x2b,0xe1,0x03,0x18}};
constexpr DWORD UpperFiltersProperty = 0x00000011;

struct win_error : std::runtime_error {
    DWORD code;
    explicit win_error(const char* message, DWORD value=GetLastError()) : std::runtime_error(message), code(value) {}
};

std::wstring program_data_path(const wchar_t* name) {
    DWORD needed=GetEnvironmentVariableW(L"ProgramData",nullptr,0);
    if(!needed) throw win_error("ProgramData is unavailable");
    std::wstring root(needed,L'\0');
    if(!GetEnvironmentVariableW(L"ProgramData",root.data(),needed)) throw win_error("ProgramData is unavailable");
    root.resize(wcslen(root.c_str()));
    return root+L"\\0Accel\\"+name;
}

void ensure_log_directory() {
    DWORD needed=GetEnvironmentVariableW(L"ProgramData",nullptr,0);
    if(!needed) return;
    std::wstring root(needed,L'\0');
    if(!GetEnvironmentVariableW(L"ProgramData",root.data(),needed)) return;
    root.resize(wcslen(root.c_str()));
    CreateDirectoryW((root+L"\\0Accel").c_str(),nullptr);
}

void log_line(const std::wstring& text) {
    ensure_log_directory();
    std::wstring path=program_data_path(L"setup.log");
    std::wofstream file(path.c_str(),std::ios::app);
    SYSTEMTIME time{}; GetSystemTime(&time);
    if(file) file << time.wYear << L'-' << time.wMonth << L'-' << time.wDay << L' '
        << time.wHour << L':' << time.wMinute << L':' << time.wSecond << L" UTC  " << text << L'\n';
}

std::wstring driver_path() {
    wchar_t windows[MAX_PATH];
    UINT length=GetWindowsDirectoryW(windows,MAX_PATH);
    if(!length || length>=MAX_PATH) throw win_error("GetWindowsDirectory failed");
    return std::wstring(windows,length)+L"\\System32\\drivers\\rawaccel.sys";
}

std::array<unsigned char,32> hash_file(const std::wstring& path) {
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,
        nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE) throw win_error("Open file for SHA-256 failed");
    BCRYPT_ALG_HANDLE algorithm=nullptr; BCRYPT_HASH_HANDLE hash=nullptr;
    std::vector<unsigned char> object;
    std::array<unsigned char,32> result{};
    try {
        if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)
            throw std::runtime_error("BCryptOpenAlgorithmProvider failed");
        DWORD object_size=0,received=0,hash_size=0;
        if(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&object_size),sizeof(object_size),&received,0)<0
            || BCryptGetProperty(algorithm,BCRYPT_HASH_LENGTH,reinterpret_cast<PUCHAR>(&hash_size),sizeof(hash_size),&received,0)<0
            || hash_size!=result.size()) throw std::runtime_error("BCryptGetProperty failed");
        object.resize(object_size);
        if(BCryptCreateHash(algorithm,&hash,object.data(),object_size,nullptr,0,0)<0)
            throw std::runtime_error("BCryptCreateHash failed");
        std::array<unsigned char,65536> buffer{}; DWORD read=0;
        for(;;) {
            if(!ReadFile(file,buffer.data(),static_cast<DWORD>(buffer.size()),&read,nullptr))
                throw win_error("Read file for SHA-256 failed");
            if(!read) break;
            if(BCryptHashData(hash,buffer.data(),read,0)<0) throw std::runtime_error("BCryptHashData failed");
        }
        if(BCryptFinishHash(hash,result.data(),static_cast<ULONG>(result.size()),0)<0)
            throw std::runtime_error("BCryptFinishHash failed");
    } catch(...) {
        if(hash) BCryptDestroyHash(hash); if(algorithm) BCryptCloseAlgorithmProvider(algorithm,0); CloseHandle(file); throw;
    }
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm,0); CloseHandle(file); return result;
}

std::wstring hex(const std::array<unsigned char,32>& bytes) {
    constexpr wchar_t digits[]=L"0123456789abcdef"; std::wstring value; value.reserve(64);
    for(unsigned char byte:bytes) { value.push_back(digits[byte>>4]); value.push_back(digits[byte&15]); }
    return value;
}

bool expected_hash(const std::wstring& path) {
    try { return hex(hash_file(path))==ExpectedHash; } catch(...) { return false; }
}

bool valid_signature(const std::wstring& path) {
    WINTRUST_FILE_INFO file{}; file.cbStruct=sizeof(file); file.pcwszFilePath=path.c_str();
    WINTRUST_DATA data{}; data.cbStruct=sizeof(data); data.dwUIChoice=WTD_UI_NONE;
    data.fdwRevocationChecks=WTD_REVOKE_NONE; data.dwUnionChoice=WTD_CHOICE_FILE; data.pFile=&file;
    data.dwProvFlags=WTD_CACHE_ONLY_URL_RETRIEVAL|WTD_REVOCATION_CHECK_NONE;
    GUID action=WINTRUST_ACTION_GENERIC_VERIFY_V2;
    return WinVerifyTrust(nullptr,&action,&data)==ERROR_SUCCESS;
}

void verify_source(const std::wstring& path) {
    if(!expected_hash(path)) throw std::runtime_error("Raw Accel driver SHA-256 mismatch");
    if(!valid_signature(path)) throw std::runtime_error("Raw Accel driver signature is invalid");
}

std::vector<std::wstring> read_filters() {
    DWORD type=0,size=0;
    if(!SetupDiGetClassRegistryPropertyW(&MouseClass,UpperFiltersProperty,&type,nullptr,0,&size,nullptr,nullptr)) {
        DWORD error=GetLastError();
        if(error==ERROR_INVALID_DATA || error==ERROR_FILE_NOT_FOUND) return {};
        if(error!=ERROR_INSUFFICIENT_BUFFER) throw win_error("Read mouse UpperFilters size failed",error);
    }
    if(type!=REG_MULTI_SZ || size<sizeof(wchar_t)) throw std::runtime_error("Mouse UpperFilters has invalid type");
    std::vector<unsigned char> bytes(size+sizeof(wchar_t),0);
    if(!SetupDiGetClassRegistryPropertyW(&MouseClass,UpperFiltersProperty,&type,bytes.data(),size,&size,nullptr,nullptr))
        throw win_error("Read mouse UpperFilters failed");
    std::vector<std::wstring> filters;
    const wchar_t* current=reinterpret_cast<const wchar_t*>(bytes.data());
    const wchar_t* end=reinterpret_cast<const wchar_t*>(bytes.data()+size);
    while(current<end && *current) {
        size_t length=wcsnlen(current,static_cast<size_t>(end-current));
        if(current+length>=end) throw std::runtime_error("Mouse UpperFilters is malformed");
        filters.emplace_back(current,length); current+=length+1;
    }
    return filters;
}

std::vector<unsigned char> encode_filters(const std::vector<std::wstring>& filters) {
    size_t chars=1; for(const auto& item:filters) chars+=item.size()+1;
    std::vector<unsigned char> bytes(chars*sizeof(wchar_t),0); wchar_t* output=reinterpret_cast<wchar_t*>(bytes.data());
    for(const auto& item:filters) { memcpy(output,item.c_str(),item.size()*sizeof(wchar_t)); output+=item.size()+1; }
    return bytes;
}

bool has_filter(const std::vector<std::wstring>& filters) {
    return std::any_of(filters.begin(),filters.end(),[](const std::wstring& item){return _wcsicmp(item.c_str(),ServiceName)==0;});
}

void backup_filters_once(const std::vector<std::wstring>& filters) {
    ensure_log_directory(); std::wstring path=program_data_path(L"mouse-upperfilters-before-install.bin");
    HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE) { if(GetLastError()==ERROR_FILE_EXISTS) return; throw win_error("Create UpperFilters backup failed"); }
    auto bytes=encode_filters(filters); DWORD written=0;
    BOOL ok=WriteFile(file,bytes.data(),static_cast<DWORD>(bytes.size()),&written,nullptr); FlushFileBuffers(file); CloseHandle(file);
    if(!ok || written!=bytes.size()) throw win_error("Write UpperFilters backup failed");
}

bool write_filter(bool install) {
    auto before=read_filters(); auto after=before;
    after.erase(std::remove_if(after.begin(),after.end(),[](const std::wstring& item){return _wcsicmp(item.c_str(),ServiceName)==0;}),after.end());
    if(install) after.insert(after.begin(),ServiceName);
    if(before==after) return false;
    if(install) backup_filters_once(before);
    auto bytes=encode_filters(after);
    if(!SetupDiSetClassRegistryPropertyW(&MouseClass,UpperFiltersProperty,bytes.data(),static_cast<DWORD>(bytes.size()),nullptr,nullptr))
        throw win_error("Write mouse UpperFilters failed");
    try {
        if(read_filters()!=after) throw std::runtime_error("Mouse UpperFilters readback failed");
    } catch(...) {
        auto original=std::current_exception();
        auto rollback=encode_filters(before);
        if(!SetupDiSetClassRegistryPropertyW(&MouseClass,UpperFiltersProperty,rollback.data(),static_cast<DWORD>(rollback.size()),nullptr,nullptr))
            throw win_error("Mouse UpperFilters rollback failed");
        std::rethrow_exception(original);
    }
    return true;
}

bool service_ready() {
    SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT);
    if(!manager) return false;
    SC_HANDLE service=OpenServiceW(manager,ServiceName,SERVICE_QUERY_CONFIG);
    if(!service) {CloseServiceHandle(manager);return false;}
    DWORD needed=0; QueryServiceConfigW(service,nullptr,0,&needed);
    std::vector<unsigned char> bytes(needed);
    bool ready=false;
    if(needed && QueryServiceConfigW(service,reinterpret_cast<QUERY_SERVICE_CONFIGW*>(bytes.data()),needed,&needed)) {
        auto* config=reinterpret_cast<QUERY_SERVICE_CONFIGW*>(bytes.data());
        ready=config->dwServiceType==SERVICE_KERNEL_DRIVER && config->dwStartType==SERVICE_DEMAND_START
            && config->lpBinaryPathName && _wcsicmp(config->lpBinaryPathName,ServiceImage)==0;
    }
    CloseServiceHandle(service); CloseServiceHandle(manager); return ready;
}

bool configure_service() {
    if(service_ready()) return false;
    SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT|SC_MANAGER_CREATE_SERVICE);
    if(!manager) throw win_error("OpenSCManager failed");
    SC_HANDLE service=OpenServiceW(manager,ServiceName,SERVICE_CHANGE_CONFIG|SERVICE_QUERY_CONFIG);
    if(service) {
        if(!ChangeServiceConfigW(service,SERVICE_KERNEL_DRIVER,SERVICE_DEMAND_START,SERVICE_ERROR_NORMAL,
            ServiceImage,nullptr,nullptr,nullptr,nullptr,nullptr,ServiceName)) {
            DWORD error=GetLastError(); CloseServiceHandle(service); CloseServiceHandle(manager); throw win_error("ChangeServiceConfig failed",error);
        }
    } else if(GetLastError()==ERROR_SERVICE_DOES_NOT_EXIST) {
        service=CreateServiceW(manager,ServiceName,ServiceName,SERVICE_QUERY_CONFIG|SERVICE_CHANGE_CONFIG,
            SERVICE_KERNEL_DRIVER,SERVICE_DEMAND_START,SERVICE_ERROR_NORMAL,ServiceImage,nullptr,nullptr,nullptr,nullptr,nullptr);
        if(!service) {DWORD error=GetLastError();CloseServiceHandle(manager);throw win_error("CreateService failed",error);}
    } else {DWORD error=GetLastError();CloseServiceHandle(manager);throw win_error("OpenService failed",error);}
    CloseServiceHandle(service); CloseServiceHandle(manager);
    if(!service_ready()) throw std::runtime_error("Raw Accel service readback failed");
    return true;
}

bool copy_driver(const std::wstring& source) {
    std::wstring target=driver_path();
    if(expected_hash(target)) return false;
    bool had_existing=GetFileAttributesW(target.c_str())!=INVALID_FILE_ATTRIBUTES;
    std::wstring old=target+L".0accel-backup-"+std::to_wstring(GetCurrentProcessId());
    if(had_existing) {
        if(GetFileAttributesW(old.c_str())!=INVALID_FILE_ATTRIBUTES && !DeleteFileW(old.c_str()))
            throw win_error("Remove stale Raw Accel backup failed");
        if(!MoveFileExW(target.c_str(),old.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))
            throw win_error("Move existing Raw Accel driver failed");
    }
    auto rollback=[&]() {
        DeleteFileW(target.c_str());
        return !had_existing || MoveFileExW(old.c_str(),target.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH);
    };
    if(!CopyFileW(source.c_str(),target.c_str(),FALSE)) {
        DWORD error=GetLastError();
        if(!rollback()) throw win_error("Raw Accel driver copy rollback failed");
        throw win_error("Copy Raw Accel driver failed",error);
    }
    if(!expected_hash(target) || !valid_signature(target)) {
        if(!rollback()) throw win_error("Raw Accel driver verification rollback failed");
        throw std::runtime_error("Installed Raw Accel driver verification failed");
    }
    if(had_existing && !MoveFileExW(old.c_str(),nullptr,MOVEFILE_DELAY_UNTIL_REBOOT)) {
        DWORD error=GetLastError();
        if(!rollback()) throw win_error("Raw Accel backup cleanup rollback failed");
        throw win_error("Schedule old driver deletion failed",error);
    }
    return true;
}

bool delete_service() {
    SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT);
    if(!manager) throw win_error("OpenSCManager failed");
    SC_HANDLE service=OpenServiceW(manager,ServiceName,DELETE);
    if(!service) {
        DWORD error=GetLastError(); CloseServiceHandle(manager);
        if(error==ERROR_SERVICE_DOES_NOT_EXIST) return false;
        throw win_error("OpenService for deletion failed",error);
    }
    BOOL deleted=DeleteService(service); DWORD error=GetLastError();
    CloseServiceHandle(service); CloseServiceHandle(manager);
    if(!deleted && error!=ERROR_SERVICE_MARKED_FOR_DELETE) throw win_error("DeleteService failed",error);
    return true;
}

bool delete_driver() {
    std::wstring target=driver_path();
    if(GetFileAttributesW(target.c_str())==INVALID_FILE_ATTRIBUTES) return false;
    std::wstring old=target+L".0accel-remove-"+std::to_wstring(GetCurrentProcessId());
    if(GetFileAttributesW(old.c_str())!=INVALID_FILE_ATTRIBUTES && !DeleteFileW(old.c_str()))
        throw win_error("Remove stale Raw Accel deletion file failed");
    if(!MoveFileExW(target.c_str(),old.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))
        throw win_error("Move Raw Accel driver for deletion failed");
    if(!MoveFileExW(old.c_str(),nullptr,MOVEFILE_DELAY_UNTIL_REBOOT)) {
        DWORD error=GetLastError();
        if(!MoveFileExW(old.c_str(),target.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))
            throw win_error("Raw Accel deletion rollback failed");
        throw win_error("Schedule driver deletion failed",error);
    }
    return true;
}

bool ready() {
    return expected_hash(driver_path()) && valid_signature(driver_path()) && service_ready() && has_filter(read_filters());
}

int relaunch_elevated(int argc,wchar_t** argv) {
    wchar_t executable[32768]; DWORD length=GetModuleFileNameW(nullptr,executable,32768);
    if(!length || length>=32768) throw win_error("GetModuleFileName failed");
    std::wstring parameters;
    for(int i=1;i<argc;i++) {
        if(wcschr(argv[i],L'\"')) throw std::runtime_error("Invalid quote in command argument");
        if(!parameters.empty()) parameters.push_back(L' ');
        parameters.push_back(L'\"'); parameters.append(argv[i]); parameters.push_back(L'\"');
    }
    SHELLEXECUTEINFOW info{}; info.cbSize=sizeof(info); info.fMask=SEE_MASK_NOCLOSEPROCESS;
    info.lpVerb=L"runas"; info.lpFile=executable; info.lpParameters=parameters.c_str(); info.nShow=SW_HIDE;
    if(!ShellExecuteExW(&info)) throw win_error("Elevation failed");
    WaitForSingleObject(info.hProcess,INFINITE); DWORD code=1;
    if(!GetExitCodeProcess(info.hProcess,&code)) {DWORD error=GetLastError();CloseHandle(info.hProcess);throw win_error("Read elevated exit code failed",error);}
    CloseHandle(info.hProcess); return static_cast<int>(code);
}
}

int wmain(int argc,wchar_t** argv) {
    try {
        if(argc<2) return 64;
        std::wstring command=argv[1];
        if(command==L"verify") {
            if(argc!=3) return 64; verify_source(argv[2]); log_line(L"Verified pinned Raw Accel payload."); return 0;
        }
        if(command==L"status") return ready()?0:2;
        if(!IsUserAnAdmin()) return relaunch_elevated(argc,argv);
        if(command==L"install") {
            if(argc!=3) return 64; verify_source(argv[2]);
            bool changed=copy_driver(argv[2]); changed=configure_service()||changed; changed=write_filter(true)||changed;
            if(!ready()) throw std::runtime_error("Raw Accel installation readback failed");
            log_line(changed?L"Installed pinned Raw Accel driver; restart required.":L"Pinned Raw Accel driver already ready.");
            return changed?3010:0;
        }
        if(command==L"uninstall") {
            bool changed=write_filter(false); changed=delete_service()||changed; changed=delete_driver()||changed;
            log_line(changed?L"Removed Raw Accel filter; restart required.":L"Raw Accel driver was already absent.");
            return changed?3010:0;
        }
        return 64;
    } catch(const win_error& error) {
        log_line(L"Driver helper Windows error "+std::to_wstring(error.code)+L": "+std::wstring(error.what(),error.what()+strlen(error.what())));
        return static_cast<int>(error.code?error.code:1);
    } catch(const std::exception& error) {
        log_line(L"Driver helper error: "+std::wstring(error.what(),error.what()+strlen(error.what())));
        return 1;
    }
}
