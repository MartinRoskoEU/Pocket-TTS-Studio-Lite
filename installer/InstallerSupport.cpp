// Installer-only baseline-x64 checks. Do not compile this helper with /arch:AVX2.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <intrin.h>

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
