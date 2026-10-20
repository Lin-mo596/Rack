# Rack 编译脚本 —— 只用 Windows 自带的 csc.exe，不需要 .NET SDK 或 Visual Studio
# 用法:  .\build.ps1
# 产物:  dist\Rack.exe

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "找不到 csc.exe。需要 .NET Framework 4.x（Windows 10/11 自带）。"
}

# WPF 程序集在 GAC 里，路径带版本号，每次都要现找
function Find-GacAssembly([string]$name, [string]$prefer) {
    $all = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly' -Recurse -Filter $name -ErrorAction SilentlyContinue
    if ($prefer) {
        $hit = $all | Where-Object { $_.FullName -match $prefer } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $hit = $all | Select-Object -First 1
    if (-not $hit) { throw "找不到 $name（需要 .NET Framework 4.x）" }
    return $hit.FullName
}

$refs = @(
    (Find-GacAssembly 'PresentationFramework.dll' 'GAC_MSIL'),
    (Find-GacAssembly 'PresentationCore.dll'      'GAC_64'),
    (Find-GacAssembly 'WindowsBase.dll'           'GAC_MSIL'),
    (Find-GacAssembly 'System.Xaml.dll'           'GAC_MSIL'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll')
)

foreach ($r in $refs) {
    if (-not (Test-Path $r)) { throw "缺少引用: $r" }
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$out = Join-Path $distDir 'Rack.exe'
$sources = @(
    (Join-Path $srcDir 'Rack.cs'),
    (Join-Path $srcDir 'DockWindow.cs'),
    (Join-Path $srcDir 'SettingsWindow.cs')
)

Write-Host "编译中..." -ForegroundColor Cyan

& $csc /nologo /target:winexe /codepage:65001 /optimize+ "/out:$out" `
    ($refs | ForEach-Object { "/r:$_" }) $sources

if ($LASTEXITCODE -ne 0) {
    throw "编译失败（退出码 $LASTEXITCODE）"
}

$size = (Get-Item $out).Length
Write-Host ("编译成功 -> {0}  ({1:N0} 字节)" -f $out, $size) -ForegroundColor Green