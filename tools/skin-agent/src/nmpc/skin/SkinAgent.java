package nmpc.skin;

import java.io.IOException;
import java.lang.instrument.Instrumentation;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;

/**
 * NPMC 离线皮肤 agent（原型：1.20.1 / authlib 4.x）。
 *
 * 启动时：
 *   1. 在 127.0.0.1 上起一个只服务"当前账号"的皮肤 API；
 *   2. 把 minecraft.api.* 四个 host 指到本地（authlib 4/6/7 会读）；
 *   3. 用 ASM 给 SkinManager 的 getSkinTextures(GameProfile) 注入一次
 *      "properties 里没有 textures 就向本地 API 要"。
 *
 * 参数格式（分号分隔）：
 *   port=8765;name=Steve;uuid=5627...;skin=C:\path\skin.png;model=classic;log=C:\path\agent.log
 */
public final class SkinAgent {
    static int port = 8765;
    static String name = "Player";
    static String uuid = "00000000000000000000000000000000";
    static Path skin;
    static String model = "classic";
    static Path logFile;
    private static String skinId;

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
        // 如果把 services 也指向本地，聊天/Realms/在线功能会被判为不可用。
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

    /** 极简 GET（只访问本地回环，失败返回 null）。 */
    static String get(String url) {
        try {
            java.net.HttpURLConnection connection =
                    (java.net.HttpURLConnection) java.net.URI.create(url).toURL().openConnection();
            connection.setConnectTimeout(1000);
            connection.setReadTimeout(3000);
            connection.setRequestMethod("GET");

            if (connection.getResponseCode() != 200) return null;

            try (java.io.InputStream stream = connection.getInputStream()) {
                return new String(stream.readAllBytes(), StandardCharsets.UTF_8);
            }
        } catch (Throwable error) {
            log("GET " + url + " failed: " + error);
            return null;
        }
    }

    static void log(String message) {
        String line = "[" + new java.util.Date() + "] " + message + System.lineSeparator();

        try {
            if (logFile != null) {
                Files.writeString(logFile, line, StandardCharsets.UTF_8,
                        StandardOpenOption.CREATE, StandardOpenOption.APPEND);
            }
        } catch (Throwable ignored) {
            // 日志写不了不影响游戏
        }
    }

    private static void parse(String args) {
        if (args == null || args.isBlank()) return;

        for (String part : args.split(";")) {
            int eq = part.indexOf('=');
            if (eq <= 0) continue;

            String key = part.substring(0, eq).trim();
            String value = part.substring(eq + 1).trim();

            switch (key) {
                case "port" -> {
                    try {
                        port = Integer.parseInt(value);
                    } catch (NumberFormatException ignored) {
                    }
                }
                case "name" -> name = value;
                case "uuid" -> uuid = value.replace("-", "");
                case "skin" -> {
                    if (!value.isEmpty()) skin = Paths.get(value);
                }
                case "model" -> model = value.equals("slim") ? "slim" : "classic";
                case "log" -> {
                    if (!value.isEmpty()) logFile = Paths.get(value);
                }
                default -> {
                }
            }
        }
    }

    private SkinAgent() {
    }
}
