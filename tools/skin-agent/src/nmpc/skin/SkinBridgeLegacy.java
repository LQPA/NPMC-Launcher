package nmpc.skin;

import java.util.HashMap;
import java.util.Map;

/**
 * authlib 1.x~5.x 的皮肤入口（1.7.10 ~ 1.20.1）：替换 getTextures(GameProfile, boolean)。
 *
 * 和 SkinBridgeModern 一样**不能 import authlib 类型**：1.16+ 的加载器把 authlib 放在模块层，
 * 只有 bootstrap 里的类能被解析，而 bootstrap 看不到 authlib。所以全部用反射，
 * ClassLoader 取自调用方 profile 的类。
 *
 * 需要保持 Java 8 兼容（1.7.10 用 Java 8 运行）。
 */
public final class SkinBridgeLegacy {
    private SkinBridgeLegacy() {
    }

    /** 只对当前启动账号返回本地皮肤，其他玩家继续走原方法。 */
    public static boolean isLocal(Object profile) {
        if (profile == null) return false;

        try {
            String name = property("nmpc.skin.name", "");
            String uuid = property("nmpc.skin.uuid", "");

            Object profileName = invoke(profile, "getName", "name");
            if (profileName != null && !name.isEmpty() && profileName.toString().equalsIgnoreCase(name)) return true;

            Object id = invoke(profile, "getId", "id");
            return id != null && !uuid.isEmpty()
                    && id.toString().replace("-", "").equalsIgnoreCase(uuid);
        } catch (Throwable error) {
            log("isLocal failed: " + error);
            return false;
        }
    }

    public static Map textures(Object profile) {
        Map<Object, Object> map = new HashMap<Object, Object>();

        try {
            String url = property("nmpc.skin.url", "");
            if (url.isEmpty() || profile == null) return map;

            ClassLoader loader = profile.getClass().getClassLoader();

            Class<?> typeClass = Class.forName(
                    "com.mojang.authlib.minecraft.MinecraftProfileTexture$Type", true, loader);
            Class<?> textureClass = Class.forName(
                    "com.mojang.authlib.minecraft.MinecraftProfileTexture", true, loader);

            Object skinType = Enum.valueOf(typeClass.asSubclass(Enum.class), "SKIN");

            Map<String, String> metadata = new HashMap<String, String>();
            if ("slim".equals(property("nmpc.skin.model", "classic"))) metadata.put("model", "slim");

            Object texture = textureClass.getConstructor(String.class, Map.class).newInstance(url, metadata);

            map.put(skinType, texture);
            log("legacy textures served url=" + url);
        } catch (Throwable error) {
            log("legacy textures failed: " + error);
        }

        return map;
    }

    private static String property(String key, String fallback) {
        try {
            String value = System.getProperty(key);
            return value == null || value.isEmpty() ? fallback : value;
        } catch (Throwable error) {
            return fallback;
        }
    }

    private static Object invoke(Object target, String... names) {
        for (String name : names) {
            try {
                return target.getClass().getMethod(name).invoke(target);
            } catch (Throwable ignored) {
                // 下一个名字
            }
        }

        return null;
    }

    private static void log(String message) {
        try {
            String path = System.getProperty("nmpc.skin.log", "");
            if (path.isEmpty()) return;

            java.nio.file.Files.write(
                    java.nio.file.Paths.get(path),
                    ("[" + new java.util.Date() + "] legacy " + message + System.lineSeparator())
                            .getBytes(java.nio.charset.StandardCharsets.UTF_8),
                    java.nio.file.StandardOpenOption.CREATE,
                    java.nio.file.StandardOpenOption.APPEND);
        } catch (Throwable ignored) {
            // 日志写不了不影响游戏
        }
    }
}
