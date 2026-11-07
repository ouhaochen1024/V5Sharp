#pragma once
/*
 * v5hook.h - V5Sharp 注入模块公共定义
 *
 * 设计要点（与原始 v5_hook.dll 等价，但去掉 CRT 依赖、同时支持 x86/x64）：
 *   1. 通过环境变量 "ProcessGroup" 取得本次多开实例的组号；
 *   2. 在 ntdll 的系统调用桩(SSN stub)上做 inline hook；
 *   3. 命名的内核对象（Mutant/Event/Semaphore/Section/Timer/管道）
 *      名字尾部追加 "Group_<组号>"，从而让每个实例拥有独立的命名空间；
 *   4. 系统关键对象（RPC 端点、输入法共享内存、会话事件等）走白名单，不改名。
 */
#ifndef V5HOOK_H
#define V5HOOK_H

#include <windows.h>

#ifndef NTAPI
#define NTAPI __stdcall
#endif
#ifndef STATUS_SUCCESS
#define STATUS_SUCCESS ((NTSTATUS)0x00000000L)
#endif

/* ---- 自行声明，避免引入 winternl.h 与 SDK 版本耦合 ---- */
typedef struct _V5_US {
    USHORT Length;
    USHORT MaximumLength;
    PWSTR  Buffer;
} V5_US;

typedef struct _V5_OA {
    ULONG   Length;
    HANDLE  RootDirectory;
    V5_US*  ObjectName;
    ULONG   Attributes;
    PVOID   SecurityDescriptor;
    PVOID   SecurityQualityOfService;
} V5_OA;

typedef struct _V5_IOSB {
    NTSTATUS Status;
    ULONG_PTR Information;
} V5_IOSB;

/* ================= 各种对象类型，用于选择白名单 ================= */
typedef enum {
    OBJ_MUTANT = 0,
    OBJ_EVENT,
    OBJ_SEMAPHORE,
    OBJ_SECTION,
    OBJ_TIMER,
    OBJ_PIPE,
    OBJ_TYPE_MAX
} V5_OBJTYPE;

/* ================= 日志 ================= */
void V5Log(const WCHAR* api, const WCHAR* oldName, const WCHAR* newName);
void V5LogFlush(void);
void V5LogInit(void);
BOOL V5LogEnabled(void);

/* ================= 名称改写 ================= */
/*
 * 尝试改写 ObjectName。
 * 返回 TRUE 时 outOA 指向改写后的 OBJECT_ATTRIBUTES（Buffer 在 buf 中），
 * 调用方把 outOA 传给原始函数即可。
 */
BOOL V5RewriteObject(const V5_OA* oa, V5_OBJTYPE type,
                     WCHAR* buf, SIZE_T cchBuf,
                     V5_OA* outOA, V5_US* outUS);

/* 白名单匹配（通配符 * ?，大小写不敏感） */
BOOL V5IsSkippedName(const WCHAR* name, V5_OBJTYPE type);

/* 通配符匹配（* ?，大小写不敏感） */
BOOL V5MatchPattern(const WCHAR* s, const WCHAR* pattern);

/* 判断是否为命名管道路径：\??\pipe\x  \\.\pipe\x  \Device\NamedPipe\x */
BOOL V5IsPipePath(const WCHAR* name);

/* ================= hook 引擎 ================= */
/* 安装 hook，返回 trampoline（即"原始函数"的可调用地址），失败返回 NULL */
void* V5HookInstall(void** ppTarget, void* detour);
BOOL  V5LooksLikeSyscallStub(const BYTE* code);

#endif /* V5HOOK_H */
