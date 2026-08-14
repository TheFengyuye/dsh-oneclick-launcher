# 构建 DeepSeek Harness 一键启动器 (在仓库目录内)
# 用法: pwsh -File build.ps1  (或右键 -> 使用 PowerShell 运行)
# 需要: Windows 自带的 .NET Framework 4.x (csc.exe), 无需额外安装
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

# --- 图标: 优先用 icon-source/meme.png (蓝色大肥鱼), 不存在则用内置鲸鱼图标 ---
$icon = Join-Path $here "launcher.ico"
$meme = Join-Path $here "icon-source\meme.png"
if (Test-Path $meme) {
    & (Join-Path $here "make-icon-from-image.ps1") -InputImage $meme -OutputIco $icon
} else {
    & (Join-Path $here "make-icon.ps1")
}
if (-not (Test-Path $icon)) { throw "icon generation failed: launcher.ico missing" }

# --- 定位 csc.exe (优先 64 位框架) ---
$cscCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe ( .NET Framework 4.x ) not found" }

# --- 输出路径 ---
# csc.exe 是 ANSI 程序, 无法可靠接收中文文件名参数; 先编译为 ASCII 名再复制为中文名
$outTmp = Join-Path $here "DSH-Launcher.exe"
$outExe = Join-Path $here "DeepSeek Harness 一键启动.exe"

# --- 编译 ---
$src = Join-Path $here "Launcher.cs"
$args = @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/out:$outTmp",
    "/win32icon:$icon",
    "/r:System.Windows.Forms.dll",
    "/r:System.Drawing.dll",
    $src
)
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }

Copy-Item $outTmp $outExe -Force
Remove-Item $outTmp -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "build OK -> $outExe"
