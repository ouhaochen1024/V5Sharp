/*
 * hook.c - 极简 inline hook 引擎
 *
 * 与原始 v5_hook.dll 使用 mHook + 反汇编引擎的做法不同，这里直接利用
 * "Windows 用户态系统调用桩(SSN stub)" 的固定结构：
 *
 *   x64:  4C 8B D1              mov r10, rcx
 *         B8 <ssn:4>            mov eax, ssn
 *         F6 04 25 08 03 FE 7F 01   test byte ptr [7FFE0308h], 1
 *         75 03                 jne +3
 *         0F 05                 syscall
 *         C3                    ret
 *
 *   x86:  B8 <ssn:4>            mov eax, ssn
 *         BA <KiFastSystemCall> mov edx, 7FFE0300h 形式
 *         FF D2                 call edx
 *         C2 10 00              ret 10h
 *
 * 这些桩内部没有任何 RIP 相对寻址，因此可以整段复制到一块可执行内存里当作
 * "原始函数" 调用（trampoline），入口处再用一条绝对跳转覆盖即可。
 * 这种做法不需要长度反汇编器，天然支持 x86 / x64。
 */
#include "v5hook.h"

#ifdef _WIN64
#define PATCH_SIZE 12
#else
#define PATCH_SIZE 5
#endif

BOOL V5LooksLikeSyscallStub(const BYTE* c)
{
#ifdef _WIN64
    return c[0] == 0x4C && c[1] == 0x8B && c[2] == 0xD1 && c[3] == 0xB8;
#else
    return c[0] == 0xB8;
#endif
}

/* 返回桩的长度（含结尾的 ret），非标准桩返回 0 */
static SIZE_T V5StubLength(const BYTE* c)
{
#ifdef _WIN64
    /* 标准序列：... 75 03 0F 05 C3 */
    if (c[0] == 0x4C && c[1] == 0x8B && c[2] == 0xD1 && c[3] == 0xB8 &&
        c[8] == 0xF6 && c[9] == 0x04 && c[10] == 0x25 &&
        c[16] == 0x75 && c[17] == 0x03 && c[18] == 0x0F && c[19] == 0x05 &&
        c[20] == 0xC3)
        return 21;
#else
    /* B8 ssn:4 | BA imm32 | FF D2 | C2 xx xx */
    if (c[0] == 0xB8 && c[5] == 0xBA && c[10] == 0xFF && c[11] == 0xD2 && c[12] == 0xC2)
        return 15;
#endif
    return 0;
}

/* trampoline 从同一个可执行页里顺序分配，避免每个 hook 单独占用一页 */
static BYTE* g_codePage = NULL;
static SIZE_T g_codeUsed = 0;

static void* V5AllocCode(SIZE_T size)
{
    void* p;
    size = (size + 15) & ~(SIZE_T)15;
    if (!g_codePage || g_codeUsed + size > 0x1000)
    {
        g_codePage = (BYTE*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE,
                                         PAGE_EXECUTE_READWRITE);
        g_codeUsed = 0;
        if (!g_codePage) return NULL;
    }
    p = g_codePage + g_codeUsed;
    g_codeUsed += size;
    return p;
}

static void v5_copy(void* dst, const void* src, SIZE_T n)
{
    BYTE* d = (BYTE*)dst; const BYTE* s = (const BYTE*)src;
    while (n--) *d++ = *s++;
}

void* V5HookInstall(void** ppTarget, void* detour)
{
    BYTE* target = (BYTE*)(ppTarget ? *ppTarget : NULL);
    SIZE_T len;
    BYTE* tramp;
    DWORD oldProtect;

    if (!target || !detour) return NULL;
    if (!V5LooksLikeSyscallStub(target)) return NULL;   /* 已被其它产品做过热补丁/转发，跳过 */

    len = V5StubLength(target);
    if (len < PATCH_SIZE) return NULL;

    tramp = (BYTE*)V5AllocCode(len);
    if (!tramp) return NULL;
    v5_copy(tramp, target, len);         /* 整段复制，末尾自带 ret */

    if (!VirtualProtect(target, PATCH_SIZE, PAGE_EXECUTE_READWRITE, &oldProtect))
        return NULL;

#ifdef _WIN64
    target[0] = 0x48; target[1] = 0xB8;                       /* mov rax, imm64 */
    *(UINT64*)(target + 2) = (UINT64)detour;
    target[10] = 0xFF; target[11] = 0xE0;                     /* jmp rax */
#else
    target[0] = 0xE9;                                         /* jmp rel32 */
    *(INT32*)(target + 1) = (INT32)((BYTE*)detour - (target + 5));
#endif

    VirtualProtect(target, PATCH_SIZE, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), target, PATCH_SIZE);
    return tramp;
}
