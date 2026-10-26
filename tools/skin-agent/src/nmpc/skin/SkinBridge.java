package nmpc.skin;

import java.nio.charset.StandardCharsets;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import com.mojang.authlib.GameProfile;
import com.mojang.authlib.properties.Property;

/**
 * 从被注入的 SkinManager 里调用：如果 profile 没有 textures 属性，
 * 就同步向本地皮肤 API 要一次并写回 profile。
 *
 * 只在本地回环上同步请求，耗时几毫秒；这也是"离线客户端不主动查会话服务器"
 * 的补丁点（原版只有 fillProfileProperties / 社交界面才查）。
 */
public final class SkinBridge {
    private static final Pattern VALUE = Pattern.compile("\"value\"\\s*:\\s*\"([^\"]+)\"");

    public static void fill(GameProfile profile) {
        try {
            if (profile == null) return;
            if (profile.getProperties().containsKey("textures")) {
                SkinAgent.log("already has textures for " + profile.getName());
                return;
            }

            SkinAgent.log("fill for " + profile.getName() + " props=" + profile.getProperties().size());

            String id = profile.getId() == null
                    ? SkinAgent.uuid
                    : profile.getId().toString().replace("-", "");

            String json = SkinAgent.get(baseUrl() + "/session/minecraft/profile/" + id);
            if (json == null) {
                SkinAgent.log("profile fetch failed for " + id);
                return;
            }

            Matcher matcher = VALUE.matcher(json);
            if (!matcher.find()) {
                SkinAgent.log("no textures value in profile for " + id + " json=" + json);
                return;
            }

            profile.getProperties().put("textures", new Property("textures", matcher.group(1)));
            SkinAgent.log("textures injected for " + profile.getName() + " / " + id);
        } catch (Throwable error) {
            SkinAgent.log("bridge failed: " + error);
            java.io.StringWriter writer = new java.io.StringWriter();
            error.printStackTrace(new java.io.PrintWriter(writer));
            SkinAgent.log(writer.toString());
        }
    }

    private static String baseUrl() {
        return "http://127.0.0.1:" + SkinAgent.port;
    }

    /**
     * 直接返回皮肤纹理表：原版会话服务会去 Mojang 校验签名，离线伪造的纹理过不了；
     * 这里在 authlib 层把结果换掉，SkinManager 拿到就能按 URL 下载并应用。
     */
    public static java.util.Map<com.mojang.authlib.minecraft.MinecraftProfileTexture.Type,
            com.mojang.authlib.minecraft.MinecraftProfileTexture> textures(GameProfile profile) {
        java.util.Map<com.mojang.authlib.minecraft.MinecraftProfileTexture.Type,
                com.mojang.authlib.minecraft.MinecraftProfileTexture> map = new java.util.HashMap<>();

        try {
            if (SkinAgent.skin == null || !java.nio.file.Files.isReadable(SkinAgent.skin)) {
                SkinAgent.log("no readable skin file");
                return map;
            }

            java.util.Map<String, String> metadata = new java.util.HashMap<>();
            if ("slim".equals(SkinAgent.model)) metadata.put("model", "slim");

            String url = baseUrl() + "/textures/" + SkinAgent.skinId() + ".png";
            map.put(com.mojang.authlib.minecraft.MinecraftProfileTexture.Type.SKIN,
                    new com.mojang.authlib.minecraft.MinecraftProfileTexture(url, metadata));

            SkinAgent.log("textures served for " + (profile == null ? "?" : profile.getName())
                    + " url=" + url + " model=" + SkinAgent.model);
        } catch (Throwable error) {
            SkinAgent.log("textures failed: " + error);
        }

        return map;
    }

    private SkinBridge() {
    }
}
