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

function Build-Agent {
    param(
        [string]$Name,
        [string]$Release,
        [string[]]$Classpath,
        [string[]]$Sources
    )

    $classes = Join-Path $out "$Name-classes"
    Remove-Item $classes -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $classes -Force | Out-Null

    & $javac --release $Release -encoding UTF-8 -cp ($Classpath -join ';') -d $classes @Sources
    if ($LASTEXITCODE -ne 0) { throw "javac failed: $Name" }

    # 把 ASM 解包进 classes，做成自包含 jar
    $asmZip = Join-Path $out "$Name-asm.zip"
    $asmTemp = Join-Path $out "$Name-asm"
    Copy-Item $asm $asmZip
    Expand-Archive -Path $asmZip -DestinationPath $asmTemp -Force
    Copy-Item (Join-Path $asmTemp 'org') $classes -Recurse -Force
    Remove-Item $asmZip, $asmTemp -Recurse -Force

    $manifest = Join-Path $out "$Name-MANIFEST.MF"
    @(
        'Manifest-Version: 1.0'
        'Premain-Class: nmpc.skin.SkinAgent'
        'Agent-Class: nmpc.skin.SkinAgent'
        'Can-Retransform-Classes: true'
    ) | Set-Content $manifest -Encoding ASCII

    $target = Join-Path $out "$Name.jar"
    & $jarTool --create --file $target --manifest $manifest -C $classes .
    if ($LASTEXITCODE -ne 0) { throw "jar failed: $Name" }

    Write-Host "agent: $target ($Release)"
}

$allSources = Get-ChildItem $src -Recurse -Filter '*.java' | Select-Object -ExpandProperty FullName
$legacySources = @($allSources | Where-Object { $_ -notmatch 'SkinBridgeModern\.java$' })

$legacyClasspath = @()
$legacyClasspath += (Join-Path $Root '.data\libraries\com\mojang\authlib\1.5.21\authlib-1.5.21.jar')
$legacyClasspath += (Join-Path $Root '.data\libraries\com\google\guava\guava\17.0\guava-17.0.jar')
$legacyClasspath += $asm

# authlib 1.5.21：Java 8 运行时（1.7.10）
Build-Agent -Name 'nmpc-skin-agent-legacy' -Release '8' -Classpath $legacyClasspath -Sources $legacySources

$modernClasspath = @()
$modernClasspath += (Join-Path $Root '.data\libraries\com\mojang\authlib\7.0.63\authlib-7.0.63.jar')
$modernClasspath += (Join-Path $Root '.data\libraries\com\google\guava\guava\33.3.1-jre\guava-33.3.1-jre.jar')
$modernClasspath += $asm

# authlib 7：Java 17+（1.20.1 / 1.21.6 / 26.1.2）
Build-Agent -Name 'nmpc-skin-agent' -Release '17' -Classpath $modernClasspath -Sources $allSources
