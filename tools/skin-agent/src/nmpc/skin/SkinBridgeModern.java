package nmpc.skin;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.HashMap;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;

/**
 * authlib 6/7 的皮肤入口（1.21.x）。
 *
 * 关键约束：这个类会被追加到 **bootstrap** 类路径，让 ModLauncher 的模块化加载器也能看到它
 * （1.16+ 的 Forge/NeoForge/Fabric 里 authlib 在模块层，普通系统类路径对模块不可见）。
 * 因此：
 *   - 不能 import 任何 authlib / Minecraft 类型（bootstrap 看不到它们），全部用反射；
 *   - 不能引用 SkinAgent 的字段（bootstrap 里会有另一份 SkinAgent），配置一律读系统属性；
 *   - 用**调用方实例的 ClassLoader** 去解析 authlib 类型，保证和行为对象是同一份类。
 *
 * 系统属性（由 SkinAgent.premain 设置）：
 *   nmpc.skin.url / nmpc.skin.uuid / nmpc.skin.model / nmpc.skin.port
 */
public final class SkinBridgeModern {
    private SkinBridgeModern() {
    }

    /** 由 unpackTextures(Property) 调用：只接管我们自己的 textures 属性。 */
    public static Object texturesFor(Object property) {
        try {
            ClassLoader loader = property.getClass().getClassLoader();

            boolean ours = isOurProperty(property);
            log("texturesFor: ours=" + ours + " loader=" + loader);

            if (!ours) return emptyTextures(loader);

            return buildTextures(loader);
        } catch (Throwable error) {
            log("texturesFor failed: " + error);
            return null;
        }
    }

    /**
     * 替换 SkinManager.get(GameProfile)：本地账号直接注册本地纹理；
     * 其它玩家返回空，走各自的默认皮肤。
     */
    public static CompletableFuture get(Object skinManager, Object profile) {
        try {
            String id = String.valueOf(invoke(profile, "id", "getId")).replace("-", "");
            String uuid = property("nmpc.skin.uuid", "");

            log("get: id=" + id + " ours=" + id.equalsIgnoreCase(uuid));

            if (id.isEmpty() || !id.equalsIgnoreCase(uuid)) {
                return CompletableFuture.completedFuture(Optional.empty());
            }

            ClassLoader loader = skinManager.getClass().getClassLoader();
            Object textures = buildTextures(loader);

            for (Method method : skinManager.getClass().getDeclaredMethods()) {
                if (!method.getName().equals("registerTextures") || method.getParameterCount() != 2) continue;

                method.setAccessible(true);

                Object future = method.invoke(skinManager, profile, textures);
                if (future instanceof CompletableFuture) {
                    return ((CompletableFuture) future).thenApply(Optional::of);
                }
            }
        } catch (Throwable error) {
            log("get failed: " + error);
        }

        return CompletableFuture.completedFuture(Optional.empty());
    }

    /** 构建 MinecraftProfileTextures(skin, cape, elytra, SignatureState.UNSIGNED)。 */
    private static Object buildTextures(ClassLoader loader) throws Exception {
        String url = property("nmpc.skin.url", "");
        if (url.isEmpty()) return emptyTextures(loader);

        Class<?> texturesClass = Class.forName(
                "com.mojang.authlib.minecraft.MinecraftProfileTextures", true, loader);
        Class<?> textureClass = Class.forName(
                "com.mojang.authlib.minecraft.MinecraftProfileTexture", true, loader);
        Class<?> stateClass = Class.forName("com.mojang.authlib.SignatureState", true, loader);

        Map<String, String> metadata = new HashMap<>();
        if ("slim".equals(property("nmpc.skin.model", "classic"))) metadata.put("model", "slim");

        Constructor<?> textureConstructor = textureClass.getConstructor(String.class, Map.class);
        Object skin = textureConstructor.newInstance(url, metadata);

        Object unsigned = Enum.valueOf(stateClass.asSubclass(Enum.class), "UNSIGNED");

        Constructor<?> texturesConstructor = texturesClass.getConstructor(
                textureClass, textureClass, textureClass, stateClass);

        return texturesConstructor.newInstance(skin, null, null, unsigned);
    }

    private static Object emptyTextures(ClassLoader loader) {
        try {
            Class<?> texturesClass = Class.forName(
                    "com.mojang.authlib.minecraft.MinecraftProfileTextures", true, loader);

            Field empty = texturesClass.getField("EMPTY");
            return empty.get(null);
        } catch (Throwable error) {
            return null;
        }
    }

    /** textures 属性的 value 是 base64 JSON；里面带我们本地端口才算"我们自己的"。 */
    private static boolean isOurProperty(Object property) {
        String port = property("nmpc.skin.port", "");
        if (port.isEmpty()) return true;

        try {
            // authlib 6/7 的 Property 是 record：value()；老版本是 getValue()
            Object value = invoke(property, "value", "getValue");
            if (value == null) {
                try {
                    java.lang.reflect.Field field = property.getClass().getDeclaredField("value");
                    field.setAccessible(true);
                    value = field.get(property);
                } catch (Throwable ignored) {
                    // 没有这个字段就放弃
                }
            }

            if (value == null) {
                log("property check: no value accessor");
                return false;
            }

            String decoded;

            try {
                decoded = new String(Base64.getDecoder().decode(value.toString()), StandardCharsets.UTF_8);
            } catch (IllegalArgumentException notStandard) {
                decoded = new String(Base64.getMimeDecoder().decode(value.toString()), StandardCharsets.UTF_8);
            }

            String sample = decoded.length() > 120 ? decoded.substring(0, 120) : decoded;
            log("property check: port=" + port + " decoded=" + sample);

            return decoded.contains("127.0.0.1:" + port) || decoded.contains("localhost:" + port);
        } catch (Throwable error) {
            log("property check failed: " + error);
            return false;
        }
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
                    ("[" + new java.util.Date() + "] bridge " + message + System.lineSeparator())
                            .getBytes(StandardCharsets.UTF_8),
                    java.nio.file.StandardOpenOption.CREATE,
                    java.nio.file.StandardOpenOption.APPEND);
        } catch (Throwable ignored) {
            // 日志写不了不影响游戏
        }
    }
}
