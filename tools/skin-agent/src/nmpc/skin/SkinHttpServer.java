package nmpc.skin;

import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Base64;
import java.util.concurrent.Executors;

import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpHandler;
import com.sun.net.httpserver.HttpServer;

/** 本地皮肤 API：只服务当前启动账号的 profile 与皮肤 PNG。 */
final class SkinHttpServer {
    private static HttpServer server;

    static void start(int port) throws IOException {
        server = HttpServer.create(new InetSocketAddress("127.0.0.1", port), 0);
        server.createContext("/session/minecraft/profile/", new ProfileHandler());
        server.createContext("/textures/", new TextureHandler());
        server.createContext("/player/attributes", exchange -> reply(exchange, 200, "application/json", "{}"));
        server.createContext("/publickeys", exchange -> reply(exchange, 200, "application/json",
                "{\"profilePropertyKeys\":[],\"playerCertificateKeys\":[]}"));
        server.setExecutor(Executors.newSingleThreadExecutor(r -> {
            Thread t = new Thread(r, "nmpc-skin-http");
            t.setDaemon(true);
            return t;
        }));
        server.start();
    }

    static void stop() {
        if (server != null) server.stop(0);
    }

    private static final class ProfileHandler implements HttpHandler {
        @Override
        public void handle(HttpExchange exchange) throws IOException {
            String path = exchange.getRequestURI().getPath();
            String requestedId = path.substring(path.lastIndexOf('/') + 1);
            String id = requestedId.isEmpty() ? SkinAgent.uuid : requestedId;

            String textureUrl = SkinAgent.base() + "/textures/" + SkinAgent.skinId() + ".png";
            String textures = "{\"timestamp\":" + System.currentTimeMillis()
                    + ",\"profileId\":\"" + id + "\""
                    + ",\"profileName\":\"" + SkinAgent.name + "\""
                    + ",\"textures\":{\"SKIN\":{\"url\":\"" + textureUrl + "\""
                    + (SkinAgent.model.equals("slim") ? ",\"metadata\":{\"model\":\"slim\"}" : "")
                    + "}}}";

            String value = Base64.getEncoder().encodeToString(
                    textures.getBytes(StandardCharsets.UTF_8));

            String json = "{\"id\":\"" + id + "\",\"name\":\"" + SkinAgent.name + "\""
                    + ",\"properties\":[{\"name\":\"textures\",\"value\":\"" + value + "\"}]}";

            SkinAgent.log("profile " + id + " -> " + SkinAgent.skin);
            reply(exchange, 200, "application/json", json);
        }
    }

    private static final class TextureHandler implements HttpHandler {
        @Override
        public void handle(HttpExchange exchange) throws IOException {
            Path skin = SkinAgent.skin;
            if (skin == null || !Files.isReadable(skin)) {
                reply(exchange, 404, "text/plain", "no skin");
                return;
            }

            byte[] data = Files.readAllBytes(skin);
            SkinAgent.log("texture " + skin + " (" + data.length + " bytes)");
            exchange.getResponseHeaders().add("Content-Type", "image/png");
            exchange.sendResponseHeaders(200, data.length);
            try (OutputStream out = exchange.getResponseBody()) {
                out.write(data);
            }
        }
    }

    private static void reply(HttpExchange exchange, int status, String type, String body) throws IOException {
        byte[] data = body.getBytes(StandardCharsets.UTF_8);
        exchange.getResponseHeaders().add("Content-Type", type);
        exchange.sendResponseHeaders(status, data.length);
        try (OutputStream out = exchange.getResponseBody()) {
            out.write(data);
        }
    }

    private SkinHttpServer() {
    }
}
