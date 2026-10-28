package nmpc.skin;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.UUID;

/**
 * 1.7.10 常见整合包装了 SkinPort（lain.mods.skinport）。
 *
 * SkinPort 有自己的 ASM 钩子和皮肤提供者链；离线账号在它的链里找不到纹理时，
 * 会落到 DefaultSkinProvider，而它是按 UUID.hashCode() & 1 选 DefaultAlex / DefaultSteve。
 * 账号 12 恰好命中 Alex，于是启动器的自定义皮肤被完全盖掉。
 *
 * 这里不依赖编译期 SkinPort，全部反射：
 *   - 本账号：构造 lain.mods.skins.impl.SkinData 并塞入启动器的 PNG；
 *   - 其它玩家：按 SkinPort 自己的规则返回 DefaultAlex / DefaultSteve。
 *
 * 这个类在**桥接 jar** 里：Java 8 时桥接 jar 进系统类加载器，Java 17+ 时进 bootstrap。
 * 所以不能引用 SkinAgent 的字段（那是 impl 子加载器里的另一份），配置一律读系统属性：
 *   nmpc.skin.uuid / nmpc.skin.name / nmpc.skin.skin / nmpc.skin.model / nmpc.skin.log
 */
public final class SkinPortBridge {
    private static final String SKIN_DATA = "lain.mods.skins.impl.SkinData";

    public static Object get(Object provider, Object profile) {
        try {
            String id = playerId(profile);
            String playerName = playerName(profile);

            log("get id=" + id + " name=" + playerName);

            boolean local = (id != null && id.replace("-", "").equalsIgnoreCase(property("nmpc.skin.uuid", "")))
                    || (playerName != null && playerName.equalsIgnoreCase(property("nmpc.skin.name", "")));

            if (local) {
                Object skin = createSkin();
                if (skin != null) return skin;
            }

            return fallback(provider, id);
        } catch (Throwable error) {
            log("get failed: " + error);
            return null;
        }
    }

    private static Object createSkin() {
        try {
            String configured = property("nmpc.skin.skin", "");
            if (configured.isEmpty()) return null;

            Path skin = Paths.get(configured);
            if (!Files.isReadable(skin)) return null;

            byte[] bytes = Files.readAllBytes(skin);

            Class<?> type = Class.forName(SKIN_DATA);

            Constructor<?> constructor = type.getDeclaredConstructor();
            constructor.setAccessible(true);
            Object data = constructor.newInstance();

            Method judge = type.getDeclaredMethod("judgeSkinType", byte[].class);
            judge.setAccessible(true);
            String skinType = (String) judge.invoke(null, (Object) bytes);

            Method put = type.getDeclaredMethod("put", byte[].class, String.class);
            put.setAccessible(true);
            put.invoke(data, bytes, skinType);

            log("skin served type=" + skinType + " model=" + property("nmpc.skin.model", "classic"));
            return data;
        } catch (Throwable error) {
            log("create failed: " + error);
            return null;
        }
    }

    private static Object fallback(Object provider, String id) {
        try {
            boolean alex = false;

            if (id != null) {
                try {
                    alex = (UUID.fromString(id).hashCode() & 1) == 1;
                } catch (Throwable ignored) {
                    // 非标准 UUID 就走 Steve
                }
            }

            Field field = provider.getClass().getDeclaredField(alex ? "DefaultAlex" : "DefaultSteve");
            field.setAccessible(true);
            return field.get(provider);
        } catch (Throwable error) {
            log("fallback failed: " + error);
            return null;
        }
    }

    private static String playerId(Object profile) {
        try {
            Method method = profile.getClass().getMethod("getPlayerID");
            Object value = method.invoke(profile);
            return value == null ? null : value.toString();
        } catch (Throwable ignored) {
            return null;
        }
    }

    private static String playerName(Object profile) {
        try {
            Method method = profile.getClass().getMethod("getPlayerName");
            Object value = method.invoke(profile);
            return value == null ? null : value.toString();
        } catch (Throwable ignored) {
            return null;
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

    private static void log(String message) {
        try {
            String path = System.getProperty("nmpc.skin.log", "");
            if (path.isEmpty()) return;

            Files.write(
                    Paths.get(path),
                    ("[" + new java.util.Date() + "] bridge skinport " + message + System.lineSeparator())
                            .getBytes(java.nio.charset.StandardCharsets.UTF_8),
                    java.nio.file.StandardOpenOption.CREATE,
                    java.nio.file.StandardOpenOption.APPEND);
        } catch (Throwable ignored) {
            // 日志写不了不影响游戏
        }
    }

    private SkinPortBridge() {
    }
}
