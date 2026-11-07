/*
 * filter.c - 系统对象白名单（通配符 * ?，大小写不敏感）
 *
 * 原始 v5_hook.dll 的规则：先把名字前面的 "Global\" / "Local\" 去掉，
 * 再逐个匹配内置的白名单数组，命中则"不改名"，避免把 RPC 端点、输入法共享
 * 内存、会话级事件改坏导致系统/输入法/桌面异常。
 * 下表的条目直接来自原始 DLL 的字符串表（已去重合并）。
 */
#include "v5hook.h"

static const WCHAR* kCommon[] = {
    L"\\KernelObjects\\*",  /* 系统级内核对象，改名会影响整个系统 */
    L"Telesto*",            /* CLR 遥测事件 */
    L"Prayaya*",          /* 原厂沙盘对象，不改 */
    L"DBWIN_*",           /* 调试输出缓冲 */
    L"Shim*",             /* 兼容性垫片 */
    L"V5Sharp*",          /* 本工具自身的对象 */
    NULL
};

static const WCHAR* kMutant[] = {
    L"PowerProfileRegistrySemaphore",
    L"shell.*",
    L"MutexMSPY*",
    L"GlobalMutexMSPY*",
    L"DBWinMutex",
    L"HGFSMUTEX*",
    L"Ras_*",
    L"wbuserenv*",
    L"wbfilemap*",
    L"SGPY*",
    NULL
};

static const WCHAR* kEvent[] = {
    L"SvcctrlStartEvent*",
    L"DownloadProcessServiceEvent",
    L"RouterPreInitEvent",
    L"\\SECURITY\\LSA_AUTHENTICATION_INITIALIZED",
    L"SeLsaInitEvent",
    L"LSA_RPC_SERVER_ACTIVE",
    L"TermSrvReadyEvent",
    L"WinSta0_DesktopSwitch",
    L"wkssvc*",
    L"AgentToWkssvcEvent",
    L"MSCTF*",
    L"eventlog",
    L"kersvc*",
    L"Winsock2*",
    L"ROUTER",
    L"DAV RPC SERVICE",
    NULL
};

static const WCHAR* kSection[] = {
    L"*.dll", L"*.exe", L"*.sys", L"*.mui", L"*.nls",   /* 镜像/映射名，改了会把加载器弄崩 */
    L"\\NLS\\NlsSection*",
    L"SENS Information Cache",
    L"CiceroSharedMemDefault*",
    L"ShimSharedMemory",
    L"sogouInput*",
    L"mem_py*",
    L"QQPinyin*",
    L"HYPIM_WORDLIB_SHARED*",
    L"IME_SETTINGS_MEM",
    L"chenhuznwb",
    L"WBJJ*",
    L"JPWB",
    L"Filemap_Wb*",
    L"GooglePinyinDashboardIPCMem",
    L"GPY2SETTINGS",
    L"_wnpy_table_mapping",
    L"FREEIME*",
    L"DfShared*",
    L"DFMap*",
    L"DfRoot*",
    L"FileMappingMSPY*",
    L"mmGlobalPnpInfo",
    NULL
};

static const WCHAR* kPipe[] = {
    L"lsarpc", L"samr", L"netlogon", L"lsass", L"protected_storage",
    L"spoolss", L"srvsvc", L"wkssvc", L"winlogonrpc", L"trkwks",
    L"LSM_API_service", L"eventlog", L"ROUTER", L"DAV RPC SERVICE",
    L"ShimViewer", L"chrome.*", L"Winsock2*", L"kersvc*", L"epmapper",
    L"atsvc", L"keysvc", L"plugplay", L"ntsvcs", L"locator",
    NULL
};

static const WCHAR** V5ListFor(V5_OBJTYPE t)
{
    switch (t) {
    case OBJ_MUTANT:    return kMutant;
    case OBJ_EVENT:     return kEvent;
    case OBJ_SECTION:   return kSection;
    case OBJ_PIPE:      return kPipe;
    default:            return NULL;   /* semaphore / timer 只用通用表 */
    }
}

static WCHAR V5Upper(WCHAR c)
{
    return (c >= L'a' && c <= L'z') ? (WCHAR)(c - 32) : c;
}

static BOOL V5WildMatch(const WCHAR* s, const WCHAR* p)
{
    const WCHAR* star = NULL;
    const WCHAR* retry = NULL;

    while (*s) {
        if (*p == L'?' || (*p && V5Upper(*p) == V5Upper(*s))) { s++; p++; }
        else if (*p == L'*') { star = p++; retry = s; }
        else if (star)       { p = star + 1; s = ++retry; }
        else                 return FALSE;
    }
    while (*p == L'*') p++;
    return *p == L'\0';
}

static BOOL V5MatchList(const WCHAR* s, const WCHAR** list)
{
    if (!list) return FALSE;
    for (; *list; list++)
        if (V5WildMatch(s, *list)) return TRUE;
    return FALSE;
}

BOOL V5MatchPattern(const WCHAR* s, const WCHAR* pattern)
{
    return V5WildMatch(s, pattern);
}

BOOL V5IsSkippedName(const WCHAR* name, V5_OBJTYPE type)
{
    const WCHAR* bare = name;

    if (!name || !*name) return TRUE;

    /* 与原始实现一致：剥掉 Local\ / Global\ 前缀再匹配 */
    if (V5WildMatch(name, L"Global\\*"))      bare = name + 7;
    else if (V5WildMatch(name, L"Local\\*"))  bare = name + 6;

    if (V5MatchList(name, kCommon) || V5MatchList(bare, kCommon)) return TRUE;
    if (V5MatchList(bare, V5ListFor(type))) return TRUE;
    return FALSE;
}

BOOL V5IsPipePath(const WCHAR* name)
{
    const WCHAR* p;
    int i;

    if (!name) return FALSE;
    if (V5WildMatch(name, L"\\Device\\NamedPipe\\*")) return TRUE;

    /* 查找子串 "\pipe\"（大小写不敏感） */
    for (p = name; p[0] && p[1] && p[2] && p[3] && p[4] && p[5]; p++) {
        if (p[0] != L'\\') continue;
        for (i = 1; i <= 4; i++)
            if (V5Upper(p[i]) != "PIPE"[i - 1]) break;
        if (i == 5 && p[5] == L'\\') return TRUE;
    }
    return FALSE;
}
