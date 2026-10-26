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

        log("ready name=" + name + " uuid=" + uuid + " skin=" + skin + " model=" + model);
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