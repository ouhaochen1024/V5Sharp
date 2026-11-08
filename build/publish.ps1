# 打包一个"自带运行时"的发布包
#
#   powershell -ExecutionPolicy Bypass -File build\publish.ps1
#   powershell -ExecutionPolicy Bypass -File build\publish.ps1 -SkipNative -Version 1.1.1
#
# 产出：dist\release\V5Sharp-<版本>-win-x64.zip
#   ├─ V5多开器Pro.exe     自包含单文件（内含 .NET 8 运行时 + MiSans 字体）
#   ├─ payload\            注入模块，必须和 exe 放在一起
#   ├─ README.md
#   └─ v5sharp.ini         默认配置（首次运行会写回这里）
#
# 为什么要 payload 目录而不是全塞进一个 exe：
#   V5Hook32.dll 要由 32 位辅助进程 V5Inject32.exe 用 LoadLibrary 加载，
#   两者都必须以真实文件形式存在；单文件把内容解到临时目录后，注入路径和
#   目标进程的位数/权限都对不上，反而不稳。exe 本体已经是单文件了，够了。
param(
    [string]$Version,
    [string]$OutDir,
    [string]$Runtime = 'win-x64',
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$proj = Join-Path $root 'src\V5Sharp.App\V5Sharp.App.csproj'

if (-not $Version) {
    $Version = (Select-String -Path $proj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
}
if (-not $OutDir) { $OutDir = Join-Path $root 'dist\release' }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

$stage = Join-Path $OutDir 'V5Sharp'
$zip = Join-Path $OutDir "V5Sharp-$Version-$Runtime.zip"

Write-Host "版本  : $Version"
Write-Host "输出  : $zip"

# 1) 原生 payload
if (-not $SkipNative) {
    Write-Host '==> 编译原生 payload'
    & (Join-Path $PSScriptRoot 'build-native.ps1') | Out-Null
}
$payloadDir = Join-Path $root 'dist\payload'
foreach ($f in 'V5Hook32.dll', 'V5Hook64.dll', 'V5Inject32.exe') {
    if (-not (Test-Path (Join-Path $payloadDir $f))) {
        throw "缺少 payload\$f，先跑 build\build-native.ps1（或去掉 -SkipNative）"
    }
}

# 2) 发布 C# 宿主：自包含 + 单文件
#    不打开 EnableCompressionInSingleFile：外层已经是 zip，内层再压一遍只会让
#    每次启动都多一次解压（80MB 解到内存），下载体积却几乎不变。
Write-Host '==> dotnet publish'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

& dotnet publish $proj `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:FileVersion=$Version `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 (exit=$LASTEXITCODE)" }

# 3) 随包文件
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'payload') | Out-Null
Copy-Item (Join-Path $payloadDir '*') (Join-Path $stage 'payload') -Force
Copy-Item (Join-Path $root 'README.md') $stage -Force

@'
; 首次运行会自动写入本文件。字段说明见 README.md
[V5Sharp]
ExePath=
Arguments=
WorkDir=
HideNames=
Count=2
WriteHookLog=1
StrongMode=0
StrongSuffixes=
GlobalHide=0
RegFilter=
PanelsOpen=0
TopMost=0
'@ | Set-Content -Path (Join-Path $stage 'v5sharp.ini') -Encoding utf8

# 单文件发布仍可能留下 pdb / 多余的本地化目录，清掉
Get-ChildItem $stage -Recurse -Include *.pdb, *.xml -File | Remove-Item -Force -ErrorAction SilentlyContinue

# 4) 压缩
Write-Host '==> 压缩'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

$size = (Get-Item $zip).Length
Write-Host ''
Write-Host ('-' * 60)
Get-ChildItem $stage -Recurse -File | Sort-Object Length -Descending |
    ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1)
        Write-Host ("  {0,-40} {1,10:N0} KB" -f $rel, ($_.Length / 1KB))
    }
Write-Host ('-' * 60)
Write-Host ("  压缩包  {0}  {1:N1} MB" -f (Split-Path $zip -Leaf), ($size / 1MB))
