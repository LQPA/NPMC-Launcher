package nmpc.skin;

import java.util.HashMap;
import java.util.Map;

import com.mojang.authlib.GameProfile;
import com.mojang.authlib.minecraft.MinecraftProfileTexture;

/**
 * 旧入口（authlib 1.x~4.x）：getTextures(GameProfile, boolean) → Map。
 * 只做一件事：返回本地皮肤纹理表，绕开 Mojang 的会话/签名校验。
 *
 * 这里不直接调用 GameProfile 的方法（authlib 4/7 的 API 名不同），
 * 只通过反射取名字，保证同一个类在 1.7.10 / 1.20.1 / 26.1.2 都能加载。
 */
public final class SkinBridge {
    /**
     * 只对当前启动账号返回本地皮肤；其他玩家返回 false，
     * 让 authlib 继续走原方法（正版会话查询）。
     */
    public static boolean isLocal(GameProfile profile) {
        if (profile == null) return false;

        try {
            Object name = invoke(profile, "getName", "name");
            if (name != null && name.toString().equalsIgnoreCase(SkinAgent.name)) return true;

            Object id = invoke(profile, "getId", "id");
            return id != null && id.toString().replace("-", "").equalsIgnoreCase(SkinAgent.uuid);
        } catch (Throwable ignored) {
            return false;
        }
    }

    public static Map<MinecraftProfileTexture.Type, MinecraftProfileTexture> textures(GameProfile profile) {
        Map<MinecraftProfileTexture.Type, MinecraftProfileTexture> map = new HashMap<MinecraftProfileTexture.Type, MinecraftProfileTexture>();

        try {
            if (SkinAgent.skin == null || !java.nio.file.Files.isReadable(SkinAgent.skin)) {
                SkinAgent.log("no readable skin file");
                return map;
            }

            Map<String, String> metadata = new HashMap<String, String>();
            if ("slim".equals(SkinAgent.model)) metadata.put("model", "slim");

            String url = SkinAgent.base() + "/textures/" + SkinAgent.skinId() + ".png";

            map.put(MinecraftProfileTexture.Type.SKIN,
                    new MinecraftProfileTexture(url, metadata));

            SkinAgent.log("textures served for " + name(profile) + " url=" + url + " model=" + SkinAgent.model);
        } catch (Throwable error) {
            SkinAgent.log("textures failed: " + error);
        }

        return map;
    }

    private static String name(GameProfile profile) {
        try {
            Object value = invoke(profile, "getName", "name");
            return String.valueOf(value);
        } catch (Throwable ignored) {
            return "?";
        }
    }

    private static Object invoke(Object target, String... names) {
        for (String name : names) {
            try {
                return target.getClass().getMethod(name).invoke(target);
            } catch (Throwable ignored) {
                // 尝试下一个相容的方法名
            }
        }

        return null;
    }

    private SkinBridge() {
    }
}