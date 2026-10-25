# 自签名 + 签名当前构建
#
# 目的：消除 SmartScreen 的「Windows 已保护你的电脑 / 未知发布者」提示。
#
# 限制（务必看清）：
#   * 自签名证书**只对导入了它的这台机器有效**。分发给别人不会生效，也无法绕过对方的 SmartScreen。
#   * 这是本机自用工具的合理做法；要面向公众发布，仍然需要购买 OV/EV 代码签名证书。
#   * 它**不能**改变进程的完整性级别（Low/Medium/High）—— 那是另一回事。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\sign-local.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\sign-local.ps1 -ExePath <某个exe>
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\sign-local.ps1 -RecreateCertificate
#
# 首次运行会把证书导入「受信任的根证书颁发机构」和「受信任的发布者」，
# 届时 Windows 会弹一次安全警告，需要点「是」。

[CmdletBinding()]
param(
    [string] $ExePath,
    [string] $Subject = 'CN=NPMC Launcher (self-signed)',
    [switch] $RecreateCertificate
)

$ErrorActionPreference = 'Stop'

if (-not $ExePath) {
    $ExePath = Join-Path $PSScriptRoot '..\src\NPMC.Launcher.App\bin\Release\net8.0\NPMC.Launcher.App.exe'
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "找不到要签名的文件: $ExePath（先构建一次）"
}

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
Write-Host "目标文件  $ExePath"

# ---- 1) 取得或创建代码签名证书 ----
$cert = $null
if (-not $RecreateCertificate) {
    $cert = Get-ChildItem 'Cert:\CurrentUser\My' -CodeSigningCert |
            Where-Object { $_.Subject -eq $Subject } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
}

if ($cert) {
    Write-Host "复用证书  $($cert.Thumbprint)"
}
else {
    Write-Host "创建自签名代码签名证书…"
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5)

    Write-Host "  已创建  $($cert.Thumbprint)"
}

# ---- 2) 确保证书已受信任（幂等）----
function Test-CertificateInstalled([string] $StorePath, [string] $Thumbprint) {
    Test-Path -LiteralPath (Join-Path $StorePath $Thumbprint)
}

$needsImport = -not (Test-CertificateInstalled 'Cert:\CurrentUser\Root' $cert.Thumbprint) -or
               -not (Test-CertificateInstalled 'Cert:\CurrentUser\TrustedPublisher' $cert.Thumbprint)

if ($needsImport) {
    Write-Host "导入受信任存储（Windows 会弹一次安全警告，请点『是』）…"
    $temp = [System.IO.Path]::GetTempFileName() + '.cer'

    try {
        Export-Certificate -Cert $cert -FilePath $temp -Force | Out-Null

        if (-not (Test-CertificateInstalled 'Cert:\CurrentUser\Root' $cert.Thumbprint)) {
            Import-Certificate -FilePath $temp -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
            Write-Host "  ✓ 受信任的根证书颁发机构"
        }

        if (-not (Test-CertificateInstalled 'Cert:\CurrentUser\TrustedPublisher' $cert.Thumbprint)) {
            Import-Certificate -FilePath $temp -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null
            Write-Host "  ✓ 受信任的发布者"
        }
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
}
else {
    Write-Host "证书已在本机受信任，跳过导入"
}

# ---- 3) 签名 ----
Write-Host "签名中…"
$signature = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256

if ($signature.Status -ne 'Valid') {
    throw "签名失败: $($signature.Status) — $($signature.StatusMessage)"
}

# ---- 4) 复核 ----
$check = Get-AuthenticodeSignature -FilePath $ExePath
Write-Host ''
Write-Host "状态    $($check.Status)"
Write-Host "签名者  $($check.SignerCertificate.Subject)"
Write-Host "指纹    $($check.SignerCertificate.Thumbprint)"

if ($check.Status -eq 'Valid') {
    Write-Host '✓ 已签名，SmartScreen 不再把它当作「未知发布者」' -ForegroundColor Green
}
else {
    Write-Host '! 签名状态不是 Valid，SmartScreen 可能仍会提示' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '注意：每次重新构建后 exe 会变，需要重新运行本脚本签名。'
