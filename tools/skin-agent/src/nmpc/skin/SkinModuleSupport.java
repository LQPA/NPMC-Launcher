package nmpc.skin;

import java.lang.Module;
import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.security.ProtectionDomain;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;

/**
 * 让模块化加载器（Forge/NeoForge/Fabric 1.16+ 的 ModLauncher）能链接到 bootstrap 里的桥接类。
 *
 * 光把 agent 追加到 bootstrap 只解决"找得到类"，命名模块还需要一条 read edge 才能
 * 解析 bootstrap 那个未命名模块里的类，否则会变成 IllegalAccessError。
 *
 * 只编译进 modern agent（Java 17），legacy（Java 8）没有模块系统，不需要这个类。
 */
public final class SkinModuleSupport {
    private static final Set<String> HOOK_CLASSES = Set.of(
            "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService",
            "net/minecraft/client/resources/SkinManager");

    private static final Set<Module> PATCHED = ConcurrentHashMap.newKeySet();

    public static void install(Instrumentation instrumentation) {
        Module bridgeModule = bridgeModule();
        if (bridgeModule == null) {
            SkinAgent.log("module support: bridge module not found (bootstrap append failed?)");
            return;
        }

        instrumentation.addTransformer(new ClassFileTransformer() {
            @Override
            public byte[] transform(Module module, ClassLoader loader, String className,
                                    Class<?> classBeingRedefined, ProtectionDomain domain, byte[] bytes) {
                if (module == null || !HOOK_CLASSES.contains(className)) return null;
                if (!PATCHED.add(module)) return null;

                try {
                    instrumentation.redefineModule(
                            module,
                            Set.of(bridgeModule),   // extraReads：让该模块能解析 bootstrap 里的桥接类
                            Map.of(),               // extraExports
                            Map.of(),               // extraOpens
                            Set.of(),               // extraUses
                            Map.of());              // extraProvides

                    SkinAgent.log("read edge added: " + module.getName() + " -> " + bridgeModule.getName());
                } catch (Throwable error) {
                    SkinAgent.log("redefineModule failed for " + module.getName() + ": " + error);
                }

                return null;
            }
        });
    }

    /** bootstrap 里那份桥接类所在的未命名模块。 */
    private static Module bridgeModule() {
        try {
            Class<?> bridge = Class.forName("nmpc.skin.SkinBridgeModern", false, null);
            return bridge.getModule();
        } catch (Throwable error) {
            SkinAgent.log("bridge lookup failed: " + error);
            return null;
        }
    }

    private SkinModuleSupport() {
    }
}
