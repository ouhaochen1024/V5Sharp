/*
 * v5hook.c - V5Sharp 注入模块（x86 / x64 同一份源码）
 *
 * 职责（与原 v5_hook.dll 一一对应）：
 *   1. 读取环境变量 ProcessGroup 作为本实例的"组号"；
 *   2. hook ntdll 的 NtCreateXXX / NtOpenXXX，把命名内核对象的名字尾部追加
 *      "Group_<组号>"，让每个实例拥有独立的命名空间；
 *   3. 命中系统白名单的对象不改名（否则 RPC / 输入法 / 会话事件会被改坏）；
 *   4. 可选：把每次改名写进日志文件，便于排查。
 *
 * 与原始实现的差异：
 *   - 原版用 mHook + 反汇编引擎，这里用系统调用桩直复制（见 hook.c）；
 *   - 原版依赖 CRT，这里只用 kernel32/user32；
 *   - 原版把日志写进 DBWIN 共享内存再由宿主落盘，这里直接写文件（更简单）。
 */
#include "v5hook.h"

#pragma comment(lib, "user32.lib")

/* ============================ 全局状态 ============================ */

static DWORD             g_group = 0;
static WCHAR             g_suffix[40];       /* L"Group_123456" */
static CRITICAL_SECTION  g_cs;
static HANDLE            g_log = INVALID_HANDLE_VALUE;
static WCHAR             g_logPath[MAX_PATH];
static volatile LONG     g_logOn = 0;
static WCHAR             g_exeName[MAX_PATH];
static DWORD            g_tlsBusy = TLS_OUT_OF_INDEXES;  /* 防重入（不用 __declspec(thread)，避免依赖 CRT） */

/* ============================ 日志 ============================ */

BOOL V5LogEnabled(void) { return g_logOn != 0; }

static void V5LogOpen(void)
{
    if (g_log != INVALID_HANDLE_VALUE) return;
    if (!g_logPath[0]) return;
    g_log = CreateFileW(g_logPath, FILE_APPEND_DATA,
                        FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
}

/*
 * 日志采用缓冲写：
 * 游戏每秒会有成百上千次对象操作，逐行 WriteFile 会明显拖慢宿主线程。
 * 这里先在内存里拼行，攒够 3.5KB 或距上次落盘超过 400ms 再一次性写出。
 */
static WCHAR g_lineBuf[3800];
static int   g_lineLen = 0;
static DWORD g_lastFlush = 0;

static void V5LogFlushLocked(void)
{
    DWORD written;
    V5LogOpen();
    if (g_log != INVALID_HANDLE_VALUE && g_lineLen > 0)
        WriteFile(g_log, g_lineBuf, (DWORD)(g_lineLen * sizeof(WCHAR)), &written, NULL);
    g_lineLen = 0;
    g_lastFlush = GetTickCount();
}

void V5LogFlush(void)
{
    if (!g_logOn) return;
    EnterCriticalSection(&g_cs);
    V5LogFlushLocked();
    LeaveCriticalSection(&g_cs);
}

/*
 * 行缓冲：前缀约 80 字符 + exe 名(≤259) + 原名(≤559) + 新名(≤571)，
 * 最坏接近 1500 字符。原来只给 900，超长路径下 wsprintfW 会写穿栈。
 *
 * 注意放在全局而不是栈上：3.2KB 的栈帧会让编译器插入 __chkstk 调用，
 * 而本模块用 /NODEFAULTLIB 链接，没有 CRT 提供这个符号。
 * 访问全部在 g_cs 保护下，跨线程安全。
 */
static WCHAR g_lineScratch[1600];

void V5Log(const WCHAR* api, const WCHAR* oldName, const WCHAR* newName)
{
    SYSTEMTIME st;
    int len;
    const int cap = (int)(sizeof(g_lineBuf) / sizeof(WCHAR)) - 1;

    if (!g_logOn) return;
    if (g_tlsBusy != TLS_OUT_OF_INDEXES && TlsGetValue(g_tlsBusy)) return;
    TlsSetValue(g_tlsBusy, (LPVOID)1);          /* 防重入：日志本身也会碰内核对象 */

    EnterCriticalSection(&g_cs);

    GetLocalTime(&st);
    len = wsprintfW(g_lineScratch, L"[%u][%u][%s][%02d:%02d:%02d.%03d] %s: %s==%s\r\n",
                    GetCurrentProcessId(), GetCurrentThreadId(), g_exeName,
                    st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                    api, oldName ? oldName : L"(null)", newName ? newName : L"(null)");

    if (len >= cap)
    {
        /* 单行就超过整个缓冲区（理论上不会发生）：先落盘，再单独写这一行 */
        DWORD written;
        V5LogFlushLocked();
        V5LogOpen();
        if (g_log != INVALID_HANDLE_VALUE)
            WriteFile(g_log, g_lineScratch, (DWORD)(len * sizeof(WCHAR)), &written, NULL);
    }
    else if (len > 0)
    {
        /*
         * 先确认放得下再拷贝。上一版是"先拷后判"，当缓冲区还剩一点空间、
         * 而新行明显更长时，会从 g_lineBuf 末尾写出去，覆盖相邻的全局变量
         * （g_log / g_logPath / g_lineLen 都在旁边）。
         */
        if (g_lineLen + len >= cap) V5LogFlushLocked();

        {
            int i;
            volatile WCHAR* d = g_lineBuf + g_lineLen;
            for (i = 0; i < len; i++) d[i] = g_lineScratch[i];
        }
        g_lineLen += len;
        g_lineBuf[g_lineLen] = 0;

        /*
         * 再判断要不要落盘：缓冲区快满了，或者距上次落盘超过 400ms。
         * 这里不能加 "g_lineLen > 0" 的条件 —— 那样第一次调用只会把数据留在内存里，
         * 短命进程在退出前若没有第二次调用，日志文件根本不会被创建，整份日志丢失。
         * 首次调用直接落盘即可（g_lastFlush 初值为 0）。
         */
        if (g_lineLen >= cap - 160 || g_lastFlush == 0 ||
            GetTickCount() - g_lastFlush > 400)
            V5LogFlushLocked();
    }

    LeaveCriticalSection(&g_cs);

    TlsSetValue(g_tlsBusy, (LPVOID)0);
}

/* ============================ 名称改写 ============================ */

BOOL V5RewriteObject(const V5_OA* oa, V5_OBJTYPE type,
                     WCHAR* buf, SIZE_T cchBuf,
                     V5_OA* outOA, V5_US* outUS)
{
    const V5_US* name;
    SIZE_T len;

    if (!oa || !oa->ObjectName) return FALSE;
    name = oa->ObjectName;
    if (!name->Buffer || name->Length == 0) return FALSE;

    len = name->Length / sizeof(WCHAR);
    if (len == 0 || len >= cchBuf - 40) return FALSE;

    /*
     * 先把名字复制成一份以 0 结尾的副本，后续所有字符串判断都基于这份副本。
     * UNICODE_STRING.Buffer 不保证有结尾 0（Length 才是准的），直接拿它去跑
     * V5IsPipePath / 白名单通配符会读出对象名之外的内存。
     */
    {
        SIZE_T i;
        for (i = 0; i < len; i++) buf[i] = name->Buffer[i];
    }
    buf[len] = L'\0';

    /* 管道类只处理 \pipe\ 路径，普通文件不动 */
    if (type == OBJ_PIPE && !V5IsPipePath(buf)) return FALSE;

    /* 已经追加过，别二次改写 */
    if (len > 6) {
        const WCHAR* t = buf + len - 6;
        if (t[0] == L'G' && t[1] == L'r' && t[2] == L'o' && t[3] == L'u' && t[4] == L'p' && t[5] == L'_')
            return FALSE;
    }

    if (V5IsSkippedName(buf, type)) return FALSE;

    lstrcatW(buf, g_suffix);

    *outOA = *oa;
    outUS->Length        = (USHORT)(lstrlenW(buf) * sizeof(WCHAR));
    outUS->MaximumLength = (USHORT)(outUS->Length + sizeof(WCHAR));
    outUS->Buffer        = buf;
    outOA->ObjectName    = outUS;
    return TRUE;
}

/* ============================ 各 API 的 hook ============================ */
/*
 * 每个 hook 都必须使用与目标函数完全一致的原型：
 * x86 是 callee 清栈（stdcall），参数个数/字节数不一致会直接崩栈；
 * x64 虽然由调用方清栈，但参数数量影响阴影空间之外的可读性，同样按原型写。
 */

#define REWRITE(TYPE)                                                        \
    WCHAR buf[600]; V5_OA oa2; V5_US us;                                     \
    if (V5RewriteObject(oa, (TYPE), buf, 600, &oa2, &us)) {                   \
        V5Log(g_apiName, oa->ObjectName->Buffer, buf);

#define REWRITE_END() }

/* ---- Mutant（互斥体，最常见的单实例锁）---- */
typedef NTSTATUS (NTAPI *PFN_NtCreateMutant)(PHANDLE, ACCESS_MASK, V5_OA*, BOOLEAN);
static PFN_NtCreateMutant p_NtCreateMutant;

static NTSTATUS NTAPI Hook_NtCreateMutant(PHANDLE h, ACCESS_MASK acc, V5_OA* oa, BOOLEAN owner)
{
    const WCHAR* g_apiName = L"NtCreateMutant";
    REWRITE(OBJ_MUTANT)
        NTSTATUS r = p_NtCreateMutant(h, acc, &oa2, owner);
        return r;
    REWRITE_END()
    return p_NtCreateMutant(h, acc, oa, owner);
}

typedef NTSTATUS (NTAPI *PFN_NtOpenMutant)(PHANDLE, ACCESS_MASK, V5_OA*);
static PFN_NtOpenMutant p_NtOpenMutant;

static NTSTATUS NTAPI Hook_NtOpenMutant(PHANDLE h, ACCESS_MASK acc, V5_OA* oa)
{
    const WCHAR* g_apiName = L"NtOpenMutant";
    REWRITE(OBJ_MUTANT)
        NTSTATUS r = p_NtOpenMutant(h, acc, &oa2);
        if (r < 0) r = p_NtOpenMutant(h, acc, oa);   /* 命名空间里没有该对象，回退原名 */
        return r;
    REWRITE_END()
    return p_NtOpenMutant(h, acc, oa);
}

/* ---- Event ---- */
typedef NTSTATUS (NTAPI *PFN_NtCreateEvent)(PHANDLE, ACCESS_MASK, V5_OA*, ULONG, BOOLEAN);
static PFN_NtCreateEvent p_NtCreateEvent;

static NTSTATUS NTAPI Hook_NtCreateEvent(PHANDLE h, ACCESS_MASK acc, V5_OA* oa, ULONG type, BOOLEAN state)
{
    const WCHAR* g_apiName = L"NtCreateEvent";
    REWRITE(OBJ_EVENT)
        NTSTATUS r = p_NtCreateEvent(h, acc, &oa2, type, state);
        return r;
    REWRITE_END()
    return p_NtCreateEvent(h, acc, oa, type, state);
}

typedef NTSTATUS (NTAPI *PFN_NtOpenEvent)(PHANDLE, ACCESS_MASK, V5_OA*);
static PFN_NtOpenEvent p_NtOpenEvent;

static NTSTATUS NTAPI Hook_NtOpenEvent(PHANDLE h, ACCESS_MASK acc, V5_OA* oa)
{
    const WCHAR* g_apiName = L"NtOpenEvent";
    REWRITE(OBJ_EVENT)
        NTSTATUS r = p_NtOpenEvent(h, acc, &oa2);
        if (r < 0) r = p_NtOpenEvent(h, acc, oa);
        return r;
    REWRITE_END()
    return p_NtOpenEvent(h, acc, oa);
}

/* ---- Semaphore ---- */
typedef NTSTATUS (NTAPI *PFN_NtCreateSemaphore)(PHANDLE, ACCESS_MASK, V5_OA*, LONG, LONG);
static PFN_NtCreateSemaphore p_NtCreateSemaphore;

static NTSTATUS NTAPI Hook_NtCreateSemaphore(PHANDLE h, ACCESS_MASK acc, V5_OA* oa, LONG init, LONG max)
{
    const WCHAR* g_apiName = L"NtCreateSemaphore";
    REWRITE(OBJ_SEMAPHORE)
        NTSTATUS r = p_NtCreateSemaphore(h, acc, &oa2, init, max);
        return r;
    REWRITE_END()
    return p_NtCreateSemaphore(h, acc, oa, init, max);
}

typedef NTSTATUS (NTAPI *PFN_NtOpenSemaphore)(PHANDLE, ACCESS_MASK, V5_OA*);
static PFN_NtOpenSemaphore p_NtOpenSemaphore;

static NTSTATUS NTAPI Hook_NtOpenSemaphore(PHANDLE h, ACCESS_MASK acc, V5_OA* oa)
{
    const WCHAR* g_apiName = L"NtOpenSemaphore";
    REWRITE(OBJ_SEMAPHORE)
        NTSTATUS r = p_NtOpenSemaphore(h, acc, &oa2);
        if (r < 0) r = p_NtOpenSemaphore(h, acc, oa);
        return r;
    REWRITE_END()
    return p_NtOpenSemaphore(h, acc, oa);
}

/* ---- Section（共享内存，很多游戏用来做单实例 / 进程间通信）---- */
typedef NTSTATUS (NTAPI *PFN_NtCreateSection)(PHANDLE, ACCESS_MASK, V5_OA*, PLARGE_INTEGER, ULONG, ULONG, HANDLE);
static PFN_NtCreateSection p_NtCreateSection;

static NTSTATUS NTAPI Hook_NtCreateSection(PHANDLE h, ACCESS_MASK acc, V5_OA* oa,
                                           PLARGE_INTEGER size, ULONG protect,
                                           ULONG attrs, HANDLE file)
{
    const WCHAR* g_apiName = L"NtCreateSection";
    REWRITE(OBJ_SECTION)
        NTSTATUS r = p_NtCreateSection(h, acc, &oa2, size, protect, attrs, file);
        return r;
    REWRITE_END()
    return p_NtCreateSection(h, acc, oa, size, protect, attrs, file);
}

typedef NTSTATUS (NTAPI *PFN_NtOpenSection)(PHANDLE, ACCESS_MASK, V5_OA*);
static PFN_NtOpenSection p_NtOpenSection;

static NTSTATUS NTAPI Hook_NtOpenSection(PHANDLE h, ACCESS_MASK acc, V5_OA* oa)
{
    const WCHAR* g_apiName = L"NtOpenSection";
    REWRITE(OBJ_SECTION)
        NTSTATUS r = p_NtOpenSection(h, acc, &oa2);
        if (r < 0) r = p_NtOpenSection(h, acc, oa);
        return r;
    REWRITE_END()
    return p_NtOpenSection(h, acc, oa);
}

/* ---- Timer ---- */
typedef NTSTATUS (NTAPI *PFN_NtCreateTimer)(PHANDLE, ACCESS_MASK, V5_OA*, ULONG);
static PFN_NtCreateTimer p_NtCreateTimer;

static NTSTATUS NTAPI Hook_NtCreateTimer(PHANDLE h, ACCESS_MASK acc, V5_OA* oa, ULONG type)
{
    const WCHAR* g_apiName = L"NtCreateTimer";
    REWRITE(OBJ_TIMER)
        NTSTATUS r = p_NtCreateTimer(h, acc, &oa2, type);
        return r;
    REWRITE_END()
    return p_NtCreateTimer(h, acc, oa, type);
}

typedef NTSTATUS (NTAPI *PFN_NtOpenTimer)(PHANDLE, ACCESS_MASK, V5_OA*);
static PFN_NtOpenTimer p_NtOpenTimer;

static NTSTATUS NTAPI Hook_NtOpenTimer(PHANDLE h, ACCESS_MASK acc, V5_OA* oa)
{
    const WCHAR* g_apiName = L"NtOpenTimer";
    REWRITE(OBJ_TIMER)
        NTSTATUS r = p_NtOpenTimer(h, acc, &oa2);
        if (r < 0) r = p_NtOpenTimer(h, acc, oa);
        return r;
    REWRITE_END()
    return p_NtOpenTimer(h, acc, oa);
}

/* ---- 命名管道：IPC / 单实例检测的重灾区 ---- */
typedef NTSTATUS (NTAPI *PFN_NtCreateNamedPipeFile)(
    PHANDLE, ULONG, V5_OA*, V5_IOSB*, ULONG, ULONG, ULONG,
    ULONG, ULONG, ULONG, ULONG, ULONG, ULONG, PLARGE_INTEGER);
static PFN_NtCreateNamedPipeFile p_NtCreateNamedPipeFile;

static NTSTATUS NTAPI Hook_NtCreateNamedPipeFile(
    PHANDLE h, ULONG acc, V5_OA* oa, V5_IOSB* iosb, ULONG share,
    ULONG disp, ULONG opts, ULONG type, ULONG read, ULONG wait,
    ULONG maxInst, ULONG inBuf, ULONG outBuf, PLARGE_INTEGER timeout)
{
    const WCHAR* g_apiName = L"NtCreateNamedPipeFile";
    REWRITE(OBJ_PIPE)
        NTSTATUS r = p_NtCreateNamedPipeFile(h, acc, &oa2, iosb, share, disp, opts,
                                             type, read, wait, maxInst, inBuf, outBuf, timeout);
        if (r < 0) r = p_NtCreateNamedPipeFile(h, acc, oa, iosb, share, disp, opts,
                                               type, read, wait, maxInst, inBuf, outBuf, timeout);
        return r;
    REWRITE_END()
    return p_NtCreateNamedPipeFile(h, acc, oa, iosb, share, disp, opts,
                                   type, read, wait, maxInst, inBuf, outBuf, timeout);
}

/* ==================== 加强模式：文件锁虚拟化 ====================
 *
 * 很多程序判断"是否已有实例"并不靠命名对象，而是靠文件锁：
 *
 *     CreateFile(自身exe, GENERIC_READ|GENERIC_WRITE, dwShareMode = 0, ...)
 *     失败且 GetLastError() == ERROR_SHARING_VIOLATION  ->  "已有实例在运行"
 *
 * 因为运行中的 exe 映像本身就占着这张文件，第二个实例的独占打开必然失败。
 *
 * 加强模式在 NtCreateFile / NtOpenFile 这一层把"独占"放宽成"共享读/写/删"，
 * 句柄仍指向真实文件（内容、偏移、读写语义全部不变），只是不再因为别人
 * 开着就拒绝自己 —— 于是每个实例的独占自检都"成功"，谁也发现不了谁。
 *
 * 作用范围严格受限：
 *   1. 本进程自身的 exe（最常见的检测目标）；
 *   2. 后缀命中的锁文件（默认 *.lock / *.pid / *.mutex）；
 *   3. 兜底：程序目录内、独占打开却报共享冲突的文件。
 *
 * 注意：UNICODE_STRING 的 Buffer 不保证以 0 结尾，所有路径处理都按 Length
 * 复制，绝不能用 lstrlenW 直接读 —— 越界会直接把目标进程带走。
 */
#define STATUS_SHARING_VIOLATION_ ((NTSTATUS)0xC0000043L)
#define V5_SHARE_ALL (FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)

static volatile LONG g_strong = 0;
static WCHAR  g_exeFull[MAX_PATH];        /* 自身 exe 完整 DOS 路径 */
static WCHAR  g_exeDir[MAX_PATH];         /* 自身 exe 所在目录，带结尾反斜杠 */
static WCHAR  g_strongSfx[260];           /* 锁文件后缀名单，; 分隔 */

/* 把 NT 对象名转成本地 DOS 路径；不是 "X:/..." 形式就返回 FALSE */
static BOOL V5NtPathToDos(const V5_US* name, WCHAR* out, SIZE_T cch)
{
    const WCHAR* s;
    SIZE_T len, i;

    if (!name || !name->Buffer) return FALSE;
    len = name->Length / sizeof(WCHAR);
    if (len < 4 || len >= cch) return FALSE;
    s = name->Buffer;

    /* \??\C:/...   或   \\?\C:/... */
    if (s[0] == L'\\' && s[1] == L'?' && s[2] == L'?' && s[3] == L'\\') { s += 4; len -= 4; }
    else if (s[0] == L'\\' && s[1] == L'\\' && s[2] == L'?' && s[3] == L'\\') { s += 4; len -= 4; }

    /* 只接受 "X:/"：\Device\、\SystemRoot\、相对路径一律不碰 */
    if (len < 4) return FALSE;
    if (!(((s[0] >= L'A' && s[0] <= L'Z') || (s[0] >= L'a' && s[0] <= L'z')) && s[1] == L':'))
        return FALSE;

    for (i = 0; i < len && i < cch - 1; i++) out[i] = s[i];
    out[i] = L'\0';
    return TRUE;
}

static BOOL V5SuffixHit(const WCHAR* dos, const WCHAR* list)
{
    WCHAR pat[64];
    const WCHAR* p = list;

    while (*p)
    {
        int n = 0;
        while (*p && *p != L';' && n < 63) pat[n++] = *p++;
        pat[n] = 0;
        if (*p == L';') p++;
        if (n > 0 && V5MatchPattern(dos, pat)) return TRUE;
    }
    return FALSE;
}

/* 是不是在自身 exe 所在目录内 */
static BOOL V5InExeDir(const WCHAR* dos)
{
    WCHAR head[MAX_PATH];
    int n = (int)lstrlenW(g_exeDir);

    if (n <= 0) return FALSE;
    lstrcpynW(head, dos, n + 1);
    return lstrcmpiW(head, g_exeDir) == 0;
}

/* 预测式：明确要放宽的目标（自身 exe / 锁文件后缀） */
static BOOL V5StrongExplicit(const WCHAR* dos)
{
    if (g_exeFull[0] && lstrcmpiW(dos, g_exeFull) == 0) return TRUE;
    if (g_strongSfx[0] && V5SuffixHit(dos, g_strongSfx)) return TRUE;
    return FALSE;
}

/* ---- NtCreateFile / NtOpenFile：只处理管道路径 ---- */
typedef NTSTATUS (NTAPI *PFN_NtCreateFile)(
    PHANDLE, ACCESS_MASK, V5_OA*, V5_IOSB*, PLARGE_INTEGER,
    ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
static PFN_NtCreateFile p_NtCreateFile;

static NTSTATUS NTAPI Hook_NtCreateFile(
    PHANDLE h, ACCESS_MASK acc, V5_OA* oa, V5_IOSB* iosb, PLARGE_INTEGER alloc,
    ULONG attr, ULONG share, ULONG disp, ULONG opts, PVOID ea, ULONG ealen)
{
    const WCHAR* g_apiName = L"NtCreateFile(pipe)";
    REWRITE(OBJ_PIPE)
        NTSTATUS r = p_NtCreateFile(h, acc, &oa2, iosb, alloc, attr, share, disp, opts, ea, ealen);
        if (r < 0) r = p_NtCreateFile(h, acc, oa, iosb, alloc, attr, share, disp, opts, ea, ealen);
        return r;
    REWRITE_END()

    if (g_strong && share == 0 && oa && oa->ObjectName && oa->ObjectName->Length)
    {
        WCHAR dos[MAX_PATH];

        if (V5NtPathToDos(oa->ObjectName, dos, MAX_PATH) &&
            (V5StrongExplicit(dos) || V5InExeDir(dos)))
        {
            /*
             * 必须"主动"放宽，不能等失败再重试：
             * 共享模式是双向协商的，第一个实例以 share=0 打开后，
             * 第二个实例就算把 share 开到最大也一样被拒。
             */
            NTSTATUS r = p_NtCreateFile(h, acc, oa, iosb, alloc, attr, V5_SHARE_ALL,
                                        disp, opts, ea, ealen);
            if (r >= 0) { V5Log(L"NtCreateFile", dos, L"exclusive-allowed"); return r; }
            /* 放宽反而失败（极少数不允许 SHARE_DELETE 的场景）就退回原参数 */
            return p_NtCreateFile(h, acc, oa, iosb, alloc, attr, share, disp, opts, ea, ealen);
        }
    }

    return p_NtCreateFile(h, acc, oa, iosb, alloc, attr, share, disp, opts, ea, ealen);
}

typedef NTSTATUS (NTAPI *PFN_NtOpenFile)(PHANDLE, ACCESS_MASK, V5_OA*, V5_IOSB*, ULONG, ULONG);
static PFN_NtOpenFile p_NtOpenFile;

static NTSTATUS NTAPI Hook_NtOpenFile(PHANDLE h, ACCESS_MASK acc, V5_OA* oa,
                                      V5_IOSB* iosb, ULONG share, ULONG opts)
{
    const WCHAR* g_apiName = L"NtOpenFile(pipe)";
    REWRITE(OBJ_PIPE)
        NTSTATUS r = p_NtOpenFile(h, acc, &oa2, iosb, share, opts);
        if (r < 0) r = p_NtOpenFile(h, acc, oa, iosb, share, opts);
        return r;
    REWRITE_END()

    if (g_strong && share == 0 && oa && oa->ObjectName && oa->ObjectName->Length)
    {
        WCHAR dos[MAX_PATH];

        if (V5NtPathToDos(oa->ObjectName, dos, MAX_PATH) &&
            (V5StrongExplicit(dos) || V5InExeDir(dos)))
        {
            NTSTATUS r = p_NtOpenFile(h, acc, oa, iosb, V5_SHARE_ALL, opts);
            if (r >= 0) { V5Log(L"NtOpenFile", dos, L"exclusive-allowed"); return r; }
            return p_NtOpenFile(h, acc, oa, iosb, share, opts);
        }
    }

    return p_NtOpenFile(h, acc, oa, iosb, share, opts);
}


/* ==================== 进程枚举过滤（对付"启动器杀进程"） ====================
 *
 * 有些启动器在拉起主程序前，会枚举同名进程并直接 TerminateProcess，这属于"进程级"
 * 的单实例控制 —— 改内核对象名字对它完全无效。直接 hook NtQuerySystemInformation
 * 摘链表节点是可行的，但会让按 ReturnLength 索引的调用方读到不一致的数据（实测启动器会崩）。
 *
 * 这里同样处理，但规则更保守：只隐藏
 *   1) 名字命中环境变量 V5HideNames 名单（; 分隔，支持 * ?）；
 *   2) 不是本进程；
 *   3) 创建时间早于本进程（也就是"之前那些实例"，不影响本次刚拉起的子进程）。
 */
#define V5_MAX_HIDE 8

static WCHAR     g_hide[V5_MAX_HIDE][64];
static int       g_hideCount = 0;

/* 以下偏移是对本机 Win11 24H2 实测得到（x86 用 spi_probe 实测，x64 用 ctypes 实测），
   Windows 各版本之间可能变化，所以下面还有一次"自检"兜底。 */
#ifdef _WIN64
  #define SPI_IMG_LEN 0x38
  #define SPI_IMG_BUF 0x40
  #define SPI_PID     0x50
  #define SPI_PPID    0x58
#else
  #define SPI_IMG_LEN 0x38
  #define SPI_IMG_BUF 0x3C
  #define SPI_PID     0x44
  #define SPI_PPID    0x48
#endif

static BOOL V5NameInHideList(const WCHAR* name)
{
    int i;
    for (i = 0; i < g_hideCount; i++)
        if (V5MatchPattern(name, g_hide[i])) return TRUE;
    return FALSE;
}

/*
 * 防"启动器杀客户端"：在 NtOpenProcess 层拒绝终止权限。
 *
 * 背景：启动器在启动前会枚举进程、按镜像路径找到主程序，然后
 * OpenProcess(PROCESS_TERMINATE) + TerminateProcess 杀掉旧实例。
 *   - 改对象名 → 完全无效（这是进程级操作）
 *   - 改进程列表里的名字 → 也无效（它按路径匹配）
 *   - 摘链表节点 → 有效，但会破坏调用方的索引（实测启动器 0xC0000005）
 * 所以最干净的做法是：它想拿"终止权限"时直接拒绝，让它自己的 KillClient 走
 * killed=false 分支（启动器本来就处理了失败）。
 */
#define STATUS_ACCESS_DENIED ((NTSTATUS)0xC0000022L)

typedef struct _V5_CLIENT_ID {
    HANDLE UniqueProcess;
    HANDLE UniqueThread;
} V5_CLIENT_ID;

typedef NTSTATUS (NTAPI *PFN_NtOpenProcess)(PHANDLE, ACCESS_MASK, V5_OA*, V5_CLIENT_ID*);
static PFN_NtOpenProcess p_NtOpenProcess;

/* 判断某个 pid 的镜像名是否在隐藏名单里 */
static BOOL V5ProcessNameMatches(DWORD pid)
{
    HANDLE h;
    WCHAR path[MAX_PATH];
    DWORD len = MAX_PATH;
    const WCHAR* base = path;
    const WCHAR* p;
    BOOL hit = FALSE;

    if (pid == 0 || pid == GetCurrentProcessId()) return FALSE;

    h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return FALSE;

    if (QueryFullProcessImageNameW(h, 0, path, &len))
    {
        for (p = path; *p; p++) if (*p == L'\\' || *p == L'/') base = p + 1;
        hit = V5NameInHideList(base);
    }
    CloseHandle(h);
    return hit;
}

static NTSTATUS NTAPI Hook_NtOpenProcess(PHANDLE handle, ACCESS_MASK access,
                                         V5_OA* oa, V5_CLIENT_ID* cid)
{
    /* 只关心"要终止权限"且目标不是自己的情况 */
    if (cid && cid->UniqueProcess && (access & PROCESS_TERMINATE) &&
        (DWORD)(ULONG_PTR)cid->UniqueProcess != GetCurrentProcessId())
    {
        static DWORD tlsGuard = TLS_OUT_OF_INDEXES;
        BOOL nested = FALSE;

        if (tlsGuard == TLS_OUT_OF_INDEXES) tlsGuard = TlsAlloc();
        if (tlsGuard != TLS_OUT_OF_INDEXES)
        {
            nested = TlsGetValue(tlsGuard) != NULL;
            if (!nested) TlsSetValue(tlsGuard, (LPVOID)1);
        }

        if (!nested && V5ProcessNameMatches((DWORD)(ULONG_PTR)cid->UniqueProcess))
        {
            if (tlsGuard != TLS_OUT_OF_INDEXES) TlsSetValue(tlsGuard, NULL);
            V5Log(L"NtOpenProcess", L"deny", L"terminate");
            return STATUS_ACCESS_DENIED;
        }

        if (tlsGuard != TLS_OUT_OF_INDEXES) TlsSetValue(tlsGuard, NULL);
    }

    return p_NtOpenProcess(handle, access, oa, cid);
}

/* ==================== 全局隐藏：从进程枚举里摘掉其他实例 ====================
 *
 * "保护进程"只是在 NtOpenProcess 上拒绝终止权限 —— 能挡住"杀掉别人"，
 * 但挡不住"看到别人就自己退出"这类检测。要让对方**完全看不见**别的实例，
 * 就得让进程枚举里根本没有这些节点。
 *
 * 原版 V5 用的也是这个思路（摘 SYSTEM_PROCESS_INFORMATION 链表），
 * 但之前的实现有两处错误导致目标崩溃：
 *   1. 结构偏移是猜的（实测后已修正为下表数值）；
 *   2. 把前驱的 NextEntryOffset 直接**覆盖**成当前项的偏移 —— 跳转距离错了，
 *      链表会跳到错误位置。正确做法是"累加"两段距离。
 *
 * 这里同时做了三重保险：
 *   - 首次调用做结构自检（找到 pid == 自己那一项并比对镜像名），偏移不对就整个功能停用；
 *   - 每个指针/长度都做边界校验；
 *   - 命中项若是首项（一般是 Idle/System）就不摘，只跳过，避免没有前驱。
 */
static volatile LONG g_globalHide = 0;
static int           g_hideState = 0;   /* 0=未自检 1=可用 -1=已停用 */

typedef NTSTATUS (NTAPI *PFN_NtQuerySystemInformation)(ULONG, PVOID, ULONG, PULONG);
static PFN_NtQuerySystemInformation p_NtQuerySystemInformation;

static BOOL V5PtrInBuf(const BYTE* buf, ULONG bufLen, const void* p, ULONG need)
{
    const BYTE* q = (const BYTE*)p;
    return q >= buf && q + need >= q && (ULONG)(q - buf) + need <= bufLen;
}

/* 自检：确认偏移量在本机是对的，对不上就宁可不隐藏 */
static BOOL V5SelfCheck(BYTE* buf, ULONG bufLen)
{
    BYTE* p = buf;
    int n = 0;

    for (;;)
    {
        ULONG next;
        if (!V5PtrInBuf(buf, bufLen, p, SPI_PID + sizeof(ULONG_PTR))) return FALSE;
        next = *(ULONG*)p;

        if ((DWORD)*(ULONG_PTR*)(p + SPI_PID) == GetCurrentProcessId())
        {
            USHORT len = *(USHORT*)(p + SPI_IMG_LEN);
            const WCHAR* nm = *(const WCHAR**)(p + SPI_IMG_BUF);
            if (!nm || len < 4 || len > 520) return FALSE;
            if (!V5PtrInBuf(buf, bufLen, nm, len + sizeof(WCHAR))) return FALSE;
            return V5MatchPattern(nm, g_exeName) != FALSE;
        }
        if (next == 0) break;
        if (next >= bufLen || (ULONG)(p - buf) + next >= bufLen) break;
        p += next;
        if (++n > 8192) break;
    }
    return FALSE;
}

/* 把命中项从链表里摘掉 */
static void V5HideFromList(BYTE* buf, ULONG bufLen)
{
    BYTE* prev = NULL;
    BYTE* cur = buf;
    DWORD self = GetCurrentProcessId();
    int guard = 0;

    if (g_hideState < 0) return;

    if (g_hideState == 0)
    {
        if (!V5SelfCheck(buf, bufLen))
        {
            V5Log(L"[nqsi]", L"selfcheck", L"fail - 结构偏移不匹配，进程隐藏已停用");
            g_hideState = -1;
            return;
        }
        g_hideState = 1;
    }

    for (;;)
    {
        ULONG next;
        USHORT len;
        const WCHAR* nm;
        DWORD pid, ppid;
        BOOL hide = FALSE;

        if (!V5PtrInBuf(buf, bufLen, cur, SPI_PPID + sizeof(ULONG_PTR))) break;

        next = *(ULONG*)cur;
        len  = *(USHORT*)(cur + SPI_IMG_LEN);
        nm   = *(const WCHAR**)(cur + SPI_IMG_BUF);
        pid  = (DWORD)*(ULONG_PTR*)(cur + SPI_PID);
        ppid = (DWORD)*(ULONG_PTR*)(cur + SPI_PPID);

        /* 只隐藏"不是自己、也不是本次拉起的子进程"的同名进程 */
        if (pid != self && ppid != self && nm && len > 2 && len < 520 &&
            V5PtrInBuf(buf, bufLen, nm, len + sizeof(WCHAR)) &&
            V5NameInHideList(nm))
            hide = TRUE;

        if (hide)
        {
            if (prev)
            {
                /*
                 * 关键：偏移量要"累加"两段距离，而不是直接覆盖。
                 *   前驱 -> 当前项 距离 = cur - prev
                 *   当前项 -> 下一项 距离 = next
                 * 覆盖成 next 会让链表跳到错误位置（这正是之前崩溃的原因）。
                 */
                if (next == 0) *(ULONG*)prev = 0;                     /* 摘掉的是最后一项 */
                else          *(ULONG*)prev = (ULONG)(cur - prev) + next;
            }
            /* prev == NULL：命中的是首项（Idle/System），没有前驱可改，放弃摘除 */
        }
        else
        {
            prev = cur;
        }

        if (next == 0) break;
        if (next >= bufLen || (ULONG)(cur - buf) + next >= bufLen) break;  /* 链表异常，停手 */
        cur += next;
        if (++guard > 8192) break;
    }
}

static NTSTATUS NTAPI Hook_NtQuerySystemInformation(ULONG cls, PVOID buf, ULONG len, PULONG ret)
{
    NTSTATUS st = p_NtQuerySystemInformation(cls, buf, len, ret);

    if (g_globalHide && cls == 5 /* SystemProcessInformation */ && st >= 0 &&
        buf && len > 0x100 && g_hideCount > 0)
        V5HideFromList((BYTE*)buf, len);

    return st;
}

/* ==================== 注册表隔离 ====================
 *
 * 某些程序把"是否已有实例"记在注册表里（例如 HKCU\Software\厂商\游戏\Instance）。
 * 这类检测既不靠命名对象，也不靠进程列表，只有把注册表键也隔离开才能对付。
 *
 * 做法与命名对象一致：在命中"关键词"的那一段路径后面插入 "Group_<组号>"，
 * 于是每个实例拥有一棵独立的配置子树，互不干扰。
 *
 * 只处理绝对路径（RootDirectory == NULL）：程序用相对路径打开子键时，
 * 父键已经被改名，子键自然落在隔离后的树里，不需要再动。
 *
 * 关键词由环境变量 V5RegFilter 指定（; 分隔），不设置就完全不碰注册表 ——
 * 这是必须的保守设计：改错一个系统键，程序会直接崩。
 */
#define V5_MAX_REGFILTER 8

static WCHAR g_regFilter[V5_MAX_REGFILTER][128];
static int   g_regFilterCount = 0;

/* 大小写不敏感的子串查找，返回起始下标，未找到返回 -1 */
static int V5FindSubI(const WCHAR* s, int sLen, const WCHAR* sub, int subLen)
{
    int i, j;

    if (subLen <= 0 || sLen < subLen) return -1;
    for (i = 0; i + subLen <= sLen; i++)
    {
        for (j = 0; j < subLen; j++)
        {
            WCHAR a = s[i + j];
            WCHAR b = sub[j];
            if (a >= L'A' && a <= L'Z') a = (WCHAR)(a + 32);
            if (b >= L'A' && b <= L'Z') b = (WCHAR)(b + 32);
            if (a != b) break;
        }
        if (j == subLen) return i;
    }
    return -1;
}

/* 把注册表路径里命中关键词的部分加上组号后缀 */
static BOOL V5RegRewrite(const V5_US* name, WCHAR* out, SIZE_T cch)
{
    const WCHAR* s;
    int len, i, j, idx;
    int hitStart = -1, hitLen = 0;

    if (!name || !name->Buffer) return FALSE;
    len = (int)(name->Length / sizeof(WCHAR));
    if (len < 8 || len >= (int)cch - 64) return FALSE;
    s = name->Buffer;

    for (i = 0; i < g_regFilterCount; i++)
    {
        int klen = (int)lstrlenW(g_regFilter[i]);
        int p;
        if (klen <= 0) continue;
        p = V5FindSubI(s, len, g_regFilter[i], klen);
        if (p >= 0) { hitStart = p; hitLen = klen; break; }
    }
    if (hitStart < 0) return FALSE;

    /* 已经带过后缀就别再加 */
    {
        int after = hitStart + hitLen;
        int sl = (int)lstrlenW(g_suffix);
        if (after + sl <= len)
        {
            int k, same = 1;
            for (k = 0; k < sl; k++)
                if (s[after + k] != g_suffix[k]) { same = 0; break; }
            if (same) return FALSE;
        }
    }

    /* 结果 = 前缀 + 关键词本身 + 组号后缀 + 剩余部分（是"追加"，不是"替换"） */
    idx = 0;
    for (j = 0; j < hitStart; j++)               out[idx++] = s[j];
    for (j = 0; j < hitLen; j++)                out[idx++] = s[hitStart + j];
    for (j = 0; g_suffix[j]; j++)               out[idx++] = g_suffix[j];
    for (j = hitStart + hitLen; j < len; j++)    out[idx++] = s[j];
    out[idx] = L'\0';
    return TRUE;
}

/*
 * 组装改写后的 OBJECT_ATTRIBUTES。
 *
 * 注意不能只处理 RootDirectory == NULL 的绝对路径：注册表 API 大量使用
 * "根句柄 + 相对路径" 的形式（advapi32 会把 HKEY_CURRENT_USER 当根句柄，
 * ObjectName 传 "Software\厂商\游戏"）。所以这里一律按 ObjectName 的
 * 内容匹配关键词，绝对路径和相对路径都能命中。
 *
 * 关键词按"路径片段"匹配（大小写不敏感的子串），例如填 V5RegTest 或
 * NetEase 即可，不必写完整的 \Registry\User\... 前缀。
 */
static BOOL V5MakeRegOA(const V5_OA* oa, WCHAR* buf, SIZE_T cch,
                        V5_OA* outOA, V5_US* outUS)
{
    if (!oa) return FALSE;
    if (!oa->ObjectName || !oa->ObjectName->Buffer) return FALSE;
    if (!V5RegRewrite(oa->ObjectName, buf, cch)) return FALSE;

    *outOA = *oa;
    outUS->Length        = (USHORT)(lstrlenW(buf) * sizeof(WCHAR));
    outUS->MaximumLength = (USHORT)(outUS->Length + sizeof(WCHAR));
    outUS->Buffer        = buf;
    outOA->ObjectName    = outUS;
    return TRUE;
}

typedef NTSTATUS (NTAPI *PFN_NtCreateKey)(PHANDLE, ACCESS_MASK, V5_OA*, ULONG, V5_US*, ULONG, PULONG);
static PFN_NtCreateKey p_NtCreateKey;

typedef NTSTATUS (NTAPI *PFN_NtOpenKey)(PHANDLE, ACCESS_MASK, V5_OA*);
static PFN_NtOpenKey p_NtOpenKey;

typedef NTSTATUS (NTAPI *PFN_NtOpenKeyEx)(PHANDLE, ACCESS_MASK, V5_OA*, ULONG);
static PFN_NtOpenKeyEx p_NtOpenKeyEx;

static NTSTATUS NTAPI Hook_NtCreateKey(PHANDLE h, ACCESS_MASK acc, V5_OA* oa,
                                       ULONG titleIdx, V5_US* cls, ULONG opts, PULONG disp)
{
    WCHAR buf[1024];
    V5_OA oa2;
    V5_US us;

    if (g_regFilterCount > 0 && V5MakeRegOA(oa, buf, 1024, &oa2, &us))
    {
        V5Log(L"NtCreateKey", L"(registry)", buf);
        return p_NtCreateKey(h, acc, &oa2, titleIdx, cls, opts, disp);
    }
    return p_NtCreateKey(h, acc, oa, titleIdx, cls, opts, disp);
}

static NTSTATUS NTAPI Hook_NtOpenKey(PHANDLE h, ACCESS_MASK acc, V5_OA* oa)
{
    WCHAR buf[1024];
    V5_OA oa2;
    V5_US us;

    if (g_regFilterCount > 0 && V5MakeRegOA(oa, buf, 1024, &oa2, &us))
    {
        V5Log(L"NtOpenKey", L"(registry)", buf);
        return p_NtOpenKey(h, acc, &oa2);
    }
    return p_NtOpenKey(h, acc, oa);
}

static NTSTATUS NTAPI Hook_NtOpenKeyEx(PHANDLE h, ACCESS_MASK acc, V5_OA* oa, ULONG openOpts)
{
    WCHAR buf[1024];
    V5_OA oa2;
    V5_US us;

    if (g_regFilterCount > 0 && V5MakeRegOA(oa, buf, 1024, &oa2, &us))
    {
        V5Log(L"NtOpenKeyEx", L"(registry)", buf);
        return p_NtOpenKeyEx(h, acc, &oa2, openOpts);
    }
    return p_NtOpenKeyEx(h, acc, oa, openOpts);
}

static void V5InitHideList(void)
{
    WCHAR tmp[512];
    DWORD n;
    int idx = 0;
    WCHAR* p;


    n = GetEnvironmentVariableW(L"V5HideNames", tmp, 512);
    if (n == 0 || n >= 512) return;

    p = tmp;
    while (*p && idx < V5_MAX_HIDE)
    {
        WCHAR* s = p;
        int len, i;
        while (*p && *p != L';') p++;
        len = (int)(p - s);
        if (len > 0 && len < 64)
        {
            /* volatile 拷贝：避免编译器把它优化成 memcpy 调用（本模块不链接 CRT） */
            {
                volatile WCHAR* d = g_hide[idx];
                for (i = 0; i < len; i++) d[i] = s[i];
            }
            g_hide[idx][len] = 0;
            idx++;
        }
        if (*p == L';') p++;
    }
    g_hideCount = idx;
}

/* ============================ 安装 ============================ */


static void V5InstallOne(const char* api, void** pfn, void* hookFn)
{
    HMODULE nt = GetModuleHandleW(L"ntdll.dll");
    void* real;
    void* tramp;
    WCHAR wideApi[64];
    int wi = 0;

    if (!nt) return;
    /* API 名是 ASCII，转成宽字符再交给日志函数（否则会被当成 UTF-16 读成乱码） */
    while (api[wi] && wi < 63) { wideApi[wi] = (WCHAR)(BYTE)api[wi]; wi++; }
    wideApi[wi] = 0;
    real = (void*)GetProcAddress(nt, api);
    if (!real) { V5Log(L"[hook]", wideApi, L"not-exported"); return; }
    tramp = V5HookInstall(&real, hookFn);
    *pfn = tramp ? tramp : real;      /* 失败则保持原始地址（该 API 不生效但不崩） */
    /* 记录安装结果：被安全软件做过热补丁的 API 会在这里露出来 */
    V5Log(L"[hook]", wideApi, tramp ? L"installed" : L"SKIPPED");
}

static void V5InstallHooks(void)
{
    V5InstallOne("NtCreateMutant",        (void**)&p_NtCreateMutant,        (void*)Hook_NtCreateMutant);
    V5InstallOne("NtOpenMutant",          (void**)&p_NtOpenMutant,          (void*)Hook_NtOpenMutant);
    V5InstallOne("NtCreateEvent",         (void**)&p_NtCreateEvent,         (void*)Hook_NtCreateEvent);
    V5InstallOne("NtOpenEvent",           (void**)&p_NtOpenEvent,           (void*)Hook_NtOpenEvent);
    V5InstallOne("NtCreateSemaphore",     (void**)&p_NtCreateSemaphore,     (void*)Hook_NtCreateSemaphore);
    V5InstallOne("NtOpenSemaphore",       (void**)&p_NtOpenSemaphore,       (void*)Hook_NtOpenSemaphore);
    V5InstallOne("NtCreateSection",       (void**)&p_NtCreateSection,       (void*)Hook_NtCreateSection);
    V5InstallOne("NtOpenSection",         (void**)&p_NtOpenSection,         (void*)Hook_NtOpenSection);
    V5InstallOne("NtCreateTimer",         (void**)&p_NtCreateTimer,         (void*)Hook_NtCreateTimer);
    V5InstallOne("NtOpenTimer",           (void**)&p_NtOpenTimer,           (void*)Hook_NtOpenTimer);
    V5InstallOne("NtCreateNamedPipeFile", (void**)&p_NtCreateNamedPipeFile, (void*)Hook_NtCreateNamedPipeFile);
    V5InstallOne("NtCreateFile",          (void**)&p_NtCreateFile,          (void*)Hook_NtCreateFile);
    V5InstallOne("NtOpenFile",            (void**)&p_NtOpenFile,            (void*)Hook_NtOpenFile);
    V5InstallOne("NtOpenProcess",         (void**)&p_NtOpenProcess,         (void*)Hook_NtOpenProcess);
    V5InstallOne("NtQuerySystemInformation", (void**)&p_NtQuerySystemInformation, (void*)Hook_NtQuerySystemInformation);
    V5InstallOne("NtCreateKey",           (void**)&p_NtCreateKey,           (void*)Hook_NtCreateKey);
    V5InstallOne("NtOpenKey",             (void**)&p_NtOpenKey,             (void*)Hook_NtOpenKey);
    V5InstallOne("NtOpenKeyEx",           (void**)&p_NtOpenKeyEx,           (void*)Hook_NtOpenKeyEx);
}

static DWORD V5ParseUInt(const WCHAR* s)
{
    DWORD v = 0;
    while (*s >= L'0' && *s <= L'9') { v = v * 10 + (DWORD)(*s - L'0'); s++; }
    return v;
}


static void V5Init(void)
{
    WCHAR tmp[64];
    DWORD n;

    InitializeCriticalSection(&g_cs);
    g_tlsBusy = TlsAlloc();

    /* 组号：优先生效环境变量；没有就退化成 PID（每个进程独立） */
    n = GetEnvironmentVariableW(L"ProcessGroup", tmp, 64);
    g_group = (n > 0 && n < 64) ? V5ParseUInt(tmp) : 0;
    if (g_group == 0) g_group = GetCurrentProcessId();
    wsprintfW(g_suffix, L"Group_%u", g_group);

    GetModuleFileNameW(NULL, g_exeFull, MAX_PATH);
    {   /* 目录（带结尾反斜杠）+ 纯文件名 */
        WCHAR* p = g_exeFull;
        WCHAR* base = g_exeFull;
        WCHAR* slash = NULL;
        for (; *p; p++) if (*p == L'\\' || *p == L'/') { base = p + 1; slash = p; }
        if (slash)
        {
            int n = (int)(slash - g_exeFull) + 1;
            lstrcpynW(g_exeDir, g_exeFull, n + 1);
        }
        lstrcpyW(g_exeName, base);
    }


    /* 加强模式（默认关闭，由宿主按需下发） */
    n = GetEnvironmentVariableW(L"V5Strong", tmp, 64);
    g_strong = (n > 0 && tmp[0] == L'1') ? 1 : 0;
    if (g_strong)
    {
        static const WCHAR kDefaultSfx[] = L"*.lock;*.pid;*.mutex";
        n = GetEnvironmentVariableW(L"V5StrongSuffixes", g_strongSfx, 260);
        if (n == 0 || n >= 260)
        {
            int i;
            for (i = 0; kDefaultSfx[i]; i++) g_strongSfx[i] = kDefaultSfx[i];
            g_strongSfx[i] = 0;
        }
    }


    /* 全局隐藏（进程枚举里摘掉其他实例） */
    n = GetEnvironmentVariableW(L"V5GlobalHide", tmp, 64);
    g_globalHide = (n > 0 && tmp[0] == L'1') ? 1 : 0;

    /* 注册表隔离关键词（; 分隔，留空 = 完全不碰注册表） */
    {
        WCHAR regBuf[1024];
        DWORD rn = GetEnvironmentVariableW(L"V5RegFilter", regBuf, 1024);
        if (rn > 0 && rn < 1024)
        {
            WCHAR* p = regBuf;
            while (*p && g_regFilterCount < V5_MAX_REGFILTER)
            {
                WCHAR* seg = p;
                int segLen, i;
                while (*p && *p != L';') p++;
                segLen = (int)(p - seg);
                if (segLen > 0 && segLen < 128)
                {
                    volatile WCHAR* d = g_regFilter[g_regFilterCount];
                    for (i = 0; i < segLen; i++) d[i] = seg[i];
                    g_regFilter[g_regFilterCount][segLen] = 0;
                    g_regFilterCount++;
                }
                if (*p == L';') p++;
            }
        }
    }

    n = GetEnvironmentVariableW(L"V5SharpLog", g_logPath, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) g_logPath[0] = 0;

    g_logOn = (g_logPath[0] != 0) ? 1 : 0;      /* 提前开日志，好记录 hook 安装结果 */
    V5InitHideList();
    V5InstallHooks();

}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinst);
        V5Init();
    } else if (reason == DLL_PROCESS_DETACH) {
        V5LogFlush();
        if (g_log != INVALID_HANDLE_VALUE) { CloseHandle(g_log); g_log = INVALID_HANDLE_VALUE; }
    }
    return TRUE;
}
