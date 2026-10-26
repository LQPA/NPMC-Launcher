package nmpc.skin;

import java.nio.file.Files;
import java.util.HashMap;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;

import com.mojang.authlib.SignatureState;
import com.mojang.authlib.minecraft.MinecraftProfileTexture;
import com.mojang.authlib.minecraft.MinecraftProfileTextures;

/**
 * authlib 6/7 的皮肤入口（26.1.2 / 1.21.6）。
 *
 * 这一代 SkinManager 只在 profile 自带 textures 属性时才继续；
 * 离线 profile 没有属性，所以直接替换 SkinManager.get(profile)，
 * 让它调用自己的 registerTextures 去注册我们提供的本地纹理。
 */
public final class SkinBridgeModern {
    public static MinecraftProfileTextures textures() {
        try {
            if (SkinAgent.skin == null || !Files.isReadable(SkinAgent.skin)) {
                SkinAgent.log("modern: no readable skin file");
                return MinecraftProfileTextures.EMPTY;
            }

            Map<String, String> metadata = new HashMap<>();
            if ("slim".equals(SkinAgent.model)) metadata.put("model", "slim");

            String url = SkinAgent.base() + "/textures/" + SkinAgent.skinId() + ".png";

            MinecraftProfileTexture skin = new MinecraftProfileTexture(url, metadata);
            SkinAgent.log("modern textures served url=" + url + " model=" + SkinAgent.model);

            return new MinecraftProfileTextures(skin, null, null, SignatureState.UNSIGNED);
        } catch (Throwable error) {
            SkinAgent.log("modern textures failed: " + error);
            return MinecraftProfileTextures.EMPTY;
        }
    }

    /**
     * 替换 SkinManager.get(GameProfile)：本地账号直接注册本地纹理；
     * 其它玩家（多人）返回空，走各自的默认皮肤（离线原型够用）。
     */
    public static CompletableFuture<Optional<Object>> get(Object skinManager, Object profile) {
        try {
            var id = invoke(profile, "id", "getId");

            if (id == null || !id.toString().replace("-", "").equalsIgnoreCase(SkinAgent.uuid)) {
                return CompletableFuture.completedFuture(Optional.empty());
            }

            var textures = textures();

            for (var method : skinManager.getClass().getDeclaredMethods()) {
                if (!method.getName().equals("registerTextures") || method.getParameterCount() != 2) continue;

                method.setAccessible(true);

                var future = (CompletableFuture<?>) method.invoke(skinManager, id, textures);
                return future.thenApply(Optional::of);
            }
        } catch (Throwable error) {
            SkinAgent.log("modern get failed: " + error);
        }

        return CompletableFuture.completedFuture(Optional.empty());
    }

    private static Object invoke(Object target, String... names) {
        for (String name : names) {
            try {
                return target.getClass().getMethod(name).invoke(target);
            } catch (Throwable ignored) {
            }
        }

        return null;
    }

    private SkinBridgeModern() {
    }
}