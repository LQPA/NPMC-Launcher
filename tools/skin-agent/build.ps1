param(
    [string]$Root = 'D:\Agents\NPMC-Launcher',
    [string]$JavaHome = 'D:\Agents\NPMC-Launcher\.data\runtime\java-runtime-gamma'
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'tools\skin-agent\src'
$out = Join-Path $Root 'tools\skin-agent\out'
$classes = Join-Path $out 'classes'
$authlib = Join-Path $Root '.data\libraries\com\mojang\authlib\4.0.43\authlib-4.0.43.jar'
$guava = Join-Path $Root '.data\libraries\com\google\guava\guava\33.3.1-jre\guava-33.3.1-jre.jar'
$asm = Join-Path $Root '.data\libraries\org\ow2\asm\asm\9.6\asm-9.6.jar'

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $classes -Force | Out-Null

$sources = Get-ChildItem $src -Recurse -Filter '*.java' | Select-Object -ExpandProperty FullName

& (Join-Path $JavaHome 'bin\javac.exe') -encoding UTF-8 -cp "$authlib;$guava;$asm" -d $classes @sources
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }

# 把 ASM 解包进 classes，做成自包含 jar（游戏端 Class-Path 不可靠，fat jar 最稳）
$asmZip = Join-Path $out 'asm.zip'
$asmTemp = Join-Path $out 'asm-temp'
Copy-Item $asm $asmZip
Expand-Archive -Path $asmZip -DestinationPath $asmTemp -Force
Copy-Item (Join-Path $asmTemp 'org') $classes -Recurse -Force
Remove-Item $asmZip, $asmTemp -Recurse -Force

$manifest = Join-Path $out 'MANIFEST.MF'
@(
    'Manifest-Version: 1.0'
    'Premain-Class: nmpc.skin.SkinAgent'
    'Agent-Class: nmpc.skin.SkinAgent'
    'Can-Retransform-Classes: true'
) | Set-Content $manifest -Encoding ASCII

$jar = Join-Path $out 'nmpc-skin-agent.jar'
& (Join-Path $JavaHome 'bin\jar.exe') --create --file $jar --manifest $manifest -C $classes .
if ($LASTEXITCODE -ne 0) { throw 'jar failed' }

Write-Host "agent: $jar"
