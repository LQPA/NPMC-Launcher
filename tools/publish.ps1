#Requires -Version 5.1
<#
.SYNOPSIS
  产出可分发的 NPMC Launcher。

.DESCRIPTION
  默认打成"自包含单文件"：一个 exe，目标机器不需要预装 .NET 运行时。
  加 -FrameworkDependent 则只做框架依赖发布（体积小，但对方要装 .NET 8 Desktop Runtime）。

.EXAMPLE
  .\tools\publish.ps1

.EXAMPLE
  .\tools\publish.ps1 -FrameworkDependent

.EXAMPLE
  .\tools\publish.ps1 -OutDir E:\nmpc-dist
#>
[CmdletBinding()]
param(
    [string]$OutDir = 'dist',
    [switch]$FrameworkDependent,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\NPMC.Launcher.App\NPMC.Launcher.App.csproj'

if (-not (Test-Path $project)) {
    throw "找不到项目文件：$project（脚本应在 <repo>\tools\ 下运行）"
}

# ---- 解析并校验输出目录 ----
$target = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
$target = [System.IO.Path]::GetFullPath($target)
$repoFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
$insideRepo = $target.StartsWith($repoFull + '\', [StringComparison]::OrdinalIgnoreCase)

Write-Host "仓库    : $repoRoot" -ForegroundColor Cyan
Write-Host "项目    : src\NPMC.Launcher.App\NPMC.Launcher.App.csproj"
Write-Host "输出    : $target$(if (-not $insideRepo) { '   (仓库外，不会自动清理)' })"

# 只清空仓库内的输出目录；仓库外的目录留给用户自己管理
if ($insideRepo -and (Test-Path $target)) {
    Write-Host "清理旧产物…" -ForegroundColor DarkGray
    Remove-Item $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target -Force | Out-Null

# ---- 构建参数（与仓库其它脚本一致：项目内缓存、单进程构建） ----
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget'
$env:DOTNET_NOLOGO = '1'

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--nologo',
    '-maxcpucount:1',
    '-nodeReuse:false',
    '-o', $target
)

if ($FrameworkDependent) {
    $publishArgs += @('--self-contained', 'false')
    Write-Host "模式    : 框架依赖（需 .NET 8 Desktop Runtime）" -ForegroundColor Yellow
}
else {
    $publishArgs += @(
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    Write-Host "模式    : 自包含单文件（无需预装运行时）" -ForegroundColor Green
}

Write-Host ""
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "发布失败（dotnet publish 退出码 $LASTEXITCODE）"
}

# ---- 结果 ----
$exe = Join-Path $target 'NPMC.Launcher.App.exe'
if (-not (Test-Path $exe)) {
    throw "发布完成但找不到 $exe"
}

$size = (Get-Item $exe).Length / 1MB
Write-Host ""
Write-Host "完成：$exe  ($([math]::Round($size,1)) MB)" -ForegroundColor Green

if ($FrameworkDependent) {
    Write-Host "目标机器需要安装 .NET 8 Desktop Runtime。" -ForegroundColor Yellow
}
else {
    Write-Host "直接把该 exe 拷给用户即可，双击运行（不会弹控制台，也不需要 launch-hidden.vbs）。" -ForegroundColor Green
}

Write-Host ""
Write-Host "首次启动会在 exe 同目录下建立 runtime\、instances\ 等目录，因此要放在可写位置" -ForegroundColor DarkGray
Write-Host "（推荐 %LOCALAPPDATA%\NPMC Launcher，不要直接放 Program Files）。" -ForegroundColor DarkGray
