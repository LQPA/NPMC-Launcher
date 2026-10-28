param(
    [string]$Root = 'D:\Agents\NPMC-Launcher',
    [string]$JavaHome = 'D:\Agents\NPMC-Launcher\.data\runtime\java-runtime-gamma'
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'tools\skin-agent\src'
$out = Join-Path $Root 'tools\skin-agent\out'
$asm = Join-Path $Root '.data\libraries\org\ow2\asm\asm\9.11\asm-9.11.jar'
$resourceDir = Join-Path $Root 'src\NPMC.Launcher.Infrastructure\Skins\Agent'

$javac = Join-Path $JavaHome 'bin\javac.exe'
$jarTool = Join-Path $JavaHome 'bin\jar.exe'

# 四个 jar，各有各的加载器位置 —— 混在一起就会踩 Fabric / ModLauncher 的坑：
#
#   nmpc-skin-agent.jar        -javaagent 入口（AgentStub），**不含 ASM**，两套实现共用。
#                              JVM 会把这个 jar 放进系统类加载器，Fabric 的
#                              verifyClasspath 数得到里面的 org/objectweb/asm，
#                              所以它里面一个 ASM 类都不能有（1.15/1.21.11 Fabric 都踩过）。
#   nmpc-skin-agent-impl.jar   modern 实现 + ASM（Java 17），由 AgentStub 用子加载器加载；
#                              系统类路径与 bootstrap 都看不到它的 ASM。
#   nmpc-skin-agent-impl-legacy.jar
#                              Java 8 实现 + ASM（1.15 及更早、forge 1.7.10/1.12.2），同样由子加载器加载。
#   nmpc-skin-bridge.jar       只有被改写的游戏代码会调用的桥接类：
#                              Java 17+ 挂 bootstrap（模块化加载器只能链接 bootstrap），
#                              Java 8 挂系统类加载器（LaunchWrapper / Knot 的父加载器）。

# unpack ASM once, it gets copied into the two impl jars
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
        [string]$Classes,
        [string]$Premain
    )

    $manifest = Join-Path $out "$Name-MANIFEST.MF"
    if ([string]::IsNullOrEmpty($Premain)) {
        @('Manifest-Version: 1.0') | Set-Content $manifest -Encoding ASCII
        $target = Join-Path $out "$Name.jar"
        & $jarTool --create --file $target --manifest $manifest -C $Classes .
    } else {
        @(
            'Manifest-Version: 1.0'
            "Premain-Class: $Premain"
            "Agent-Class: $Premain"
            'Can-Retransform-Classes: true'
        ) | Set-Content $manifest -Encoding ASCII
        $target = Join-Path $out "$Name.jar"
        & $jarTool --create --file $target --manifest $manifest -C $Classes .
    }

    if ($LASTEXITCODE -ne 0) { throw "jar failed: $Name" }

    Write-Host "jar: $target"
}

$nativePreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

$allSources = Get-ChildItem $src -Recurse -Filter '*.java' | Select-Object -ExpandProperty FullName
$bridgeSources = @(
    (Join-Path $src 'nmpc\skin\SkinBridgeModern.java')
    (Join-Path $src 'nmpc\skin\SkinBridgeLegacy.java')
    (Join-Path $src 'nmpc\skin\SkinPortBridge.java')
)
# 两套 impl 都不带桥接类：桥接类只存在于 bridge jar 里，否则同一个类会被两个加载器各定义一次。
$legacySources = @($allSources | Where-Object {
        $_ -notmatch 'SkinBridgeModern\.java$' -and $_ -notmatch 'SkinBridgeLegacy\.java$' -and
        $_ -notmatch 'SkinPortBridge\.java$' -and $_ -notmatch 'SkinModuleSupport\.java$' -and
        $_ -notmatch 'AgentStub\.java$'
    })
$implSources = @($allSources | Where-Object {
        $_ -notmatch 'SkinBridgeModern\.java$' -and $_ -notmatch 'SkinBridgeLegacy\.java$' -and
        $_ -notmatch 'SkinPortBridge\.java$' -and $_ -notmatch 'AgentStub\.java$'
    })
$stubSources = @((Join-Path $src 'nmpc\skin\AgentStub.java'))

# 1) legacy impl: Java 8 runtime (1.7.10~1.15)，含 ASM
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

# 2) modern impl: Java 17+，含 ASM，靠 AgentStub 的子加载器加载
$implClasses = Join-Path $out 'agent-classes'
Remove-Item $implClasses -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $implClasses -Force | Out-Null
$modernCp = @(
    (Join-Path $Root '.data\libraries\com\mojang\authlib\7.0.63\authlib-7.0.63.jar')
    (Join-Path $Root '.data\libraries\com\google\guava\guava\33.3.1-jre\guava-33.3.1-jre.jar')
    $asm
) -join ';'
$null = & $javac --release 17 -nowarn -encoding UTF-8 -cp $modernCp -d $implClasses @implSources 2>&1
if ($LASTEXITCODE -ne 0) { throw 'javac failed: modern agent' }
Add-Asm -Classes $implClasses

# 3) stub: Java 8 字节码，只有 AgentStub 一个类，绝不带 ASM
$stubClasses = Join-Path $out 'agent-stub-classes'
Remove-Item $stubClasses -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stubClasses -Force | Out-Null
$null = & $javac --release 8 -nowarn -encoding UTF-8 -d $stubClasses @stubSources 2>&1
if ($LASTEXITCODE -ne 0) { throw 'javac failed: agent stub' }

# 4) bridge jar: only what the transformed game code calls, reflection-only
$bridgeClasses = Join-Path $out 'bridge-classes'
Remove-Item $bridgeClasses -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $bridgeClasses -Force | Out-Null
$null = & $javac --release 8 -nowarn -encoding UTF-8 -d $bridgeClasses @bridgeSources 2>&1
if ($LASTEXITCODE -ne 0) { throw 'javac failed: bridge' }

$ErrorActionPreference = $nativePreference

Write-Jar -Name 'nmpc-skin-agent-impl-legacy' -Classes $legacyClasses -Premain ''
Write-Jar -Name 'nmpc-skin-agent-impl' -Classes $implClasses -Premain ''
Write-Jar -Name 'nmpc-skin-agent' -Classes $stubClasses -Premain 'nmpc.skin.AgentStub'

@('Manifest-Version: 1.0') | Set-Content (Join-Path $out 'nmpc-skin-bridge-MANIFEST.MF') -Encoding ASCII
$bridgeTarget = Join-Path $out 'nmpc-skin-bridge.jar'
& $jarTool --create --file $bridgeTarget --manifest (Join-Path $out 'nmpc-skin-bridge-MANIFEST.MF') -C $bridgeClasses .
if ($LASTEXITCODE -ne 0) { throw 'jar failed: bridge' }
Write-Host "jar: $bridgeTarget"

# 资源目录是启动器编译时嵌进程序集的那一份，构建完直接覆盖，避免"忘了复制"。
New-Item -ItemType Directory -Path $resourceDir -Force | Out-Null
foreach ($name in @('nmpc-skin-agent.jar', 'nmpc-skin-agent-impl.jar', 'nmpc-skin-agent-impl-legacy.jar', 'nmpc-skin-bridge.jar')) {
    Copy-Item (Join-Path $out $name) (Join-Path $resourceDir $name) -Force
    Write-Host "copy: $(Join-Path $resourceDir $name)"
}
