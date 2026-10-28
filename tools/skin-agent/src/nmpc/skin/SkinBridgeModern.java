package nmpc.skin;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.HashMap;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
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
     * 替换 SkinManager.get(GameProfile)：
     *   - 本地账号：把本地纹理注册进去（registerTextures）；
     *   - 其它玩家：调用被改名保留的原方法 nmpc$get$impl，保持多人游戏正常。
     *
     * 1.21.11 的 registerTextures 第一个参数是 **UUID**（不是 GameProfile），
     * 直接传 profile 会 `IllegalArgumentException: argument type mismatch`（实测），
     * 所以按声明参数类型转换。
     */
    public static CompletableFuture get(Object skinManager, Object profile) {
        try {
            String id = String.valueOf(invoke(profile, "id", "getId")).replace("-", "");
            String uuid = property("nmpc.skin.uuid", "");
            boolean ours = !id.isEmpty() && id.equalsIgnoreCase(uuid);

            log("get: id=" + id + " ours=" + ours);

            if (ours) {
                try {
                    CompletableFuture future = register(skinManager, profile);
                    if (future != null) return future;
                } catch (Throwable error) {
                    log("get: register failed: " + error);
                }
            }

            // 不是本地账号、或注册失败：交回原实现（默认皮肤 / 真实皮肤），别把别人也挡掉
            return original(skinManager, profile);
        } catch (Throwable error) {
            log("get failed: " + error);
            return CompletableFuture.completedFuture(Optional.empty());
        }
    }

    /** 调 SkinManager.registerTextures，按实际参数类型传 UUID 或 GameProfile。 */
    private static CompletableFuture register(Object skinManager, Object profile) throws Exception {
        Object textures = buildTextures(skinManager.getClass().getClassLoader());

        for (Method method : skinManager.getClass().getDeclaredMethods()) {
            if (!method.getName().equals("registerTextures") || method.getParameterCount() != 2) continue;

            method.setAccessible(true);

            Class<?>[] types = method.getParameterTypes();
            Object first = types[0] == UUID.class ? profileId(profile) : profile;

            Object result = method.invoke(skinManager, first, textures);
            if (result instanceof CompletableFuture) {
                return ((CompletableFuture) result).thenApply(Optional::of);
            }
        }

        return null;
    }

    /** 原 SkinManager.get：改写时被改名成 nmpc$get$impl 保留下来。 */
    private static CompletableFuture original(Object skinManager, Object profile) {
        try {
            for (Method method : skinManager.getClass().getDeclaredMethods()) {
                if (!method.getName().equals("nmpc$get$impl") || method.getParameterCount() != 1) continue;

                method.setAccessible(true);

                Object result = method.invoke(skinManager, profile);
                if (result instanceof CompletableFuture) return (CompletableFuture) result;
            }
        } catch (Throwable error) {
            log("original get failed: " + error);
        }

        return CompletableFuture.completedFuture(Optional.empty());
    }

    /** GameProfile 的 UUID（getId 返回值可能是 UUID，也可能是字符串）。 */
    private static UUID profileId(Object profile) {
        Object value = invoke(profile, "id", "getId");

        if (value instanceof UUID) return (UUID) value;

        try {
            return UUID.fromString(String.valueOf(value));
        } catch (Throwable malformed) {
            return null;
        }
    }

    /**
     * 由 getPackedTextures(GameProfile) 调用：本地账号合成一个带本地 URL 的 textures 属性。
     *
     * authlib 9/10 的 SkinManager 先 getPackedTextures 拿属性、再交给 unpackTextures 解码；
     * authlib 10（26.3）的环境只认 minecraft.api.discovery.host，没法再把 session 指到本地，
     * 所以这一步决定了 26.3 能不能拿到皮肤。其它玩家交回原实现。
     */
    public static Object packedTextures(Object service, Object profile) {
        try {
            String id = String.valueOf(invoke(profile, "id", "getId")).replace("-", "");
            String uuid = property("nmpc.skin.uuid", "");
            boolean ours = !id.isEmpty() && id.equalsIgnoreCase(uuid);

            log("packedTextures: id=" + id + " ours=" + ours);

            if (ours) {
                try {
                    Object property = buildProperty(service.getClass().getClassLoader());
                    if (property != null) return property;
                } catch (Throwable error) {
                    log("packedTextures: build failed: " + error);
                }
            }

            return originalPacked(service, profile);
        } catch (Throwable error) {
            log("packedTextures failed: " + error);
            return null;
        }
    }

    /** 合成 Property("textures", base64(JSON))，字段与本地 HTTP 接口返回的 textures 一致。 */
    private static Object buildProperty(ClassLoader loader) throws Exception {
        String url = property("nmpc.skin.url", "");
        if (url.isEmpty()) return null;

        StringBuilder json = new StringBuilder();
        json.append("{\"timestamp\":").append(System.currentTimeMillis())
                .append(",\"profileId\":\"").append(property("nmpc.skin.uuid", ""))
                .append("\",\"profileName\":\"").append(property("nmpc.skin.name", ""))
                .append("\",\"textures\":{\"SKIN\":{\"url\":\"").append(url).append("\"");
        if ("slim".equals(property("nmpc.skin.model", "classic")))
            json.append(",\"metadata\":{\"model\":\"slim\"}");
        json.append("}}}");

        String value = java.util.Base64.getEncoder()
                .encodeToString(json.toString().getBytes(StandardCharsets.UTF_8));

        Class<?> propertyClass = Class.forName("com.mojang.authlib.properties.Property", true, loader);
        return propertyClass.getConstructor(String.class, String.class).newInstance("textures", value);
    }

    /** 原 getPackedTextures：改写时被改名成 nmpc$getPackedTextures$impl 保留下来。 */
    private static Object originalPacked(Object service, Object profile) {
        try {
            for (Method method : service.getClass().getDeclaredMethods()) {
                if (!method.getName().equals("nmpc$getPackedTextures$impl") || method.getParameterCount() != 1) continue;

                method.setAccessible(true);
                return method.invoke(service, profile);
            }
        } catch (Throwable error) {
            log("original packedTextures failed: " + error);
        }

        return null;
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
