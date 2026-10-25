# 提交前守卫
#
# 存在的理由：曾经因为 .gitignore 只写了 /.data/ 而漏掉 data/，
# 一次 `git add -A` 把 170.7 MB、357 个游戏文件提交并推送到了远端，
# 事后不得不重写历史 + 强制推送才清理干净。
#
# 三道检查：
#   1. 暂存文件数上限（正常提交都是个位数到几十个）
#   2. 禁止暂存运行时/生成目录
#   3. 禁止暂存大于 5 MB 的文件
#
# 用法（提交前执行，非 0 退出即中止提交）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\verify-commit.ps1

$ErrorActionPreference = 'Stop'

$staged = @(& git diff --cached --name-only)
$problems = @()

if ($staged.Count -eq 0) {
    Write-Host "· 暂存区为空，无需检查" -ForegroundColor DarkGray
    exit 0
}

if ($staged.Count -gt 50) {
    $problems += "暂存文件过多：$($staged.Count) 个（阈值 50）"
}

# 注意锚定到仓库根的路径，避免误伤 src/.../Runtime/ 这类源码目录
$forbidden = '^(data|\.data|runtime|libraries|assets|versions|instances|saves|bin|obj)/'
foreach ($file in $staged) {
    if ($file -match $forbidden) { $problems += "命中运行时/生成目录：$file" }
}

foreach ($file in $staged) {
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $size = (Get-Item -LiteralPath $file).Length
    if ($size -gt 5MB) {
        $problems += "文件过大（$([math]::Round($size / 1MB, 1)) MB）：$file"
    }
}

if ($problems.Count -gt 0) {
    Write-Host "✗ 提交前检查未通过：" -ForegroundColor Red
    $problems | Select-Object -First 25 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    if ($problems.Count -gt 25) { Write-Host "    …还有 $($problems.Count - 25) 条" -ForegroundColor Red }
    exit 1
}

Write-Host "✓ 提交前检查通过（$($staged.Count) 个文件）" -ForegroundColor Green
exit 0
