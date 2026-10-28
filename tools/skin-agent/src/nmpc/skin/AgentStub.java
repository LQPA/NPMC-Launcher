package nmpc.skin;

import java.io.File;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Method;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;

/**
 * -javaagent 的入口，**故意做得很薄**：这个 jar 里除了本类什么都没有。
 *
 * 为什么要拆成两个 jar：
 *   - JVM 会把 -javaagent 的 jar 放进**系统类加载器**的搜索路径，
 *     所以它的资源对 ClassLoader.getSystemClassLoader().getResources(...) 可见；
 *   - Fabric 的 LoaderUtil.verifyClasspath 正是数系统类加载器里
 *     org/objectweb/asm/ClassReader.class 的份数，发现两份就 abort（实测 1.21.11）；
 *   - 因此带 ASM 的实现不能放在 -javaagent 的 jar 里，只能由这里的子加载器加载：
 *     ASM 只在子加载器里定义一次，系统类路径和 bootstrap 都看不到它。
 *
 * 启动器传 impl=<nmpc-skin-agent-impl.jar>；没传时找自己旁边的同名文件。
 * 任何失败都只写日志、不抛异常：最差是没有离线皮肤，绝不能让游戏起不来。
 *
 * 注意：本类要保持 Java 8 兼容。
 */
public final class AgentStub {
    /** 必须一直持有：否则子加载器连同 impl 类可能被回收。 */
    private static ClassLoader implLoader;

    public static void premain(String arguments, Instrumentation instrumentation) {
        File impl = locateImpl(arguments);

        if (impl == null || !impl.isFile()) {
            log(arguments, "stub: impl jar not found (" + impl + ")");
            return;
        }

        try {
            implLoader = new ImplClassLoader(new URL[] { impl.toURI().toURL() });

            Class<?> agent = Class.forName("nmpc.skin.SkinAgent", true, implLoader);
            Method premain = agent.getMethod("premain", String.class, Instrumentation.class);

            premain.invoke(null, arguments, instrumentation);
        } catch (Throwable error) {
            Throwable cause = error;
            while (cause.getCause() != null && cause.getCause() != cause) cause = cause.getCause();

            StringBuilder builder = new StringBuilder("stub: agent failed: ").append(cause);
            StackTraceElement[] frames = cause.getStackTrace();
            int limit = frames.length < 20 ? frames.length : 20;
            for (int i = 0; i < limit; i++) builder.append("\n  at ").append(frames[i]);

            log(arguments, builder.toString());
        }
    }

    /**
     * 子加载器：ASM 一律从 impl jar 取（child-first）。
     * 1.7.10 的 LaunchWrapper 自带 ASM 5，NeoForge 自带 ASM 9；混用两边的 API 常量会
     * 直接 IllegalArgumentException，所以带 ASM 的类必须和 ASM 来自同一个 jar。
     */
    private static final class ImplClassLoader extends URLClassLoader {
        ImplClassLoader(URL[] urls) {
            super(urls, ClassLoader.getSystemClassLoader());
        }

        @Override
        protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
            if (!name.startsWith("org.objectweb.asm.")) return super.loadClass(name, resolve);

            synchronized (getClassLoadingLock(name)) {
                Class<?> type = findLoadedClass(name);
                if (type == null) {
                    try {
                        type = findClass(name);
                    } catch (ClassNotFoundException ignored) {
                        type = super.loadClass(name, false);
                    }
                }

                if (resolve) resolveClass(type);
                return type;
            }
        }
    }

    /** impl 路径：优先 impl= 参数，否则取 agent jar 同目录的 nmpc-skin-agent-impl.jar。 */
    private static File locateImpl(String arguments) {
        String configured = value(arguments, "impl");
        if (configured != null && !configured.isEmpty()) {
            File file = new File(configured);
            if (file.isFile()) return file;
        }

        try {
            java.security.CodeSource source = AgentStub.class.getProtectionDomain().getCodeSource();
            if (source == null || source.getLocation() == null) return null;

            File self = new File(source.getLocation().toURI());
            File parent = self.getParentFile();
            return parent == null ? null : new File(parent, "nmpc-skin-agent-impl.jar");
        } catch (Throwable error) {
            log(arguments, "stub: locate impl failed: " + error);
            return null;
        }
    }

    private static String value(String arguments, String key) {
        if (arguments == null) return null;

        String[] parts = arguments.split(";");
        for (int i = 0; i < parts.length; i++) {
            String part = parts[i];
            int eq = part.indexOf('=');
            if (eq <= 0) continue;

            if (key.equals(part.substring(0, eq).trim())) return part.substring(eq + 1).trim();
        }

        return null;
    }

    /** 只写启动器传进来的 log= 文件；写不了也不要影响游戏。 */
    private static void log(String arguments, String message) {
        String line = "[" + new java.util.Date() + "] " + message + System.lineSeparator();

        String target = value(arguments, "log");
        if (target == null || target.isEmpty()) {
            System.err.print(line);
            return;
        }

        try {
            Path path = Paths.get(target);
            Files.write(path, line.getBytes(StandardCharsets.UTF_8),
                    StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        } catch (Throwable ignored) {
            System.err.print(line);
        }
    }

    private AgentStub() {
    }
}
