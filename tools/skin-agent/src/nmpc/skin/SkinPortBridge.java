package nmpc.skin;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.file.Files;
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
 */
public final class SkinPortBridge {
    private static final String SKIN_DATA = "lain.mods.skins.impl.SkinData";

    public static Object get(Object provider, Object profile) {
        try {
            String id = playerId(profile);
            String playerName = playerName(profile);

            SkinAgent.log("skinport get id=" + id + " name=" + playerName);

            boolean local = (id != null && id.replace("-", "").equalsIgnoreCase(SkinAgent.uuid))
                    || (playerName != null && playerName.equalsIgnoreCase(SkinAgent.name));

            if (local) {
                Object skin = createSkin();
                if (skin != null) return skin;
            }

            return fallback(provider, id);
        } catch (Throwable error) {
            SkinAgent.log("skinport get failed: " + error);
            return null;
        }
    }

    private static Object createSkin() {
        try {
            if (SkinAgent.skin == null || !Files.isReadable(SkinAgent.skin)) return null;

            byte[] bytes = Files.readAllBytes(SkinAgent.skin);

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

            SkinAgent.log("skinport skin served type=" + skinType + " model=" + SkinAgent.model);
            return data;
        } catch (Throwable error) {
            SkinAgent.log("skinport create failed: " + error);
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
            SkinAgent.log("skinport fallback failed: " + error);
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

    private SkinPortBridge() {
    }
}
