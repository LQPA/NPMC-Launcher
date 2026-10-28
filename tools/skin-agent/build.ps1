param(
    [string]$Root = 'D:\Agents\NPMC-Launcher',
    [string]$JavaHome = 'D:\Agents\NPMC-Launcher\.data\runtime\java-runtime-gamma'
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'tools\skin-agent\src'
$out = Join-Path $Root 'tools\skin-agent\out'
$asm = Join-Path $Root '.data\libraries\org\ow2\asm\asm\9.11\asm-9.11.jar'
$asmCommons = Join-Path $Root '.data\libraries\org\ow2\asm\asm-commons\9.11\asm-commons-9.11.jar'
$asmTree = Join-Path $Root '.data\libraries\org\ow2\asm\asm-tree\9.11\asm-tree-9.11.jar'
$relocatorSource = Join-Path $Root 'tools\skin-agent\Relocator.java'
$relocatorClasses = Join-Path $out 'relocator'

$javac = Join-Path $JavaHome 'bin\javac.exe'
$java = Join-Path $JavaHome 'bin\java.exe'
$jarTool = Join-Path $JavaHome 'bin\jar.exe'

# Relocation tool: move the embedded ASM from org/objectweb/asm into a private
# package. Fabric aborts when it finds two copies of org.objectweb.asm on the
# classpath, and vanilla has no ASM of its own, so the agent must keep carrying
# it - just under a name nobody else uses.
Remove-Item $relocatorClasses -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $relocatorClasses -Force | Out-Null

$nativePreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$null = & $javac -encoding UTF-8 -nowarn -cp ($asm + ';' + $asmCommons + ';' + $asmTree) -d $relocatorClasses $relocatorSource 2>&1
$ErrorActionPreference = $nativePreference
if ($LASTEXITCODE -ne 0) { throw 'relocator build failed' }

$relocatorClasspath = @($asm, $asmCommons, $asmTree, $relocatorClasses) -join ';'
$asmPrefix = 'org/objectweb/asm'
$asmTarget = 'nmpc/skinned/asmx'

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

    $nativePreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $null = & $javac --release $Release -encoding UTF-8 -nowarn -cp ($Classpath -join ';') -d $classes @Sources 2>&1
    $ErrorActionPreference = $nativePreference
    if ($LASTEXITCODE -ne 0) { throw "javac failed: $Name" }

    # Rewrite the ASM references inside our own classes first, then bring in a
    # relocated copy of ASM itself.
    $null = & $java -cp $relocatorClasspath Relocator dir (Join-Path $classes 'nmpc\skin') $asmPrefix $asmTarget 2>&1
    if ($LASTEXITCODE -ne 0) { throw "skin relocation failed: $Name" }

    $null = & $java -cp $relocatorClasspath Relocator jar $asm $classes $asmPrefix $asmTarget 2>&1
    if ($LASTEXITCODE -ne 0) { throw "asm relocation failed: $Name" }

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
$legacySources = @($allSources | Where-Object {
        $_ -notmatch 'SkinBridgeModern\.java$' -and $_ -notmatch 'SkinModuleSupport\.java$'
    })

$legacyClasspath = @()
$legacyClasspath += (Join-Path $Root '.data\libraries\com\mojang\authlib\1.5.21\authlib-1.5.21.jar')
$legacyClasspath += (Join-Path $Root '.data\libraries\com\google\guava\guava\17.0\guava-17.0.jar')
$legacyClasspath += $asm

# authlib 1.5.21: Java 8 runtime (1.7.10)
Build-Agent -Name 'nmpc-skin-agent-legacy' -Release '8' -Classpath $legacyClasspath -Sources $legacySources

$modernClasspath = @()
$modernClasspath += (Join-Path $Root '.data\libraries\com\mojang\authlib\7.0.63\authlib-7.0.63.jar')
$modernClasspath += (Join-Path $Root '.data\libraries\com\google\guava\guava\33.3.1-jre\guava-33.3.1-jre.jar')
$modernClasspath += $asm

# authlib 7: Java 17+ (1.20.1 / 1.21.6 / 26.1.2)
Build-Agent -Name 'nmpc-skin-agent' -Release '17' -Classpath $modernClasspath -Sources $allSources
