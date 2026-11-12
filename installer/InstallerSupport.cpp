// Installer-only baseline-x64 checks. Do not compile this helper with /arch:AVX2.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <intrin.h>
#include <bcrypt.h>
#include <string>
#include <vector>
#include <algorithm>
#include <future>
#include <chrono>
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")

namespace {
HWND maintenanceWindow{};
void PumpMessages() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
        if (message.message == WM_QUIT) {
            PostQuitMessage(static_cast<int>(message.wParam));
            break;
        }
        if (!IsDialogMessageW(maintenanceWindow, &message)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}
int WaitForMaintenanceProcess(HANDLE process) {
    DWORD status;
    do {
        status = WaitForSingleObject(process, 30);
        PumpMessages();
    } while (status == WAIT_TIMEOUT);
    if (status != WAIT_OBJECT_0) WaitForSingleObject(process, INFINITE);
    DWORD exitCode{};
    return status == WAIT_OBJECT_0 && GetExitCodeProcess(process, &exitCode) ? static_cast<int>(exitCode) : -1;
}
struct Guard { std::wstring path; HANDLE handle; BY_HANDLE_FILE_INFORMATION info; };
std::wstring ownedRoot;
std::vector<Guard> directories;
std::vector<HANDLE> files;
void Reset() noexcept {
    for (auto handle : files) CloseHandle(handle);
    files.clear();
    for (auto it = directories.rbegin(); it != directories.rend(); ++it) CloseHandle(it->handle);
    directories.clear(); ownedRoot.clear();
}
bool Full(const wchar_t* path, std::wstring& result) {
    if (!path) return false;
    std::vector<wchar_t> buffer(32768);
    DWORD size = GetFullPathNameW(path, static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
    if (!size || size >= buffer.size()) return false;
    result.assign(buffer.data());
    while (result.size() > 3 && result.back() == L'\\') result.pop_back();
    return result.size() > 3 && result[1] == L':' && result[2] == L'\\';
}
std::wstring Identity(const BY_HANDLE_FILE_INFORMATION& info) {
    wchar_t value[41]{};
    swprintf_s(value, L"%08x%08x%08x%08x%08x", info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow,
        info.ftCreationTime.dwHighDateTime, info.ftCreationTime.dwLowDateTime);
    return value;
}
bool Regular(HANDLE handle, const std::wstring& path) {
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(handle, &info) || info.nNumberOfLinks != 1 ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) return false;
    std::vector<wchar_t> buffer(32768);
    DWORD size = GetFinalPathNameByHandleW(handle, buffer.data(), static_cast<DWORD>(buffer.size()), 0);
    if (!size || size >= buffer.size()) return false;
    std::wstring actual(buffer.data());
    if (actual.starts_with(L"\\\\?\\")) actual.erase(0, 4);
    return _wcsicmp(actual.c_str(), path.c_str()) == 0;
}
bool HashCore(HANDLE handle, const wchar_t* expected) {
    if (!expected || wcslen(expected) != 64) return false;
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return false;
    bool result = false;
    if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0) {
        std::vector<unsigned char> buffer(65536); DWORD read{}; LARGE_INTEGER zero{};
        bool valid = SetFilePointerEx(handle, zero, nullptr, FILE_BEGIN) != FALSE;
        while (valid) {
            if (!ReadFile(handle, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) { valid = false; break; }
            if (!read) break;
            if (BCryptHashData(hash, buffer.data(), read, 0) < 0) valid = false;
        }
        unsigned char digest[32]{}; wchar_t text[65]{};
        if (valid && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0) {
            for (size_t index = 0; index < 32; ++index) swprintf_s(text + index * 2, 3, L"%02x", digest[index]);
            result = _wcsicmp(text, expected) == 0;
        }
        BCryptDestroyHash(hash);
    }
    BCryptCloseAlgorithmProvider(algorithm, 0); return result;
}
bool Hash(HANDLE handle, const wchar_t* expected) {
    if (!IsWindow(maintenanceWindow)) return HashCore(handle, expected);
    auto worker = std::async(std::launch::async, [=] { return HashCore(handle, expected); });
    while (worker.wait_for(std::chrono::milliseconds(20)) != std::future_status::ready) PumpMessages();
    bool result = worker.get();
    PumpMessages();
    return result;
}
bool Pin(const std::wstring& path) {
    if (std::any_of(directories.begin(), directories.end(), [&](const Guard& guard) { return _wcsicmp(guard.path.c_str(), path.c_str()) == 0; })) return true;
    if (path.size() > 3) {
        size_t slash = path.find_last_of(L'\\');
        std::wstring parent = slash == 2 ? path.substr(0, 3) : path.substr(0, slash);
        if (!Pin(parent)) return false;
    }
    HANDLE handle = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    BY_HANDLE_FILE_INFORMATION info{};
    if (handle == INVALID_HANDLE_VALUE) return false;
    if (!GetFileInformationByHandle(handle, &info) || !(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
        CloseHandle(handle); return false;
    }
    directories.push_back({path, handle, info}); return true;
}
bool Relative(const wchar_t* relative, std::wstring& path) {
    if (!relative || !*relative || ownedRoot.empty()) return false;
    std::wstring value(relative);
    std::replace(value.begin(), value.end(), L'/', L'\\');
    if (value.front() == L'\\' || value.find(L':') != std::wstring::npos) return false;
    size_t start = 0;
    while (start < value.size()) {
        size_t end = value.find(L'\\', start); if (end == std::wstring::npos) end = value.size();
        std::wstring part = value.substr(start, end - start);
        if (part.empty() || part == L"." || part == L".." || part.back() == L'.' || part.back() == L' ' || part.find_first_of(L"*?\"<>|\r\n") != std::wstring::npos) return false;
        start = end + 1;
    }
    path = ownedRoot + L"\\" + value; return true;
}
}

extern "C" __declspec(dllexport) void __stdcall SetMaintenanceWindow(HWND window) noexcept {
    maintenanceWindow = window;
}
extern "C" __declspec(dllexport) BOOL __stdcall VerifyMaintenanceFile(const wchar_t* path, const wchar_t* expected) noexcept {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return FALSE;
    bool result = false;
    try { result = Regular(file, path) && Hash(file, expected); } catch (...) { }
    CloseHandle(file);
    return result;
}
extern "C" __declspec(dllexport) int __stdcall RunMaintenanceProcess(const wchar_t* executable,
    const wchar_t* parameters, const wchar_t* directory) noexcept {
    PROCESS_INFORMATION process{};
    try {
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_HIDE;
        std::wstring command = L"\"" + std::wstring(executable) + L"\" " + parameters;
        if (!CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
            nullptr, directory, &startup, &process)) return -1;
        CloseHandle(process.hThread);
        process.hThread = nullptr;
        int exitCode = WaitForMaintenanceProcess(process.hProcess);
        CloseHandle(process.hProcess);
        return exitCode;
    } catch (...) {
        // Input files and pins outlive the child, including exceptional teardown.
        if (process.hProcess) { WaitForSingleObject(process.hProcess, INFINITE); CloseHandle(process.hProcess); }
        return -1;
    }
}

extern "C" __declspec(dllexport) int __stdcall InstallMediaFeaturePack(HWND window) noexcept {
    SHELLEXECUTEINFOW launch{};
    HWND previousWindow = maintenanceWindow;
    try {
        wchar_t systemDirectory[MAX_PATH]{};
        UINT length = GetSystemDirectoryW(systemDirectory, MAX_PATH);
        if (!length || length >= MAX_PATH) return -1;
        std::wstring executable = std::wstring(systemDirectory) + L"\\dism.exe";
        launch.cbSize = sizeof(launch);
        launch.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC;
        launch.hwnd = window;
        launch.lpVerb = L"runas";
        launch.lpFile = executable.c_str();
        launch.lpParameters = L"/Online /Add-Capability /CapabilityName:Media.MediaFeaturePack~~~~0.0.1.0 /NoRestart";
        launch.nShow = SW_HIDE;
        if (!ShellExecuteExW(&launch) || !launch.hProcess) return -1;
        maintenanceWindow = window;
        int code = WaitForMaintenanceProcess(launch.hProcess);
        CloseHandle(launch.hProcess);
        maintenanceWindow = previousWindow;
        return code;
    } catch (...) {
        if (launch.hProcess) { WaitForSingleObject(launch.hProcess, INFINITE); CloseHandle(launch.hProcess); }
        maintenanceWindow = previousWindow;
        return -1;
    }
}

extern "C" __declspec(dllexport) BOOL __stdcall BeginOwnedDelete(const wchar_t* root) noexcept {
    try { Reset(); return Full(root, ownedRoot) && Pin(ownedRoot); } catch (...) { Reset(); return FALSE; }
}
extern "C" __declspec(dllexport) void __stdcall CancelOwnedDelete() noexcept { Reset(); }
extern "C" __declspec(dllexport) BOOL __stdcall QueueOwnedFile(const wchar_t* relative, const wchar_t* sha256) noexcept {
    try {
        if (files.size() >= 8192) return FALSE;
        std::wstring path; if (!Relative(relative, path)) return FALSE;
        HANDLE handle = CreateFileW(path.c_str(), GENERIC_READ | DELETE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (handle == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND;
        if (!Pin(path.substr(0, path.find_last_of(L'\\'))) || !Regular(handle, path) || !Hash(handle, sha256)) { CloseHandle(handle); return FALSE; }
        files.push_back(handle); return TRUE;
    } catch (...) { return FALSE; }
}
extern "C" __declspec(dllexport) BOOL __stdcall CommitOwnedDelete() noexcept {
    bool result = true;
    for (auto handle : files) { FILE_DISPOSITION_INFO disposition{TRUE}; if (!SetFileInformationByHandle(handle, FileDispositionInfo, &disposition, sizeof(disposition))) { result = false; break; } }
    for (auto handle : files) CloseHandle(handle); files.clear();
    // Prune only independently pinned empty directories. Unknown content keeps its parents.
    for (auto it = directories.rbegin(); it != directories.rend(); ++it) {
        CloseHandle(it->handle); it->handle = INVALID_HANDLE_VALUE;
        if (ownedRoot.empty() || it->path.size() < ownedRoot.size()) continue;
        HANDLE handle = CreateFileW(it->path.c_str(), DELETE | FILE_READ_ATTRIBUTES, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (handle == INVALID_HANDLE_VALUE) continue;
        BY_HANDLE_FILE_INFORMATION info{};
        if (GetFileInformationByHandle(handle, &info) && Identity(info) == Identity(it->info) && !(info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
            FILE_DISPOSITION_INFO disposition{TRUE}; SetFileInformationByHandle(handle, FileDispositionInfo, &disposition, sizeof(disposition));
        }
        CloseHandle(handle);
    }
    directories.clear(); ownedRoot.clear(); return result;
}
extern "C" __declspec(dllexport) BOOL __stdcall CheckDirectoryIdentity(const wchar_t* path, const wchar_t* expected) noexcept {
    try {
        std::wstring full; if (!Full(path, full) || !expected) return FALSE;
        HANDLE handle = CreateFileW(full.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (handle == INVALID_HANDLE_VALUE) return FALSE;
        BY_HANDLE_FILE_INFORMATION info{}; bool valid = GetFileInformationByHandle(handle, &info) && (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
            !(info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) && Identity(info) == expected;
        CloseHandle(handle); return valid;
    } catch (...) { return FALSE; }
}

extern "C" __declspec(dllexport) BOOL __stdcall CompatibleCpu()
{
    int registers[4]{};
    __cpuid(registers, 0);
    if (registers[0] < 7) return FALSE;
    __cpuidex(registers, 1, 0);
    constexpr int required = (1 << 12) | (1 << 27) | (1 << 28); // FMA, OSXSAVE, AVX
    if ((registers[2] & required) != required || (_xgetbv(0) & 6) != 6) return FALSE;
    __cpuidex(registers, 7, 0);
    return (registers[1] & (1 << 5)) != 0; // AVX2
}

extern "C" __declspec(dllexport) BOOL __stdcall MediaFoundationAvailable()
{
    // Restrict lookup to Windows, never the working directory or a downloaded DLL.
    const wchar_t* names[] = {L"mfplat.dll", L"mfreadwrite.dll", L"mf.dll"};
    for (const auto name : names)
    {
        const auto module = LoadLibraryExW(name, nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) return FALSE;
        FreeLibrary(module);
    }
    return TRUE;
}
