param(
    [string]$Root = 'D:\Agents\NPMC-Launcher',
    [string]$JavaHome = 'D:\Agents\NPMC-Launcher\.data\runtime\java-runtime-gamma'
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'tools\skin-agent\src'
$out = Join-Path $Root 'tools\skin-agent\out'
$asm = Join-Path $Root '.data\libraries\org\ow2\asm\asm\9.11\asm-9.11.jar'

$javac = Join-Path $JavaHome 'bin\javac.exe'
$jarTool = Join-Path $JavaHome 'bin\jar.exe'

# The agent jar carries its own copy of ASM because a vanilla client has none. It
# is only ever loaded through -javaagent, never from the game's class path, so
# Fabric's duplicate-ASM check never sees it. The bridge classes that transformed
# game code calls are the only thing that must sit on the bootstrap loader
# (module loaders can only link to bootstrap), so they get their own tiny jar.

# unpack ASM once, it gets copied into both agent jars
$asmExtract = Join-Path $out 'asm-extract'
Remove-Item $asmExtract -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $asmExtract -Force | Out-Null
$asmZip = Join-Path $out 'asm.zip'
Copy-Item $asm $asmZip -Force
Expand-Archive -Path $asmZip -DestinationPath $asmExtract -Force
Remove-Item $asmZip -Force

function Add-Asm {
    param([string]$Classes)

    Copy-Item (Join-Path $asmExtract 'org') $Classes -Recurse -Force
}

function Write-Jar {
    param(
        [string]$Name,
        [string]$Classes
    )

    $manifest = Join-Path $out "$Name-MANIFEST.MF"
    @(
        'Manifest-Version: 1.0'
        'Premain-Class: nmpc.skin.SkinAgent'
        'Agent-Class: nmpc.skin.SkinAgent'
        'Can-Retransform-Classes: true'
    ) | Set-Content $manifest -Encoding ASCII

    $target = Join-Path $out "$Name.jar"
    & $jarTool --create --file $target --manifest $manifest -C $Classes .
    if ($LASTEXITCODE -ne 0) { throw "jar failed: $Name" }

    Write-Host "jar: $target"
}

$nativePreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

$allSources = Get-ChildItem $src -Recurse -Filter '*.java' | Select-Object -ExpandProperty FullName
$legacySources = @($allSources | Where-Object {
        $_ -notmatch 'SkinBridgeModern\.java$' -and $_ -notmatch 'SkinModuleSupport\.java$'
    })

# 1) legacy agent: Java 8 runtime (1.7.10 / 1.12.2)
$legacyClasses = Join-Path $out 'agent-legacy-classes'
Remove-Item $legacyClasses -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $legacyClasses -Force | Out-Null
$legacyCp = @(
    (Join-Path $Root '.data\libraries\com\mojang\authlib\1.5.21\authlib-1.5.21.jar')
    (Join-Path $Root '.data\libraries\com\google\guava\guava\17.0\guava-17.0.jar')
    $asm
) -join ';'
$null = & $javac --release 8 -nowarn -encoding UTF-8 -cp $legacyCp -d $legacyClasses @legacySources 2>&1
if ($LASTEXITCODE -ne 0) { throw 'javac failed: legacy agent' }
Add-Asm -Classes $legacyClasses

# 2) modern agent: Java 17+
$modernClasses = Join-Path $out 'agent-classes'
Remove-Item $modernClasses -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $modernClasses -Force | Out-Null
$modernCp = @(
    (Join-Path $Root '.data\libraries\com\mojang\authlib\7.0.63\authlib-7.0.63.jar')
    (Join-Path $Root '.data\libraries\com\google\guava\guava\33.3.1-jre\guava-33.3.1-jre.jar')
    $asm
) -join ';'
$null = & $javac --release 17 -nowarn -encoding UTF-8 -cp $modernCp -d $modernClasses @allSources 2>&1
if ($LASTEXITCODE -ne 0) { throw 'javac failed: modern agent' }
Add-Asm -Classes $modernClasses

$ErrorActionPreference = $nativePreference

# 3) bridge jar: only what the transformed game code calls, reflection-only
$bridgeClasses = Join-Path $out 'bridge-classes'
Remove-Item $bridgeClasses -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $bridgeClasses -Force | Out-Null
$bridgeSources = @(
    (Join-Path $src 'nmpc\skin\SkinBridgeModern.java')
    (Join-Path $src 'nmpc\skin\SkinBridgeLegacy.java')
)
$ErrorActionPreference = 'Continue'
$null = & $javac --release 8 -nowarn -encoding UTF-8 -d $bridgeClasses @bridgeSources 2>&1
$ErrorActionPreference = $nativePreference
if ($LASTEXITCODE -ne 0) { throw 'javac failed: bridge' }

Write-Jar -Name 'nmpc-skin-agent-legacy' -Classes $legacyClasses
Write-Jar -Name 'nmpc-skin-agent' -Classes $modernClasses

@('Manifest-Version: 1.0') | Set-Content (Join-Path $out 'nmpc-skin-bridge-MANIFEST.MF') -Encoding ASCII
$bridgeTarget = Join-Path $out 'nmpc-skin-bridge.jar'
& $jarTool --create --file $bridgeTarget --manifest (Join-Path $out 'nmpc-skin-bridge-MANIFEST.MF') -C $bridgeClasses .
if ($LASTEXITCODE -ne 0) { throw 'jar failed: bridge' }
Write-Host "jar: $bridgeTarget"
