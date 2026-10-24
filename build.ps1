<#
    NPMC Launcher 构建脚本

    为什么必须 -m:1：
      本机 SDK 9.0.318 的并行 MSBuild 工作节点会静默崩溃（表现为"生成失败 / 0 错误"），
      同时伴随 MSB4276「WorkloadAutoImportPropsLocator SDK 目录不存在」。
      改用单进程（-maxcpucount:1）可稳定通过。
      换到健康环境后可去掉 -m:1。
#>
param(
    [switch]$Clean,
    [switch]$IncludeTests
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

# dotnet CLI 缓存重定向到工程内，避免写用户目录
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES  = Join-Path $root '.nuget'
$env:DOTNET_NOLOGO   = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if ($Clean) {
    Get-ChildItem $root -Recurse -Directory -Include bin, obj -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notlike '*\.dotnet-home\*' -and $_.FullName -notlike '*\.nuget\*' } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force; Write-Host "  clean $($_.FullName.Replace($root,'.'))" -ForegroundColor DarkGray }
}

$projects = @(
    'src\NPMC.Launcher.Core\NPMC.Launcher.Core.csproj',
    'src\NPMC.Launcher.Infrastructure\NPMC.Launcher.Infrastructure.csproj',
    'src\NPMC.Launcher.Cli\NPMC.Launcher.Cli.csproj',
    'src\NPMC.Launcher.App\NPMC.Launcher.App.csproj'
)
if ($IncludeTests) { $projects += 'tests\NPMC.Launcher.Core.Tests\NPMC.Launcher.Core.Tests.csproj' }

$failed = @()
foreach ($p in $projects) {
    Write-Host "`n>>> $p" -ForegroundColor Cyan
    & dotnet build (Join-Path $root $p) -c Release --nologo -maxcpucount:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { $failed += $p }
}

Write-Host ""
if ($failed.Count -eq 0) { Write-Host "全部构建成功" -ForegroundColor Green; exit 0 }
Write-Host "构建失败: $($failed -join ', ')" -ForegroundColor Red
exit 1