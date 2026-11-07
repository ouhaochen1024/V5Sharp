# Build V5Hook32.dll / V5Hook64.dll (MSVC, no CRT dependency)
# Usage: powershell -ExecutionPolicy Bypass -File build-native.ps1
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist\payload')
)

$ErrorActionPreference = 'Stop'
$src = (Resolve-Path (Join-Path $PSScriptRoot '..\native\V5Hook')).Path
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found - install VS Build Tools with C++ workload' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio instance with C++ tools found' }

$msvcRoot = Join-Path $vs 'VC\Tools\MSVC'
$msvc = (Get-ChildItem $msvcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkLib = (Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Lib') -Directory |
           Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkInc = (Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Include') -Directory |
           Where-Object { $_.Name -match '^\d+\.' } | Sort-Object Name -Descending | Select-Object -First 1).FullName

Write-Host "MSVC : $msvc"
Write-Host "SDK  : $sdkInc"

$jobs = @(
    @{ Arch = 'x64'; Host = 'x64'; Out = 'V5Hook64.dll' },
    @{ Arch = 'x86'; Host = 'x86'; Out = 'V5Hook32.dll' }
)

foreach ($j in $jobs) {
    $cl = Join-Path $msvc "bin\Hostx64\$($j.Host)\cl.exe"
    if (-not (Test-Path $cl)) { $cl = Join-Path $msvc "bin\Hostx86\$($j.Host)\cl.exe" }
    if (-not (Test-Path $cl)) { Write-Warning "no compiler for $($j.Arch), skipped"; continue }

    $env:INCLUDE = @(
        (Join-Path $msvc 'include')
        (Join-Path $sdkInc 'ucrt')
        (Join-Path $sdkInc 'um')
        (Join-Path $sdkInc 'shared')
        (Join-Path $sdkInc 'winrt')
    ) -join ';'

    $env:LIB = @(
        (Join-Path $msvc "lib\$($j.Arch)")
        (Join-Path $sdkLib "ucrt\$($j.Arch)")
        (Join-Path $sdkLib "um\$($j.Arch)")
    ) -join ';'

    Write-Host "==> building $($j.Arch) ..."
    Push-Location $src
    try {
        $clArgs = @(
            '/nologo', '/LD', '/O2', '/GS-', '/Zl', '/W3', '/utf-8',
            '/DUNICODE', '/D_UNICODE', '/D_WIN32_WINNT=0x0601',
            'v5hook.c', 'hook.c', 'filter.c',
            '/link', '/NODEFAULTLIB', '/ENTRY:DllMain', '/SUBSYSTEM:WINDOWS',
            'kernel32.lib', 'user32.lib',
            "/OUT:$OutDir\$($j.Out)"
        )
        & $cl @clArgs
        if ($LASTEXITCODE -ne 0) { throw "$($j.Arch) build failed (exit=$LASTEXITCODE)" }
    } finally {
        Pop-Location
        # -Include 只有在给路径带通配符（或加 -Recurse）时才生效，否则一个文件都匹配不到，
        # 之前正是因为这个，编译出来的 .obj 一直留在源码目录里。
        Get-ChildItem -Path (Join-Path $src '*') -Include *.obj, *.exp, *.lib -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

Get-ChildItem $OutDir -Filter *.dll | ForEach-Object {
    $arch = if ($_.Name -match '64') { 'x64' } else { 'x86' }
    Write-Host ("    {0,-14} {1,-5} {2:N0} bytes" -f $_.Name, $arch, $_.Length)
}

# ---- 32 位注入辅助进程（仅 x86）----
$helperSrc = (Resolve-Path (Join-Path $PSScriptRoot '..\native\V5Inject32')).Path
$cl86 = Join-Path $msvc 'bin\Hostx64\x86\cl.exe'
if (-not (Test-Path $cl86)) { $cl86 = Join-Path $msvc 'bin\Hostx86\x86\cl.exe' }
if (Test-Path $cl86) {
    $env:INCLUDE = @(
        (Join-Path $msvc 'include')
        (Join-Path $sdkInc 'ucrt')
        (Join-Path $sdkInc 'um')
        (Join-Path $sdkInc 'shared')
    ) -join ';'
    $env:LIB = @(
        (Join-Path $msvc 'lib\x86')
        (Join-Path $sdkLib 'ucrt\x86')
        (Join-Path $sdkLib 'um\x86')
    ) -join ';'

    Write-Host "==> building V5Inject32.exe ..."
    Push-Location $helperSrc
    try {
        $clArgs = @(
            '/nologo', '/O2', '/GS-', '/Zl', '/W3', '/utf-8',
            '/DUNICODE', '/D_UNICODE', '/D_WIN32_WINNT=0x0601',
            'V5Inject32.c',
            '/link', '/NODEFAULTLIB', '/ENTRY:wWinMain', '/SUBSYSTEM:CONSOLE',
            'kernel32.lib', 'user32.lib', 'shell32.lib', 'psapi.lib',
            "/OUT:$OutDir\V5Inject32.exe"
        )
        & $cl86 @clArgs
        if ($LASTEXITCODE -ne 0) { throw "V5Inject32 build failed (exit=$LASTEXITCODE)" }
    } finally {
        Pop-Location
        Get-ChildItem -Path (Join-Path $helperSrc '*') -Include *.obj, *.exp, *.lib -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

Get-ChildItem $OutDir -Filter *.exe | ForEach-Object {
    Write-Host ("    {0,-14} {1,-5} {2:N0} bytes" -f $_.Name, 'x86', $_.Length)
}
Write-Host "done: $OutDir"
