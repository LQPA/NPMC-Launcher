package nmpc.skin;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.lang.instrument.Instrumentation;
import java.net.HttpURLConnection;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;

/**
 * NPMC 离线皮肤 agent。
 *
 * 启动时：
 *   1. 在 127.0.0.1 上起一个只服务"当前账号"的皮肤 API；
 *   2. 设置 minecraft.api.* 的 host（session 指本地；authlib 7 要求 session/services/profiles 全有）；
 *   3. 用 ASM 替换 authlib / SkinManager 的取皮肤入口（见 SkinTransformer）。
 *
 * 本类在 impl jar 里（带 ASM），由 AgentStub 用子加载器加载；桥接类单独在 bridge jar。
 *
 * 参数格式（分号分隔）：
 *   impl=...;port=8765;name=Steve;uuid=5627...;skin=C:\path\skin.png;model=classic;log=...;bridge=...
 *
 * 注意：本类要保持 Java 8 兼容（1.7.10 用 Java 8 运行），不要用 var / switch 箭头等新语法。
 */
public final class SkinAgent {
    static int port = 8765;
    static String name = "Player";
    static String uuid = "00000000000000000000000000000000";
    static Path skin;
    static String model = "classic";
    static Path logFile;

    /** 启动器传进来的桥接 jar 路径：bootstrap 只需要它，不需要 agent 自带的 ASM。 */
    static String bridgePath;

    /**
     * 桥接类是否已经能被被改写的游戏类解析到（Java 8：系统类加载器；Java 17+：bootstrap + read edge）。
     * 没准备好时**不做**现代替换 —— 否则注入的调用找不到类，游戏会直接崩；
     * 不替换最多只是没有离线皮肤（authlib 会把未签名纹理当无效，退回默认皮肤）。
     */
    static boolean modernReady;
    private static String skinId;

    public static void premain(String args, Instrumentation instrumentation) {
        parse(args);

        try {
            SkinHttpServer.start(port);
            log("skin server on " + base());
        } catch (Throwable error) {
            log("skin server failed: " + error);
        }

        // 只劫持 session（皮肤），auth/account/services/profiles 保持官方。
        // 1.7.10 的 authlib 不读这些属性，设置了也无害。
        System.setProperty("minecraft.api.auth.host", "https://authserver.mojang.com");
        System.setProperty("minecraft.api.account.host", "https://api.mojang.com");
        System.setProperty("minecraft.api.session.host", base());
        System.setProperty("minecraft.api.services.host", "https://api.minecraftservices.com");
        // authlib 6（1.21.1）只读 session + services；authlib 7（1.21.11+）的
        // EnvironmentParser 要求 session / services / profiles **三个全有**，
        // 缺一个就整体忽略（日志：Ignoring hosts properties），于是客户端仍然去
        // sessionserver.mojang.com 查离线账号的 profile —— 拿不到 textures 属性，
        // 皮肤也就永远不生效（1.21.11 Fabric 实测）。
        System.setProperty("minecraft.api.profiles.host", "https://api.mojang.com");

        try {
            instrumentation.addTransformer(new SkinTransformer());
            log("transformer installed");
        } catch (Throwable error) {
            log("transformer failed: " + error);
        }

        // 现代加载器（Forge/NeoForge/Fabric 1.16+）把 authlib 放在模块层里，看不到系统类路径；
        // Java 8 没有模块系统，被改写的游戏类由 LaunchWrapper / Fabric 的加载器加载，
        // 它们的父加载器是系统类加载器。两种情况都只需要桥接 jar，见 prepareBridge。
        prepareBridge(instrumentation);

        try {
            System.setProperty("nmpc.skin.url", base() + "/textures/" + skinId() + ".png");
            System.setProperty("nmpc.skin.uuid", uuid);
            System.setProperty("nmpc.skin.name", name);
            System.setProperty("nmpc.skin.model", model);
            System.setProperty("nmpc.skin.port", String.valueOf(port));
            System.setProperty("nmpc.skin.skin", skin == null ? "" : skin.toString());
            if (logFile != null) System.setProperty("nmpc.skin.log", logFile.toString());
            log("bridge properties: url=" + System.getProperty("nmpc.skin.url"));
        } catch (Throwable error) {
            log("properties failed: " + error);
        }

        log("ready name=" + name + " uuid=" + uuid + " skin=" + skin + " model=" + model);
    }

    /**
     * 把**桥接 jar** 挂到被改写的游戏类看得见的地方：一律挂 **bootstrap**。
     *
     * 不能只挂系统类加载器：Fabric 的 KnotClassLoader 不接受"只出现在系统类路径搜索里"的类，
     * 会报 `ClassNotFoundException: ... hasn't been exposed to the game`（1.15 Fabric 实测）。
     * bootstrap 里的类对所有加载器（含 Knot / LaunchWrapper / 模块层）都可见，
     * 而桥接类只依赖 JDK + 反射，放 bootstrap 没有副作用。
     *
     * Java 17+ 的模块化加载器还需要一条 read edge（SkinModuleSupport）；Java 8 没有模块系统。
     * agent 自己的 ASM 必须留在 impl 子加载器里：bootstrap 上出现第二份 ASM 会触发
     * VerifyError 或 Fabric 的 duplicate-ASM 检查。
     */
    private static void prepareBridge(Instrumentation instrumentation) {
        java.io.File jar = BridgeJar();
        if (jar == null || !jar.isFile()) {
            log("bridge: jar missing");
            return;
        }

        String specification = System.getProperty("java.specification.version", "");
        boolean legacy = specification.startsWith("1.");

        try {
            instrumentation.appendToBootstrapClassLoaderSearch(new java.util.jar.JarFile(jar));
            log("bootstrap search: " + jar);
        } catch (Throwable error) {
            log("bridge append failed: " + error);
            return;
        }

        if (legacy) return;

        try {
            Class<?> support = Class.forName("nmpc.skin.SkinModuleSupport", true, SkinAgent.class.getClassLoader());
            support.getMethod("install", Instrumentation.class).invoke(null, instrumentation);
            modernReady = true;
            log("module support installed");
        } catch (Throwable error) {
            // legacy impl 里没有这个类（Java 8 没有模块系统），忽略即可
            log("module support unavailable: " + error);
        }
    }

    /** 桥接 jar：优先用启动器传的 bridge= 参数，否则找 agent jar 同目录下的同名文件。 */
    private static java.io.File BridgeJar() {
        if (bridgePath != null && !bridgePath.isEmpty()) return new java.io.File(bridgePath);

        try {
            java.security.CodeSource source = SkinAgent.class.getProtectionDomain().getCodeSource();
            if (source == null || source.getLocation() == null) return null;

            java.io.File self = new java.io.File(source.getLocation().toURI());
            java.io.File parent = self.getParentFile();
            return parent == null ? null : new java.io.File(parent, "nmpc-skin-bridge.jar");
        } catch (Throwable error) {
            log("bridge path failed: " + error);
            return null;
        }
    }

    static String base() {
        return "http://127.0.0.1:" + port;
    }

    /**
     * 客户端用"皮肤 URL 的文件名"当缓存 key（MinecraftProfileTexture.getHash）。
     * 固定叫 skin.png 会命中上一次的缓存，表现就是"模型换了、贴图没换"；
     * 所以用文件内容的 SHA-1 做文件名。
     */
    static String skinId() {
        if (skinId != null) return skinId;

        try {
            if (skin != null && Files.isReadable(skin)) {
                byte[] digest = java.security.MessageDigest.getInstance("SHA-1")
                        .digest(Files.readAllBytes(skin));
                StringBuilder builder = new StringBuilder();
                for (byte b : digest) builder.append(String.format("%02x", b));
                skinId = builder.toString();
                return skinId;
            }
        } catch (Throwable error) {
            log("skin hash failed: " + error);
        }

        skinId = "skin";
        return skinId;
    }

    static void log(String message) {
        String line = "[" + new java.util.Date() + "] " + message + System.lineSeparator();

        try {
            if (logFile != null) {
                Files.write(logFile, line.getBytes(StandardCharsets.UTF_8),
                        StandardOpenOption.CREATE, StandardOpenOption.APPEND);
            }
        } catch (Throwable ignored) {
            // 日志写不了不影响游戏
        }
    }

    /** 极简 GET（只访问本地回环，失败返回 null）。 */
    static String get(String url) {
        try {
            HttpURLConnection connection = (HttpURLConnection) URI.create(url).toURL().openConnection();
            connection.setConnectTimeout(1000);
            connection.setReadTimeout(3000);
            connection.setRequestMethod("GET");

            if (connection.getResponseCode() != 200) return null;

            InputStream stream = connection.getInputStream();
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            byte[] buffer = new byte[4096];
            int read;
            while ((read = stream.read(buffer)) != -1) output.write(buffer, 0, read);
            stream.close();

            return new String(output.toByteArray(), StandardCharsets.UTF_8);
        } catch (Throwable error) {
            log("GET " + url + " failed: " + error);
            return null;
        }
    }

    private static void parse(String args) {
        if (args == null || args.trim().isEmpty()) return;

        String[] parts = args.split(";");

        for (String part : parts) {
            int eq = part.indexOf('=');
            if (eq <= 0) continue;

            String key = part.substring(0, eq).trim();
            String value = part.substring(eq + 1).trim();

            if (key.equals("port")) {
                try {
                    port = Integer.parseInt(value);
                } catch (NumberFormatException ignored) {
                }
            } else if (key.equals("name")) {
                name = value;
            } else if (key.equals("uuid")) {
                uuid = value.replace("-", "");
            } else if (key.equals("skin")) {
                if (!value.isEmpty()) skin = Paths.get(value);
            } else if (key.equals("model")) {
                model = value.equals("slim") ? "slim" : "classic";
            } else if (key.equals("bridge")) {
                bridgePath = value;
            } else if (key.equals("log")) {
                if (!value.isEmpty()) logFile = Paths.get(value);
            }
        }
    }

    private SkinAgent() {
    }
}