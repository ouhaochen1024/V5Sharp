# V5Sharp —— Windows 通用多开器（32 / 64 位）

一个用 C# 写的 Windows 多开工具。界面的交互习惯参考了早期的 V5 多开器，
注入引擎与界面都是从零实现的：给每个实例一套独立的内核对象命名空间，
让"只允许一个实例"的程序可以同时跑多个。

- 单文件宿主（`V5多开器Pro.exe`），内嵌字体，拷走即用，无需安装运行时
- 支持 64 位与 32 位目标，自动识别、自动选择注入路线
- 覆盖五类常见的单实例限制：命名对象、进程级"杀掉旧实例"、文件锁、
  注册表标记、进程枚举
- 附带命令行版本，便于脚本化与自测

---

## 工作原理

Windows 上大多数"只能开一个"的程序，靠的是**同名的内核对象**：互斥体、事件、
信号量、内存节、命名管道……第二个实例创建同名对象时拿到 `STATUS_OBJECT_NAME_EXISTS`，
于是认为"已经有一个我在运行"。

本工具在目标进程启动的瞬间注入一个小模块，把 `ntdll` 里创建/打开这些对象的函数
全部 hook 住，在真正进入内核之前，把对象名后面加上组号后缀：

```
实例 A: CreateMutex("App_SingleInstance") → CreateMutex("App_SingleInstanceGroup_100001")
实例 B: CreateMutex("App_SingleInstance") → CreateMutex("App_SingleInstanceGroup_100002")
        两个实例都得到"创建成功、且不冲突"的结果
```

组号通过环境变量 `ProcessGroup` 传给目标进程，子进程默认继承，因此
"启动器 → 主程序 → 它的子进程"整条链共用同一个命名空间，不需要额外配置。

被 hook 的函数（同一份源码同时编出 x86 / x64 两个 payload）：

```
NtCreateMutant / NtOpenMutant          NtCreateEvent  / NtOpenEvent
NtCreateSemaphore / NtOpenSemaphore    NtCreateSection / NtOpenSection
NtCreateTimer / NtOpenTimer            NtCreateNamedPipeFile
NtCreateFile / NtOpenFile              NtCreateKey   / NtOpenKey / NtOpenKeyEx
NtOpenProcess                          NtQuerySystemInformation
```

命名对象之外的那几个，用在后面几节说的"文件锁 / 注册表 / 进程级限制"上，默认不介入。

---

## 目录结构

```
V5Sharp/
├─ src/V5Sharp.Core/        注入引擎（纯 C#）
│   ├─ Launcher.cs          创建进程 + 选 payload + 注入 + 恢复线程
│   ├─ Injector.cs          远程 LoadLibrary（x64 直接注入 / x86 换算 32 位地址）
│   ├─ PeExports.cs         读 SysWOW64\kernel32.dll 的 LoadLibraryW RVA
│   ├─ PeInfo.cs            判断目标 exe 的位数
│   ├─ InstanceManager.cs   组号分配、批量启动、状态刷新、批量结束
│   ├─ ProcessLink.cs       Job 对象：父进程退出时带走整棵进程树
│   ├─ AppConfig.cs         ini 配置读写
│   └─ Win32.cs             P/Invoke 声明
├─ src/V5Sharp.App/         WinForms 宿主（自绘的浅色界面）
│   ├─ MainForm.cs          主窗口
│   ├─ Glass/               自绘控件、主题、字体加载
│   └─ Fonts/               内嵌的 MiSans 三个字重
├─ tools/V5Sharp.Cli/       命令行入口
├─ native/V5Hook/           注入模块（x86/x64 同一份源码，无 CRT 依赖）
│   ├─ v5hook.c             各 API 的 hook、名字改写、hook 日志
│   ├─ hook.c               系统调用桩 inline hook（不依赖反汇编引擎）
│   └─ filter.c             系统对象白名单（通配符匹配）
├─ native/V5Inject32/       32 位目标专用注入辅助进程
├─ build/build-native.ps1   用 MSVC 构建 payload 与辅助进程
├─ tests/                   测试目标与对照实验（见文末）
└─ dist/                    构建产物
```

---

## 构建

需要 .NET 8 SDK，以及 Visual Studio Build Tools（C++ 生成工具，用来编译原生 payload）。

```powershell
cd V5Sharp

# 1) 先编原生 payload → dist\payload\V5Hook32.dll / V5Hook64.dll / V5Inject32.exe
powershell -ExecutionPolicy Bypass -File build\build-native.ps1

# 2) 再编 C# 宿主（会把 payload 复制到输出目录）
dotnet build src\V5Sharp.App -c Release
# 产物：dist\app\V5多开器Pro.exe
```

命令行版本：

```powershell
dotnet build tools\V5Sharp.Cli -c Release
tools\V5Sharp.Cli\bin\Release\net8.0-windows\V5Sharp.Cli.exe launch --exe "D:\App\App.exe" --count 3
```

---

## 使用

1. 运行 `dist\app\V5多开器Pro.exe`。目标程序需要管理员权限时，本工具也要以管理员身份运行；
2. 选择或拖入目标 exe，确认"工作目录"，设置实例数，点"启动全部"；
3. 列表里能看到每个实例的 PID / 组号 / 状态，可以"追加一个"或"结束全部"；
4. 勾选"记录改名日志"时，每个实例的改名记录写到 `Log\instance_<组号>.log`。

配置保存在 exe 同目录的 `v5sharp.ini`：

```ini
[V5Sharp]
ExePath=                      ; 目标程序
Arguments=                    ; 启动参数
WorkDir=                      ; 工作目录，留空用 exe 所在目录
HideNames=                    ; 受保护进程名，多个用分号分隔
Count=2                       ; 实例数
WriteHookLog=1                ; 是否记录改名日志
StrongMode=0                  ; 加强模式（放宽文件独占）
StrongSuffixes=               ; 加强模式追加的文件后缀，留空用默认 *.lock;*.pid;*.mutex
GlobalHide=0                  ; 全局隐藏
RegFilter=                    ; 注册表隔离关键词，多个用分号分隔
```

### 工作目录很关键

很多程序的可执行文件放在 `bin\` 子目录里，但资源是按**程序根目录**的相对路径找的。
工作目录填错，进程通常会启动后秒退（工具在几秒后回访时会在日志里提示）。

选 exe 时本工具会自动判断：若 exe 在 `bin\` 下，且上级目录存在 `res` / `resources` / `data`
这类资源目录，或者同级有 `launch` / `start` / `client` 之类的可执行文件，
就把工作目录自动设为上级目录，否则用 exe 所在目录。

---

## 五类单实例限制与对策

不同程序卡住第二个实例的方式不一样，工具里对应五个开关。先看清楚对方用哪一种，再开对应的那个。

### ① 命名内核对象 —— 默认开启，无需设置

就是"工作原理"那节说的：改名即可，没有任何副作用。
绝大多数程序（Electron、Qt、.NET、Unity 打包的都算）都属于这一类。

### ② 启动器"先杀掉旧实例"

有些程序拆分成了两层：一个启动器负责版本检查/更新，然后杀掉已有主程序再拉起新的。
这类限制是**进程级**的，改对象名完全无效——必须拦住"杀进程"这个动作。

| 开关 | 拦在哪 | 对方看到什么 | 适合 |
|---|---|---|---|
| **受保护进程** | `NtOpenProcess`：对方索要 `PROCESS_TERMINATE` 权限时直接返回拒绝 | 能看到别人，但杀不掉 | 启动器"先杀旧实例再启动" |
| **全局隐藏** | `NtQuerySystemInformation`：把命中名单的进程从列表里摘掉 | 根本看不到别人 | 程序"看到已有实例就自己退出" |

两个可以同时开，互不冲突。

- 受保护进程默认就够用，副作用最小（只影响那一次 open）；
- 全局隐藏会改进程列表的链表结构，代码里做了三重保险：首次调用先做结构自检、
  每个指针和长度都做边界校验、并且**绝不隐藏自己的子进程**（否则启动器会看不见
  自己刚拉起的程序）。

> 摘链表时有个必须写对的细节：前驱节点的 `NextEntryOffset` 要**累加**两段距离
> （`(cur - prev) + next`），而不是直接覆盖成 `next`。覆盖会让链表跳到错误位置，
> 调用方一遍历就 `0xC0000005`。

### ③ 文件锁（加强模式）

这类程序不看命名对象，而是独占打开"自身 exe"或某个 `*.lock` / `*.pid` 文件：

```c
HANDLE h = CreateFile(self, GENERIC_READ | GENERIC_WRITE,
                      /* share */ 0,          // 独占
                      NULL, OPEN_EXISTING, ...);
if (h == INVALID_HANDLE_VALUE && GetLastError() == ERROR_SHARING_VIOLATION)
    -> "已经有一个实例在跑"
```

运行中的 exe 映像本身就占着这张文件，所以第二个实例必然失败。

**加强模式**在 `NtCreateFile` / `NtOpenFile` 这一层，把"独占"主动放宽成"共享读写删"。
注意是**主动**放宽，不是失败后重试——共享模式是双向协商的，A 以 `share=0` 打开之后，
B 就算把 share 开到最大也一样被拒，所以只有每个实例一开始就用宽松的 share 才有效。

| | 关闭加强模式 | 打开加强模式 |
|---|---|---|
| 独占打开自身 exe | 第二个实例 `SHARING_VIOLATION` | 成功 |
| 独占打开程序目录里的 `lock.dat` | 第二个实例失败 | 成功 |
| `%TEMP%` | 所有实例共用 | 每个实例独立目录 |

句柄仍然指向**真实文件**，读写内容、文件偏移、mmap 语义都不变，只是不再因为"别人开着"就拒绝自己。

生效范围严格受限，只碰三种：本进程自身的 exe、命中后缀名单的文件（默认
`*.lock;*.pid;*.mutex`）、程序目录（exe 所在目录）内的文件。系统目录、`\Device\`、
`\SystemRoot\`、相对路径一律不动，读打开（share != 0）也不动。

默认关闭。目标真的要靠独占写同一个存档来防冲突时，这个保护在加强模式下会失效——
这正是它的目的，但要自己权衡。

### ④ 注册表标记

还有一类把"是否已有实例"记在注册表里，比如 `HKCU\Software\<厂商>\<产品>\Instance`。
填**路径片段**（大小写不敏感的子串，多个用 `;` 分隔）后，命中的那段路径后面追加组号，
每个实例就拥有一棵独立的配置子树：

```
HKCU\Software\MyApp            （原始，别人也能看到）
HKCU\Software\MyAppGroup_555454  （本实例专用）
```

填片段而不是完整路径，是因为注册表 API 大量使用"根句柄 + 相对路径"的形式，
片段匹配对绝对路径和相对路径都有效。**留空 = 完全不碰注册表**（默认）。

注意隔离后程序会认为这是一份全新配置，可能要求重新登录或重建设置。

### ⑤ 进程枚举

少数程序自己遍历进程列表数"有几个同名进程"。这种用**全局隐藏**处理；
如果它只是"看到别人就杀掉"，用**受保护进程**更轻。

---

## 两种注入路线（64 位支持的关键）

| 目标 | 做法 |
|---|---|
| **64 位** | `CreateProcess(CREATE_SUSPENDED)` → 远程 `LoadLibraryW(V5Hook64.dll)` → `ResumeThread`。全程在 C# 里完成 |
| **32 位** | 交给 `V5Inject32.exe`：以调试方式创建目标 → 停在 32 位初始断点（加载器就绪、入口点未执行）→ 把入口点前 5 字节改成跳转桩 `push <dll路径>; call LoadLibraryW; 还原入口点; jmp 入口点` → 恢复主线程 |

**32 位为什么不能照搬 64 位那套**：WOW64 进程在"刚创建、还没跑起来"的阶段，
32 位 PEB 的 `Ldr` 还是 0——既没有 32 位模块链表，也没有任何可用的 32 位代码地址；
而 64 位调试器只能看到 64 位侧的断点。就算停在 32 位初始断点，此时主线程仍持有
加载器锁，远程 `LoadLibrary` 会一直阻塞到 `main` 之后才完成，单实例检查早就跑完了。
入口点跳转桩是唯一能做到"零竞态"的接法。

---

## 命令行

```powershell
# 起 3 个实例
V5Sharp.Cli.exe launch --exe "D:\App\App.exe" --count 3

# 启动器型程序：保护主程序进程，并放宽文件独占
V5Sharp.Cli.exe launch --exe "D:\App\Launcher.exe" --workdir "D:\App" --protect "App.exe" --strong --count 2

# 注册表型检测
V5Sharp.Cli.exe launch --exe "D:\App\App.exe" --reg "MyApp"

# 结束
V5Sharp.Cli.exe kill --pid 1234,5678
```

| 参数 | 说明 |
|---|---|
| `--exe` | 目标程序（必填） |
| `--args` | 启动参数 |
| `--workdir` | 工作目录，默认 exe 所在目录 |
| `--protect` / `--hide` | 受保护进程名，多个用分号分隔 |
| `--strong` | 加强模式 |
| `--strong-sfx` | 加强模式的额外文件后缀 |
| `--global-hide` | 全局隐藏 |
| `--reg` | 注册表隔离关键词 |
| `--count` | 实例数，默认 2 |
| `--no-log` | 不写改名日志 |
| `--wait` | 启动后等待指定秒数再退出（方便观察） |

---

## 界面

自绘的浅色界面，没有任何第三方 UI 库：卡片、胶囊按钮、开关、日志面板都是
`OnPaint` 画的，外框是带折射描边的半透明玻璃质感。

字体用的是小米 **MiSans**（免费商用，<https://hyperos.mi.com/font>）。
三个字重 `Regular / Medium / Bold` 放在 `src/V5Sharp.App/Fonts/`，以
`EmbeddedResource` 编进程序集，用户机器不需要安装：

- `FontLoader.cs` 同时调用 `AddFontMemResourceEx`（GDI 侧，`TextBox` 等原生控件走这里）
  与 `PrivateFontCollection.AddMemoryFont`（GDI+ 侧，自绘控件走 `Graphics.DrawString`）；
- 由内嵌字体创建 `Font` 必须用 `FontFamily` 对象：按族名字符串 `new Font("MiSans", …)`
  走的是 GDI+ 的"已安装字体"查找，内嵌字体不在其中，会静默退成 Microsoft Sans Serif；
- 字体内存块在进程生命周期内不释放（字体表被长期引用），任何一步失败都安静退回
  微软雅黑 / Segoe UI；
- 用的是 **OTF（CFF 轮廓）**：GDI 的 ClearType 不作用于 CFF，中文走灰度抗锯齿。
  这是刻意选择的观感——同一套界面换成 TTF 走 ClearType 后，小字号中文会出现彩色噪点、
  观感更糊（已实测对比）；
- 代价是体积：`V5多开器Pro.dll` 从约 60 KB 变成约 19 MB。若要瘦身，把 `Fonts/` 改成
  `Content` 拷到输出目录、`FontLoader` 改用 `AddFontFile` 即可。

MiSans 授权：免费商用、可随软件分发，但不得单独售卖字体文件、不得修改字形后再发布。
来源与版本记录在 `src/V5Sharp.App/Fonts/README.txt`。

---

## 兼容性说明

- 被改名的对象若在系统白名单里（RPC 端点、会话事件、输入法共享内存、`\KernelObjects\*`、
  `*.dll` 等）会**跳过**，否则会把系统或加载器改坏；
- `NtOpen*` 改名后如果返回失败，会**自动用原名重试一次**，避免打开"别人创建的对象"时误伤；
- 若某个 API 的第一条指令不是标准系统调用桩（例如被安全软件做了热补丁跳转），
  该 API 会被跳过，不影响其它 hook 生效；
- 只在"创建 / 打开对象"这一层做隔离，不修改目标文件、不改校验和。

## 已知限制

1. 32 位目标会以"被调试"方式启动一瞬间（注入完成后立即脱离）。若目标对
   `IsDebuggerPresent` 极度敏感、且检查早于入口点，可能被识别；
2. 目标需要管理员权限时，本工具也需要（否则 `OpenProcess` / 注入会失败）；
3. 目标若自带内核态反多开（驱动层校验对象名 / 进程数），用户态 hook 无法绕过；
4. 白名单是经验表，遇到新的系统组件冲突可在 `native/V5Hook/filter.c` 里追加；
5. 界面尺寸按 96 DPI 调过，高 DPI 下部分行高偏紧。

## 测试工程

```
tests/TestTarget/   模拟"只允许单实例"的最小程序（命名互斥体 + 退出提示）
tests/LockTest/     模拟两类文件锁检测，结果写入 locktest.out
tests/RegTest/      建键写值后枚举 HKCU\Software 下的同名键
```

用 CLI 跑一遍就能覆盖主要链路：

```powershell
V5Sharp.Cli.exe launch --exe "tests\TestTarget\bin\Release\net8.0-windows\TestTarget.exe" --count 3
V5Sharp.Cli.exe launch --exe "C:\Windows\SysWOW64\notepad.exe" --count 2 --no-log   # 验证 32 位路径
```

## 授权

本项目的代码可自由使用与修改。第三方字体（MiSans）遵循其自身授权，见
`src/V5Sharp.App/Fonts/README.txt`。
