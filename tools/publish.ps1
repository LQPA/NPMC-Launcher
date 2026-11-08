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
    [string]$Runtime = 'win-x64',

    # 代码签名：默认自动找 CurrentUser\My 里带私钥的 NPMC 代码签名证书；
    # 没有证书时只警告并跳过（仍然产出可运行 exe）。
    [switch]$NoSign,
    [string]$SignThumbprint = '',

    # 正式证书可以直接给 PFX；密码优先取参数，其次环境变量 NPMC_SIGN_PFX_PASSWORD。
    [string]$PfxPath = '',
    [string]$PfxPassword = $env:NPMC_SIGN_PFX_PASSWORD
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

# 先发布到临时 staging，再只把产物复制到目标目录。
# 绝不递归清空 dist：dist\data 和 dist\minecraft 是用户数据。
$staging = Join-Path $repoRoot '.publish'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
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
    '-o', $staging
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
$stagedExe = Join-Path $staging 'NPMC.Launcher.App.exe'
if (-not (Test-Path $stagedExe)) {
    throw "发布完成但找不到 $stagedExe"
}

# ---- 目标 exe 被占用就先关掉 ----
# 用户在目标目录里开着启动器时，Copy-Item 会 Access denied —— 以前脚本照样打印「完成」，
# 结果 E: 上还是旧版本（2026-11-08 实际踩到）。开发阶段允许直接结束进程，这里先礼后兵：
# 先请求正常关闭窗口，3 秒不退再强杀，最长等 15 秒。
$targetExe = Join-Path $target 'NPMC.Launcher.App.exe'

function Test-FileLocked([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    try {
        $stream = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
        $stream.Close()
        return $false
    } catch {
        return $true
    }
}

if (Test-FileLocked $targetExe) {
    $holders = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [string]::Equals($_.Path, $targetExe, [StringComparison]::OrdinalIgnoreCase) })

    if ($holders.Count -eq 0) {
        Write-Host "! $targetExe 被占用，但没找到对应进程；继续尝试覆盖" -ForegroundColor Yellow
    }
    else {
        foreach ($process in $holders) {
            Write-Host "关闭占用中的启动器（PID $($process.Id)）…" -ForegroundColor Yellow
            try { [void]$process.CloseMainWindow() } catch { }
        }

        $deadline = (Get-Date).AddSeconds(3)
        while ((Get-Date) -lt $deadline -and (Test-FileLocked $targetExe)) { Start-Sleep -Milliseconds 200 }

        if (Test-FileLocked $targetExe) {
            foreach ($process in $holders) {
                if ($process.HasExited) { continue }
                Write-Host "强杀 PID $($process.Id)（开发阶段允许）" -ForegroundColor Yellow
                try { $process.Kill() } catch { }
            }

            $deadline = (Get-Date).AddSeconds(12)
            while ((Get-Date) -lt $deadline -and (Test-FileLocked $targetExe)) { Start-Sleep -Milliseconds 250 }
        }

        if (Test-FileLocked $targetExe) {
            throw "$targetExe 仍被占用，无法覆盖。请手动关掉启动器后重试。"
        }
    }
}

try {
    Get-ChildItem $staging -Force | Copy-Item -Destination $target -Recurse -Force
}
catch {
    throw "覆盖 $target 失败：$($_.Exception.Message)`n目标目录可能还开着启动器或游戏进程，先关掉再重试。"
}
$exe = Join-Path $target 'NPMC.Launcher.App.exe'

# 覆盖后必须核对哈希：以前被占用时复制没生效，脚本却照样打印「完成」。
if ((Get-FileHash $stagedExe -Algorithm SHA256).Hash -ne (Get-FileHash $exe -Algorithm SHA256).Hash) {
    throw "复制后哈希不一致：$exe 仍是旧版本（可能被占用），发布中止。"
}

# 便携启动脚本：复制到别的目录后，先抬 Medium 再启动，避免 Low 权限
$startScript = Join-Path $repoRoot 'tools\start.cmd'
if (Test-Path $startScript) {
    Copy-Item $startScript (Join-Path $target 'start.cmd') -Force
}

# ---- 代码签名 ----
# 先签名再抬完整性：Authenticode 会重写 PE 校验和，但不会改变文件内容哈希之外的元数据。
$signatureStatus = '未签名（-NoSign）'

if (-not $NoSign) {
    $signtool = $null
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        $signtool = $command.Source
    }
    else {
        $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        if (Test-Path $kitsRoot) {
            $signtool = Get-ChildItem $kitsRoot -Directory |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
                Where-Object { Test-Path $_ } |
                Select-Object -First 1
        }
    }

    $certificate = $null
    $pfxPath = $null

    if ($PfxPath) {
        $resolvedPfx = Resolve-Path -LiteralPath $PfxPath -ErrorAction SilentlyContinue
        if ($resolvedPfx) { $pfxPath = $resolvedPfx.Path }
    }
    elseif ($SignThumbprint) {
        $certificate = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Thumbprint -eq $SignThumbprint } |
            Select-Object -First 1
    }
    else {
        $certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object { $_.HasPrivateKey -and $_.Subject -like '*NPMC*' } |
            Select-Object -First 1
    }

    if (-not $signtool) {
        $signatureStatus = '跳过：找不到 signtool.exe'
    }
    elseif ($PfxPath -and -not $pfxPath) {
        $signatureStatus = "跳过：找不到 PFX 文件（$PfxPath）"
    }
    elseif (-not $pfxPath -and -not $certificate) {
        $signatureStatus = '跳过：没有可用的代码签名证书'
    }
    else {
        if ($pfxPath) {
            $signArgs = @('sign', '/f', $pfxPath, '/fd', 'SHA256')
            if ($PfxPassword) { $signArgs += @('/p', $PfxPassword) }
            $signArgs += @('/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $exe)
            $display = "PFX $pfxPath"
        }
        else {
            $signArgs = @(
                'sign', '/sha1', $certificate.Thumbprint, '/fd', 'SHA256',
                '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $exe
            )
            $display = "$($certificate.Subject)（$($certificate.Thumbprint)）"
        }

        & $signtool @signArgs | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "! 带时间戳签名失败（退出码 $LASTEXITCODE），改用无时间戳重试…" -ForegroundColor Yellow
            if ($pfxPath) {
                $retry = @('sign', '/f', $pfxPath, '/fd', 'SHA256')
                if ($PfxPassword) { $retry += @('/p', $PfxPassword) }
            }
            else {
                $retry = @('sign', '/sha1', $certificate.Thumbprint, '/fd', 'SHA256')
            }
            $retry += $exe
            & $signtool @retry | Out-Null
        }

        if ($LASTEXITCODE -eq 0) {
            $signatureStatus = '已签名'
            Write-Host "签名    : $display" -ForegroundColor Green
        }
        else {
            $signatureStatus = "签名失败（signtool 退出码 $LASTEXITCODE）"
            Write-Host "! $signatureStatus" -ForegroundColor Yellow
        }
    }

    if ($signatureStatus -ne '已签名') {
        Write-Host "! $signatureStatus；产物仍是未签名 exe。" -ForegroundColor Yellow
    }
}

# 便于脚本/用户核对：自签名证书在别的机器上仍会提示“未知发布者”。
try {
    $authenticode = Get-AuthenticodeSignature -FilePath $exe
    if ($authenticode.Status -ne 'NotSigned') {
        $subject = if ($authenticode.SignerCertificate) { $authenticode.SignerCertificate.Subject } else { '(未知)' }
        Write-Host "验证    : $($authenticode.Status) / $subject" -ForegroundColor DarkGray
    }
}
catch {
    Write-Host "! 无法读取 Authenticode 状态：$($_.Exception.Message)" -ForegroundColor Yellow
}

# ---- 完整性级别 ----
# 本仓库所在的 D:\Agents 是 Low 完整性，发布出来的 exe 会继承 Low 标签。
# Explorer 双击走 ShellExecute，对 Low 完整性的 exe 会静默卡住（任务管理器里连进程都没有），
# 而 cmd 的 start / CreateProcess 能正常启动。这里显式抬到 Medium（自己的文件，不需要管理员）。
try {
    & icacls $exe /setintegritylevel Medium | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls 退出码 $LASTEXITCODE" }

    $label = (& icacls $exe | Select-String 'Mandatory Label' | Select-Object -First 1).Line
    Write-Host "完整性  : $($label.Trim())" -ForegroundColor DarkGray
}
catch {
    Write-Host "! 无法把 exe 抬到 Medium 完整性：$($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "! 双击 exe 可能没有反应；请改用仓库根目录的 launch.cmd（dotnet 宿主）启动。" -ForegroundColor Yellow
}

# ---- 发布清单（SHA256） ----
$releaseFiles = @()
$releaseFiles += Get-Item $exe
$startScriptPath = Join-Path $target 'start.cmd'
if (Test-Path $startScriptPath) { $releaseFiles += Get-Item $startScriptPath }

$hashLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}

$sumsPath = Join-Path $target 'SHA256SUMS.txt'
Set-Content -Path $sumsPath -Value $hashLines -Encoding ASCII
$exeHash = $hashLines | Where-Object { $_ -match 'NPMC\.Launcher\.App\.exe$' } | Select-Object -First 1
Set-Content -Path (Join-Path $target 'NPMC.Launcher.App.exe.sha256') -Value $exeHash -Encoding ASCII

Write-Host ""
Write-Host "SHA256  : $sumsPath" -ForegroundColor Cyan
$hashLines | ForEach-Object { Write-Host "          $_" -ForegroundColor DarkGray }

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
