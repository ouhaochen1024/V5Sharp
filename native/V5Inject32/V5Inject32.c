/*
 * V5Inject32.c - 32 位目标专用注入辅助进程（x86）
 *
 * 为什么需要它：
 *   WOW64 进程在"刚创建、还没跑起来"时，32 位 PEB 的 Ldr 是 0，既没有
 *   32 位模块链表，也没有可用的 32 位代码地址；而 64 位调试器只能看到
 *   64 位侧的断点，拿不到 32 位的入口断点。所以 32 位目标交给这个 32 位
 *   帮手：由它创建被调试的目标、在 32 位初始断点处停下（此时加载器已就绪、
 *   程序入口还没执行）、注入 V5Hook32.dll，然后脱离调试并恢复运行。
 *
 * 用法（由 V5Sharp 宿主调用，也可手工使用）：
 *   V5Inject32.exe --exe <目标路径> [--args "参数"] [--group N] [--log <日志路径>] --dll <payload 路径>
 * 成功时向 stdout 输出 "PID=<进程号>" 并返回 0。
 *
 * 不依赖 CRT：只用 kernel32 / shell32 / user32。
 */
#include <windows.h>
#include <shellapi.h>
#include <psapi.h>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "user32.lib")

static int    g_argc;
static LPWSTR* g_argv;

static LPWSTR Arg(LPCWSTR name)
{
    int i;
    for (i = 1; i + 1 < g_argc; i++)
        if (!lstrcmpiW(g_argv[i], name)) return g_argv[i + 1];
    return NULL;
}

/* 从 exe 文件里读入口点 RVA */
static DWORD EntryPointRva(LPCWSTR exePath)
{
    HANDLE h = CreateFileW(exePath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    HANDLE map;
    BYTE*  base;
    DWORD  rva = 0;

    if (h == INVALID_HANDLE_VALUE) return 0;
    map = CreateFileMappingW(h, NULL, PAGE_READONLY, 0, 0, NULL);
    base = map ? (BYTE*)MapViewOfFile(map, FILE_MAP_READ, 0, 0, 0) : NULL;
    if (base)
    {
        IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)base;
        if (dos->e_magic == IMAGE_DOS_SIGNATURE)
        {
            IMAGE_NT_HEADERS32* nt = (IMAGE_NT_HEADERS32*)(base + dos->e_lfanew);
            if (nt->Signature == IMAGE_NT_SIGNATURE)
                rva = nt->OptionalHeader.AddressOfEntryPoint;
        }
        UnmapViewOfFile(base);
    }
    if (map) CloseHandle(map);
    CloseHandle(h);
    return rva;
}

/* 取目标进程里 32 位 kernel32 的基址（取不到就用本进程的，同一次开机一致） */
static ULONG_PTR TargetKernel32(HANDLE hProc)
{
    HMODULE mods[256];
    DWORD   needed = 0, i;
    WCHAR   name[MAX_PATH];

    if (EnumProcessModulesEx(hProc, mods, sizeof(mods), &needed, 0x01))
    {
        for (i = 0; i < needed / sizeof(HMODULE); i++)
        {
            if (!GetModuleBaseNameW(hProc, mods[i], name, MAX_PATH)) continue;
            if (!lstrcmpiW(name, L"kernel32.dll")) return (ULONG_PTR)mods[i];
        }
    }
    return (ULONG_PTR)GetModuleHandleW(L"kernel32.dll");
}

/*
 * 32 位目标的注入：改写模块入口点。
 *
 * 为什么不用 CreateRemoteThread + LoadLibrary？
 *   在初始断点处主线程仍持有加载器锁，远程线程会一直阻塞在 LoadLibrary
 *   里，直到主线程继续运行到入口点之后才装好 DLL —— 那时程序早就检查完
 *   单实例锁了。这里改成把入口点前 5 字节替换成一段跳转桩：
 *       push dllPath; call LoadLibraryW; 还原入口点 5 字节; jmp 入口点
 *   程序一进入就被接管，装完 hook 再原样继续，完全消除竞态。
 */
static BOOL g_skipLoad = FALSE;
static WCHAR g_diag[160];

static BOOL PatchEntryAndInject(DWORD pid, ULONG_PTR imageBase, LPCWSTR exePath, LPCWSTR dllPath)
{
    HANDLE  hProc;
    DWORD   entryRva = EntryPointRva(exePath);
    ULONG_PTR entryVA, llw, stubVA, pathVA;
    BYTE    orig[5];
    SIZE_T  written = 0;
    BYTE    stub[128];
    SIZE_T  stubLen = 0, pathLen;
    DWORD   oldProtect, n, i;
    ULONG_PTR k32;

    if (!entryRva) return FALSE;

    hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ |
                        PROCESS_QUERY_INFORMATION, FALSE, pid);
    if (!hProc) return FALSE;

    entryVA = imageBase + entryRva;
    k32 = TargetKernel32(hProc);
    llw = k32 + ((ULONG_PTR)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW") -
                 (ULONG_PTR)GetModuleHandleW(L"kernel32.dll"));

    if (!ReadProcessMemory(hProc, (LPCVOID)entryVA, orig, 5, &written) || written != 5)
    {
        CloseHandle(hProc);
        return FALSE;
    }

    pathLen = (lstrlenW(dllPath) + 1) * sizeof(WCHAR);
    stubVA = (ULONG_PTR)VirtualAllocEx(hProc, NULL, pathLen + sizeof(stub),
                                       MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!stubVA) { CloseHandle(hProc); return FALSE; }
    pathVA = stubVA + sizeof(stub);

    /* ---- 生成跳转桩（x86 机器码） ---- */
    stubLen = 0;
    if (!g_skipLoad)
    {
        stub[stubLen++] = 0x68;                          /* push pathVA */
        *(DWORD*)(stub + stubLen) = (DWORD)pathVA; stubLen += 4;
        stub[stubLen++] = 0xB8;                          /* mov eax, LoadLibraryW */
        *(DWORD*)(stub + stubLen) = (DWORD)llw; stubLen += 4;
        stub[stubLen++] = 0xFF; stub[stubLen++] = 0xD0;  /* call eax */
    }
    for (i = 0; i < 5; i++)                              /* 还原入口点原始字节 */
    {
        stub[stubLen++] = 0xC6;
        stub[stubLen++] = 0x05;
        *(DWORD*)(stub + stubLen) = (DWORD)(entryVA + i); stubLen += 4;
        stub[stubLen++] = orig[i];
    }
    stub[stubLen++] = 0xE9;                              /* jmp entryVA */
    *(DWORD*)(stub + stubLen) = (DWORD)(entryVA - (stubVA + stubLen + 4)); stubLen += 4;

    if (!WriteProcessMemory(hProc, (LPVOID)stubVA, stub, stubLen, &written) || written != stubLen ||
        !WriteProcessMemory(hProc, (LPVOID)pathVA, dllPath, pathLen, &written) || written != pathLen)
    {
        VirtualFreeEx(hProc, (LPVOID)stubVA, 0, MEM_RELEASE);
        CloseHandle(hProc);
        return FALSE;
    }

    if (!VirtualProtectEx(hProc, (LPVOID)entryVA, 5, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        VirtualFreeEx(hProc, (LPVOID)stubVA, 0, MEM_RELEASE);
        CloseHandle(hProc);
        return FALSE;
    }
    {
        BYTE patch[5];
        patch[0] = 0xE9;
        *(DWORD*)(patch + 1) = (DWORD)(stubVA - (entryVA + 5));
        WriteProcessMemory(hProc, (LPVOID)entryVA, patch, 5, &n);
    }
    /* 注意：这里故意不把入口点页面改回只读 —— 跳转桩执行完要回写原始 5 字节，
       页面必须是可写的。 */
    FlushInstructionCache(hProc, (LPVOID)entryVA, 5);

    CloseHandle(hProc);
    return TRUE;
}

/* 把结果写到宿主指定的文件（用文件而不是 stdout，避免管道被目标进程句柄拖住） */
static LPWSTR g_pidFile = NULL;

static void Report(LPCWSTR text)
{
    HANDLE h;
    DWORD n, len;
    char utf8[256];
    if (!g_pidFile || !*g_pidFile) return;
    h = CreateFileW(g_pidFile, GENERIC_WRITE, FILE_SHARE_READ, NULL,
                    CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    len = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, sizeof(utf8), NULL, NULL);
    if (len > 0) WriteFile(h, utf8, (DWORD)(len - 1), &n, NULL);
    CloseHandle(h);
}

/* 输出到 stdout（可能是管道，必须用 WriteFile 而不是 WriteConsoleW） */
static void Out(LPCWSTR text)
{
    char utf8[256];
    DWORD n;
    int len = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, sizeof(utf8), NULL, NULL);
    if (len > 0)
        WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), utf8, (DWORD)(len - 1), &n, NULL);
}

int WINAPI wWinMain(HINSTANCE a, HINSTANCE b, LPWSTR c, int d)
{
    LPWSTR exe, args, group, dll, logPath, hideNames, strongSfx, regFilter;
    WCHAR  buf[128];
    LPWSTR cmd;
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    DWORD t0;
    BOOL injected = FALSE;
    ULONG_PTR imageBase = 0;

    (void)a; (void)b; (void)c; (void)d;

    g_argv = CommandLineToArgvW(GetCommandLineW(), &g_argc);
    if (!g_argv) return 10;

    exe  = Arg(L"--exe");
    args = Arg(L"--args");
    group = Arg(L"--group");
    dll  = Arg(L"--dll");
    logPath = Arg(L"--log");
    hideNames = Arg(L"--hide");
    strongSfx = Arg(L"--strong-sfx");
    regFilter = Arg(L"--reg");
    g_pidFile = Arg(L"--pidfile");
    g_skipLoad = (Arg(L"--no-load") != NULL);
    if (!exe || !dll) return 11;

    /* 目标继承本进程环境，再补上组号与日志路径 */
    if (group && *group) SetEnvironmentVariableW(L"ProcessGroup", group);
    if (logPath && *logPath) SetEnvironmentVariableW(L"V5SharpLog", logPath);
    else SetEnvironmentVariableW(L"V5SharpLog", NULL);

    /* 需要让目标"看不见"的进程名（给启动器类程序用），hook 模块从这里读 */
    if (hideNames && *hideNames) SetEnvironmentVariableW(L"V5HideNames", hideNames);
    else SetEnvironmentVariableW(L"V5HideNames", NULL);

    /* 全局隐藏：进程枚举里摘掉其他实例 */
    if (Arg(L"--global-hide")) SetEnvironmentVariableW(L"V5GlobalHide", L"1");
    else SetEnvironmentVariableW(L"V5GlobalHide", NULL);

    /* 注册表隔离关键词 */
    if (regFilter && *regFilter) SetEnvironmentVariableW(L"V5RegFilter", regFilter);
    else SetEnvironmentVariableW(L"V5RegFilter", NULL);

    /* 加强模式：文件锁虚拟化（具体逻辑在注入模块里） */
    if (Arg(L"--strong")) SetEnvironmentVariableW(L"V5Strong", L"1");
    else SetEnvironmentVariableW(L"V5Strong", NULL);

    if (strongSfx && *strongSfx) SetEnvironmentVariableW(L"V5StrongSuffixes", strongSfx);
    else SetEnvironmentVariableW(L"V5StrongSuffixes", NULL);

    cmd = (LPWSTR)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0x10000 * sizeof(WCHAR));
    if (!cmd) return 16;
    /*
     * 不用 wsprintfW 拼命令行：它内部有 1024 字符上限，超长路径/参数会被静默截断，
     * 目标就会带着半截参数启动。这里显式拼接，缓冲 64K 足够。
     */
    lstrcpynW(cmd, L"\"", 0x10000);
    lstrcatW(cmd, exe);
    lstrcatW(cmd, L"\" ");
    if (args) lstrcatW(cmd, args);

    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    /* 目标不要继承本进程的 stdout，否则宿主读管道会被目标的生命周期拖住 */
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = si.hStdOutput = si.hStdError = NULL;
    ZeroMemory(&pi, sizeof(pi));

    if (!CreateProcessW(exe, cmd, NULL, NULL, FALSE, DEBUG_ONLY_THIS_PROCESS,
                        NULL, NULL, &si, &pi))
    {
        wsprintfW(buf, L"ERR CreateProcess=%u", GetLastError());
        Report(buf); Out(buf);
        return 12;
    }

    t0 = GetTickCount();
    while (GetTickCount() - t0 < 20000)
    {
        DEBUG_EVENT de;
        if (!WaitForDebugEvent(&de, 5000)) continue;

        if (de.dwDebugEventCode == CREATE_PROCESS_DEBUG_EVENT)
            imageBase = (ULONG_PTR)de.u.CreateProcessInfo.lpBaseOfImage;

        if (de.dwDebugEventCode == EXIT_PROCESS_DEBUG_EVENT)
        {
            ContinueDebugEvent(de.dwProcessId, de.dwThreadId, DBG_CONTINUE);
            Report(L"ERR exited-before-inject"); Out(L"ERR exited-before-inject");
            return 13;
        }

        if (!injected && de.dwDebugEventCode == EXCEPTION_DEBUG_EVENT &&
            de.u.Exception.ExceptionRecord.ExceptionCode == EXCEPTION_BREAKPOINT)
        {
            /* 32 位加载器已初始化、入口点尚未执行 */
            SuspendThread(pi.hThread);
            ContinueDebugEvent(de.dwProcessId, de.dwThreadId, DBG_CONTINUE);
            DebugActiveProcessStop(pi.dwProcessId);

            injected = TRUE;
            if (Arg(L"--diag"))
            {
                DWORD er = EntryPointRva(exe);
                wsprintfW(g_diag, L"base=0x%X rva=0x%X entry=0x%X",
                          (DWORD)imageBase, er, (DWORD)(imageBase + er));
            }
            if (!PatchEntryAndInject(pi.dwProcessId, imageBase, exe, dll))
            {
                ResumeThread(pi.hThread);
                Report(L"ERR inject-failed"); Out(L"ERR inject-failed");
                return 14;
            }
            ResumeThread(pi.hThread);          /* 主线程进入入口点即被接管道 */

            wsprintfW(buf, L"PID=%u %s", pi.dwProcessId, g_diag);
            Report(buf); Out(buf);
            return 0;
        }

        ContinueDebugEvent(de.dwProcessId, de.dwThreadId,
            de.dwDebugEventCode == EXCEPTION_DEBUG_EVENT ? DBG_EXCEPTION_NOT_HANDLED : DBG_CONTINUE);
    }

    Report(L"ERR no-breakpoint"); Out(L"ERR no-breakpoint");
    return 15;
}
