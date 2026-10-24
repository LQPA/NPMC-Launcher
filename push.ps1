<#
    NPMC Launcher - 首次推送到 GitHub

    前置条件：
      1. 安装 Git for Windows  https://git-scm.com/download/win
      2. 首次 push 时会弹浏览器要求授权，用你的 GitHub 账号登录即可

    设计要点：
      - 远端已有 README.md 与 LICENSE（AppRegInfo 申请材料引用了这个仓库），
        本脚本会先 fetch 远端历史、恢复这两个文件，再提交本地代码，
        因此不会覆盖或删除它们。
      - .data/ 已在 .gitignore 中排除，不会把约 820MB 游戏数据推上去。
#>
param(
    [string]$Remote = 'https://github.com/LQPA/NPMC-Launcher.git',
    [string]$Branch = 'main'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host '未找到 git。请先安装 Git for Windows: https://git-scm.com/download/win' -ForegroundColor Red
    Write-Host '装好后重新运行本脚本即可。' -ForegroundColor Yellow
    exit 1
}

Write-Host '=== 1) 初始化本地仓库 ===' -ForegroundColor Cyan
if (-not (Test-Path '.git')) {
    git init
    git branch -M $Branch
}

Write-Host ''
Write-Host '=== 2) 配置远端 ===' -ForegroundColor Cyan
git remote remove origin 2>$null
git remote add origin $Remote

Write-Host ''
Write-Host '=== 3) 拉取远端历史（保留 README.md / LICENSE）===' -ForegroundColor Cyan
git fetch origin $Branch
git reset --mixed "origin/$Branch"

foreach ($f in @('README.md', 'LICENSE')) {
    git ls-files --error-unmatch $f *> $null
    if ($LASTEXITCODE -eq 0) {
        git checkout -- $f
        Write-Host "  已从远端恢复 $f" -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host '=== 4) 暂存本地代码 ===' -ForegroundColor Cyan
git add -A
Write-Host '  将提交以下变更：' -ForegroundColor DarkGray
git status --short

Write-Host ''
Write-Host '=== 5) 提交 ===' -ForegroundColor Cyan
$msgPath = Join-Path $PSScriptRoot '.commit-msg.txt'
$msg = @'
Add launcher core

- Core: version metadata model (with polymorphic arguments converter),
  rules evaluator, portable path resolution, classpath/argument builder
- Infrastructure: SHA1-verifying atomic file downloader, version manifest
  source, concurrent asset installer, Mojang Java runtime installer
- Cli: probe / install / runtime / launch commands
- docs: architecture outline with measured baselines

Verified: 1.20.1 launches to the main menu, world generation completes and
saves correctly; built-in Java 17 runtime still works after relocating the
whole folder (tested with spaces and non-ASCII characters in the path).
'@
Set-Content -LiteralPath $msgPath -Value $msg -Encoding UTF8
git commit -F $msgPath
Remove-Item $msgPath -Force

Write-Host ''
Write-Host '=== 6) 推送 ===' -ForegroundColor Cyan
git push -u origin $Branch

Write-Host ''
Write-Host "完成。仓库地址: $Remote" -ForegroundColor Green
