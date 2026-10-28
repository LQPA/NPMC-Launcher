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
 *   2. 把 minecraft.api.* 四个 host 里只把 session 指到本地（authlib 4~7 会读）；
 *   3. 用 ASM 替换 authlib / SkinManager 的取皮肤入口（见 SkinTransformer）。
 *
 * 参数格式（分号分隔）：
 *   port=8765;name=Steve;uuid=5627...;skin=C:\path\skin.png;model=classic;log=C:\path\agent.log
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

    /**
     * 现代类加载环境（模块化加载器）是否已经准备好：bootstrap 追加 + 模块 read edge 都成功。
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

        // 只劫持 session（皮肤），auth/account/services 保持官方；
        // 四个属性必须同时存在，否则 authlib 会整体退回默认环境。
        // 1.7.10 的 authlib 不读这些属性，设置了也无害。
        System.setProperty("minecraft.api.auth.host", "https://authserver.mojang.com");
        System.setProperty("minecraft.api.account.host", "https://api.mojang.com");
        System.setProperty("minecraft.api.session.host", base());
        System.setProperty("minecraft.api.services.host", "https://api.minecraftservices.com");

        try {
            instrumentation.addTransformer(new SkinTransformer());
            log("transformer installed");
        } catch (Throwable error) {
            log("transformer failed: " + error);
        }

        // 现代加载器（Forge/NeoForge/Fabric 1.16+）把 authlib 放在模块层里，看不到系统类路径。
        // 把自己追加到 bootstrap，并给改写到的模块加 read edge（SkinModuleSupport 只在 Java 17+ 的 jar 里）。
        prepareBootstrap(instrumentation);

        try {
            System.setProperty("nmpc.skin.url", base() + "/textures/" + skinId() + ".png");
            System.setProperty("nmpc.skin.uuid", uuid);
            System.setProperty("nmpc.skin.model", model);
            System.setProperty("nmpc.skin.port", String.valueOf(port));
            if (logFile != null) System.setProperty("nmpc.skin.log", logFile.toString());
            log("bridge properties: url=" + System.getProperty("nmpc.skin.url"));
        } catch (Throwable error) {
            log("properties failed: " + error);
        }

        log("ready name=" + name + " uuid=" + uuid + " skin=" + skin + " model=" + model);
    }

    /** 把 agent jar 追加到 bootstrap，让模块化加载器也能解析 nmpc.skin.*。 */
    private static void prepareBootstrap(Instrumentation instrumentation) {
        // Java 8（1.7.10/1.12）没有模块系统，老加载器本来就能看到系统类路径，不必动 bootstrap
        String specification = System.getProperty("java.specification.version", "");
        if (specification.startsWith("1.")) {
            log("bootstrap: java " + specification + ", skipped");
            return;
        }

        try {
            java.security.CodeSource source = SkinAgent.class.getProtectionDomain().getCodeSource();
            if (source == null || source.getLocation() == null) {
                log("bootstrap: no code source");
                return;
            }

            java.io.File jar = new java.io.File(source.getLocation().toURI());
            if (!jar.isFile()) {
                log("bootstrap: not a jar: " + jar);
                return;
            }

            instrumentation.appendToBootstrapClassLoaderSearch(new java.util.jar.JarFile(jar));
            log("bootstrap search: " + jar);
        } catch (Throwable error) {
            log("bootstrap append failed: " + error);
            return;
        }

        try {
            Class<?> support = Class.forName("nmpc.skin.SkinModuleSupport", true, SkinAgent.class.getClassLoader());
            support.getMethod("install", Instrumentation.class).invoke(null, instrumentation);
            modernReady = true;
            log("module support installed");
        } catch (Throwable error) {
            // legacy jar 里没有这个类（Java 8 没有模块系统），忽略即可
            log("module support unavailable: " + error);
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
            } else if (key.equals("log")) {
                if (!value.isEmpty()) logFile = Paths.get(value);
            }
        }
    }

    private SkinAgent() {
    }
}