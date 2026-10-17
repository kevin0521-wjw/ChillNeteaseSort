# ChillNetease Sort - 构建脚本
# 用法：
#   .\build.ps1                          # 只编译，产物在 dist\
#   .\build.ps1 -Install                 # 编译并直接装进游戏 plugins\ChillNeteaseSort\
#   .\build.ps1 -GameDir "D:\Steam\...\Chill with You Lo-Fi Story"
param(
    [string]$GameDir = "",
    [switch]$Install,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

if (-not $GameDir) {
    $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story"
}
if (-not (Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.dll"))) {
    throw "在 $GameDir 找不到 BepInEx。请先安装 ChillNetease，或用 -GameDir 指定游戏目录。"
}
if (-not (Test-Path (Join-Path $GameDir "BepInEx\plugins\ChillNetease\ChillNetease.Plugin.dll"))) {
    throw "在 $GameDir 找不到 ChillNetease.Plugin.dll。本插件是它的增强补丁，必须先把 ChillNetease 装好。"
}

# 找 dotnet：优先 PATH，其次 $env:DOTNET_ROOT
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and $env:DOTNET_ROOT) {
    $candidate = Join-Path $env:DOTNET_ROOT "dotnet.exe"
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) {
    throw "找不到 dotnet（需要 .NET SDK 6+）。装好后重试，或设置 `$env:DOTNET_ROOT 指向 SDK 目录。"
}

Write-Host "dotnet  : $dotnet"
Write-Host "GameDir : $GameDir"
Write-Host ""

& $dotnet build (Join-Path $root "ChillNeteaseSort.csproj") `
    -c $Configuration `
    "-p:GameDir=$GameDir" `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

# 把产物收进 dist\
$out = Join-Path $root "bin\$Configuration\netstandard2.1\ChillNeteaseSort.dll"
if (-not (Test-Path $out)) { $out = Join-Path $root "bin\$Configuration\ChillNeteaseSort.dll" }
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $out (Join-Path $dist "ChillNeteaseSort.dll") -Force
Write-Host ""
Write-Host "产物: $dist\ChillNeteaseSort.dll  ($((Get-Item (Join-Path $dist 'ChillNeteaseSort.dll')).Length) bytes)"

if ($Install) {
    $target = Join-Path $GameDir "BepInEx\plugins\ChillNeteaseSort"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $dist "ChillNeteaseSort.dll") $target -Force
    Write-Host "已安装到: $target"
    Write-Host "启动游戏后按 F10 循环切换排序模式（F6 是网易云面板）。"
}
