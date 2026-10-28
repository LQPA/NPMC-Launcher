package nmpc.skin;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Base64;

/**
 * 本地皮肤 API：只服务当前启动账号的 profile 与皮肤 PNG。
 *
 * 不用 com.sun.net.httpserver：它的 dispatcher 线程不是 daemon，
 * 即使游戏因为启动失败（比如 Fabric 的 classpath 校验）立刻退出，JVM 也会挂在那里不结束，
 * 启动器就会永远显示"运行中"。这里用 ServerSocket + daemon 线程实现同样几个路由，
 * 主线程结束后 JVM 能正常退出。
 *
 * 保持 Java 8 兼容（1.7.10 用 Java 8 运行）。
 */
final class SkinHttpServer {
    private static ServerSocket server;

    static void start(final int port) throws IOException {
        server = new ServerSocket(port, 16, InetAddress.getByName("127.0.0.1"));

        Thread acceptor = new Thread(new Runnable() {
            @Override
            public void run() {
                while (!server.isClosed()) {
                    final Socket socket;
                    try {
                        socket = server.accept();
                    } catch (IOException closed) {
                        return;
                    }

                    Thread worker = new Thread(new Runnable() {
                        @Override
                        public void run() {
                            try {
                                handle(socket);
                            } catch (Throwable ignored) {
                                // 单个请求失败不影响服务
                            } finally {
                                try {
                                    socket.close();
                                } catch (IOException ignored) {
                                }
                            }
                        }
                    }, "nmpc-skin-conn");

                    worker.setDaemon(true);
                    worker.start();
                }
            }
        }, "nmpc-skin-http");

        acceptor.setDaemon(true);
        acceptor.start();
    }

    static void stop() {
        try {
            if (server != null) server.close();
        } catch (IOException ignored) {
        }
    }

    private static void handle(Socket socket) throws IOException {
        InputStream input = socket.getInputStream();
        BufferedReader reader = new BufferedReader(new InputStreamReader(input, StandardCharsets.ISO_8859_1));

        String requestLine = reader.readLine();
        if (requestLine == null) return;

        // 读完请求头（不解析，保持连接语义简单）
        String line;
        while ((line = reader.readLine()) != null && !line.isEmpty()) {
            // 只消费
        }

        String[] parts = requestLine.split(" ");
        if (parts.length < 2) {
            respond(socket, 400, "text/plain", "bad request".getBytes(StandardCharsets.UTF_8));
            return;
        }

        String path = parts[1];
        int query = path.indexOf('?');
        if (query >= 0) path = path.substring(0, query);

        if (path.startsWith("/session/minecraft/profile/")) {
            String requestedId = path.substring(path.lastIndexOf('/') + 1);
            String id = requestedId.isEmpty() ? SkinAgent.uuid : requestedId;

            String textureUrl = SkinAgent.base() + "/textures/" + SkinAgent.skinId() + ".png";
            String textures = "{\"timestamp\":" + System.currentTimeMillis()
                    + ",\"profileId\":\"" + id + "\""
                    + ",\"profileName\":\"" + SkinAgent.name + "\""
                    + ",\"textures\":{\"SKIN\":{\"url\":\"" + textureUrl + "\""
                    + (SkinAgent.model.equals("slim") ? ",\"metadata\":{\"model\":\"slim\"}" : "")
                    + "}}}";

            String value = Base64.getEncoder().encodeToString(textures.getBytes(StandardCharsets.UTF_8));
            String json = "{\"id\":\"" + id + "\",\"name\":\"" + SkinAgent.name + "\""
                    + ",\"properties\":[{\"name\":\"textures\",\"value\":\"" + value + "\"}]}";

            SkinAgent.log("profile " + id + " -> " + SkinAgent.skin);
            respond(socket, 200, "application/json", json.getBytes(StandardCharsets.UTF_8));
            return;
        }

        if (path.startsWith("/textures/")) {
            Path skin = SkinAgent.skin;
            if (skin == null || !Files.isReadable(skin)) {
                respond(socket, 404, "text/plain", "no skin".getBytes(StandardCharsets.UTF_8));
                return;
            }

            byte[] data = Files.readAllBytes(skin);
            SkinAgent.log("texture " + skin + " (" + data.length + " bytes)");
            respond(socket, 200, "image/png", data);
            return;
        }

        // SkinPort 的 CustomServer2 会按 /skins/%auto% 取当前玩家皮肤。
        // 只回本账号，其它 UUID 一律 404，让 SkinPort 继续走 Mojang/Crafatar。
        if (path.startsWith("/skins/")) {
            String requested = path.substring(path.lastIndexOf('/') + 1);
            String normalized = requested.replace("-", "");

            boolean local = normalized.equalsIgnoreCase(SkinAgent.uuid)
                    || requested.equalsIgnoreCase(SkinAgent.name);

            if (!local) {
                SkinAgent.log("skinport request " + requested + " -> 404 (not local)");
                respond(socket, 404, "text/plain", "unknown player".getBytes(StandardCharsets.UTF_8));
                return;
            }

            Path skin = SkinAgent.skin;
            if (skin == null || !Files.isReadable(skin)) {
                respond(socket, 404, "text/plain", "no skin".getBytes(StandardCharsets.UTF_8));
                return;
            }

            byte[] data = Files.readAllBytes(skin);
            SkinAgent.log("skinport request " + requested + " -> local skin (" + data.length + " bytes)");
            respond(socket, 200, "image/png", data);
            return;
        }

        if (path.equals("/player/attributes")) {
            respond(socket, 200, "application/json", "{}".getBytes(StandardCharsets.UTF_8));
            return;
        }

        if (path.equals("/publickeys")) {
            respond(socket, 200, "application/json",
                    "{\"profilePropertyKeys\":[],\"playerCertificateKeys\":[]}".getBytes(StandardCharsets.UTF_8));
            return;
        }

        respond(socket, 404, "text/plain", "not found".getBytes(StandardCharsets.UTF_8));
    }

    private static void respond(Socket socket, int status, String type, byte[] body) throws IOException {
        OutputStream output = socket.getOutputStream();

        StringBuilder head = new StringBuilder();
        head.append("HTTP/1.1 ").append(status).append(status == 200 ? " OK" : " Not Found").append("\r\n");
        head.append("Content-Type: ").append(type).append("\r\n");
        head.append("Content-Length: ").append(body.length).append("\r\n");
        head.append("Connection: close\r\n\r\n");

        output.write(head.toString().getBytes(StandardCharsets.ISO_8859_1));
        output.write(body);
        output.flush();
    }

    private SkinHttpServer() {
    }
}
