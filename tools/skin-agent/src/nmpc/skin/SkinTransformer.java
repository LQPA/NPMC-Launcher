package nmpc.skin;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Label;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

/**
 * 替换"取皮肤"的入口，直接返回本地纹理（绕开 Mojang 签名校验）：
 *
 *   - authlib 1.5~4.x（1.7.10~1.20.1）：
 *       YggdrasilMinecraftSessionService.getTextures(GameProfile, boolean) → Map
 *   - authlib 6.x / 7.x（1.21.6 / 26.1.2）：
 *       YggdrasilMinecraftSessionService.unpackTextures(Property) → MinecraftProfileTextures
 *   - 26.1.2 的 SkinManager.get(profile) 在 profile 没有 textures 时直接返回空，
 *     所以整段替换成 SkinBridgeModern.get：本地账号调用它自己的 registerTextures。
 *
 * 保持 Java 8 语法（1.7.10 也加载这个类；现代入口在这些版本上不会命中）。
 */
final class SkinTransformer implements ClassFileTransformer {
    private static final String YGGDRASIL = "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService";
    private static final String SKIN_MANAGER = "net/minecraft/client/resources/SkinManager";

    /** 1.7.10 常见整合包的 SkinPort；它的默认皮肤会按 UUID 哈希选 Alex/Steve。 */
    private static final String SKINPORT_DEFAULT =
            "lain/mods/skinport/init/forge/ForgeSkinPort$DefaultSkinProvider";

    private static final String SKINPORT_GET_DESCRIPTOR =
            "(Llain/mods/skins/api/interfaces/IPlayerProfile;)Llain/mods/skins/api/interfaces/ISkin;";

    private static final String OLD_METHOD = "getTextures";
    private static final String OLD_DESCRIPTOR =
            "(Lcom/mojang/authlib/GameProfile;Z)Ljava/util/Map;";

    private static final String MODERN_METHOD = "unpackTextures";
    private static final String MODERN_DESCRIPTOR =
            "(Lcom/mojang/authlib/properties/Property;)Lcom/mojang/authlib/minecraft/MinecraftProfileTextures;";

    private static final String GET_DESCRIPTOR =
            "(Lcom/mojang/authlib/GameProfile;)Ljava/util/concurrent/CompletableFuture;";

    /**
     * 1.7.10 的 Forge/LaunchWrapper 会先加载它自带的旧版 ASM（5.x），
     * 而 agent jar 里带的是 ASM 9.11；两边 API 常量不能混用，
     * 否则 ClassVisitor 构造直接 IllegalArgumentException。
     * 运行时探测实际加载的 ASM 支持的最高 API。
     */
    private static final int ASM_API = ResolveAsmApi();

    private static int ResolveAsmApi()
    {
        for (int version = 9; version >= 4; version--)
        {
            try
            {
                return Opcodes.class.getField("ASM" + version).getInt(null);
            }
            catch (Throwable ignored)
            {
                // 该版本不存在，继续往下试
            }
        }

        return 5 << 16; // ASM5 的 API 数值
    }

    @Override
    public byte[] transform(ClassLoader loader, String className, Class<?> classBeingRedefined,
                            ProtectionDomain domain, byte[] bytes) {
        boolean isYggdrasil = YGGDRASIL.equals(className);
        boolean isSkinManager = SKIN_MANAGER.equals(className);
        boolean isSkinPort = SKINPORT_DEFAULT.equals(className);

        if (!isYggdrasil && !isSkinManager && !isSkinPort) return null;

        SkinAgent.log("transforming " + className + " loader=" + loader);

        try {
            ClassReader reader = new ClassReader(bytes);
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);

            final int classMajor = reader.readUnsignedShort(6);

            reader.accept(new ClassVisitor(ASM_API, writer) {
                private boolean legacyRenamed;
                private int legacyAccess;
                private String legacySignature;
                private String[] legacyExceptions;

                @Override
                public MethodVisitor visitMethod(int access, String name, String descriptor,
                                                 String signature, String[] exceptions) {
                    // 1.x 的 getTextures 不能整段替换，否则其他玩家也会拿到本地皮肤。
                    // 把原方法改名保留，另生成一个按 UUID 分流的同名方法。
                    if (isYggdrasil && OLD_METHOD.equals(name) && OLD_DESCRIPTOR.equals(descriptor)) {
                        legacyRenamed = true;
                        legacyAccess = access;
                        legacySignature = signature;
                        legacyExceptions = exceptions;

                        SkinAgent.log("wrapping " + className + "." + name + descriptor);
                        return super.visitMethod(access, "nmpc$getTextures$impl",
                                descriptor, signature, exceptions);
                    }

                    MethodVisitor target = super.visitMethod(access, name, descriptor, signature, exceptions);

                    // 调试：SkinManager 的方法签名各版本不一样，先把候选打出来（每次启动一条）
                    if (isSkinManager && (descriptor.endsWith(")Ljava/util/concurrent/CompletableFuture;")
                            || name.startsWith("get") || name.startsWith("load"))) {
                        SkinAgent.log("skinmanager: " + name + descriptor);
                    }

                    // 不写死参数类型：只要叫 get、只有一个参数、返回 CompletableFuture 就接管
                    if (isSkinManager && SkinAgent.modernReady && "get".equals(name)
                            && descriptor.endsWith(")Ljava/util/concurrent/CompletableFuture;")) {
                        SkinAgent.log("replacing " + className + "." + name + descriptor);
                        return replaceGet(target);
                    }

                    if (isSkinPort && "getSkin".equals(name) && SKINPORT_GET_DESCRIPTOR.equals(descriptor)) {
                        SkinAgent.log("replacing " + className + "." + name + descriptor);
                        return replaceSkinPort(target);
                    }

                    if (!isYggdrasil) return target;

                    if (SkinAgent.modernReady && MODERN_METHOD.equals(name) && MODERN_DESCRIPTOR.equals(descriptor)) {
                        SkinAgent.log("replacing " + className + "." + name + descriptor);
                        return replaceModern(target);
                    }

                    return target;
                }

                @Override
                public void visitEnd() {
                    if (legacyRenamed)
                    {
                        MethodVisitor method = super.visitMethod(
                            legacyAccess & ~(Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE),
                            OLD_METHOD, OLD_DESCRIPTOR, legacySignature, legacyExceptions);

                        Label notLocal = new Label();

                        method.visitCode();
                        method.visitVarInsn(Opcodes.ALOAD, 1);
                        method.visitMethodInsn(Opcodes.INVOKESTATIC, "nmpc/skin/SkinBridge", "isLocal",
                                "(Lcom/mojang/authlib/GameProfile;)Z", false);
                        method.visitJumpInsn(Opcodes.IFEQ, notLocal);

                        method.visitVarInsn(Opcodes.ALOAD, 1);
                        method.visitMethodInsn(Opcodes.INVOKESTATIC, "nmpc/skin/SkinBridge", "textures",
                                "(Lcom/mojang/authlib/GameProfile;)Ljava/util/Map;", false);
                        method.visitInsn(Opcodes.ARETURN);

                        method.visitLabel(notLocal);
                        if (classMajor >= 50)
                        {
                            method.visitFrame(Opcodes.F_NEW, 3, new Object[] {
                                    className,
                                    "com/mojang/authlib/GameProfile",
                                    Opcodes.INTEGER
                            }, 0, null);
                        }

                        method.visitVarInsn(Opcodes.ALOAD, 0);
                        method.visitVarInsn(Opcodes.ALOAD, 1);
                        method.visitVarInsn(Opcodes.ILOAD, 2);
                        method.visitMethodInsn(Opcodes.INVOKEVIRTUAL, className,
                                "nmpc$getTextures$impl", OLD_DESCRIPTOR, false);
                        method.visitInsn(Opcodes.ARETURN);
                        method.visitMaxs(3, 3);
                        method.visitEnd();
                    }

                    super.visitEnd();
                }
            }, 0);

            return writer.toByteArray();
        } catch (Throwable error) {
            StringBuilder builder = new StringBuilder("transform failed: ").append(error);
            for (StackTraceElement frame : error.getStackTrace())
                builder.append("\n  at ").append(frame);

            SkinAgent.log(builder.toString());
            return null;
        }
    }

    /** return SkinBridge.textures(profile) */
    private static MethodVisitor replaceOld(MethodVisitor target) {
        return new MethodVisitor(ASM_API) {
            @Override
            public void visitEnd() {
                target.visitCode();
                target.visitVarInsn(Opcodes.ALOAD, 1);
                target.visitMethodInsn(Opcodes.INVOKESTATIC, "nmpc/skin/SkinBridge", "textures",
                        "(Lcom/mojang/authlib/GameProfile;)Ljava/util/Map;", false);
                target.visitInsn(Opcodes.ARETURN);
                target.visitMaxs(1, 2);
                target.visitEnd();
            }
        };
    }

    /** return (MinecraftProfileTextures) SkinBridgeModern.texturesFor(property) */
    private static MethodVisitor replaceModern(MethodVisitor target) {
        return new MethodVisitor(ASM_API) {
            @Override
            public void visitEnd() {
                target.visitCode();
                target.visitVarInsn(Opcodes.ALOAD, 1);
                target.visitMethodInsn(Opcodes.INVOKESTATIC, "nmpc/skin/SkinBridgeModern", "texturesFor",
                        "(Ljava/lang/Object;)Ljava/lang/Object;", false);
                target.visitTypeInsn(Opcodes.CHECKCAST, "com/mojang/authlib/minecraft/MinecraftProfileTextures");
                target.visitInsn(Opcodes.ARETURN);
                target.visitMaxs(1, 2);
                target.visitEnd();
            }
        };
    }

    /** return (ISkin) SkinPortBridge.get(provider, profile) */
    private static MethodVisitor replaceSkinPort(MethodVisitor target) {
        return new MethodVisitor(ASM_API) {
            @Override
            public void visitEnd() {
                target.visitCode();
                target.visitVarInsn(Opcodes.ALOAD, 0);
                target.visitVarInsn(Opcodes.ALOAD, 1);
                target.visitMethodInsn(Opcodes.INVOKESTATIC, "nmpc/skin/SkinPortBridge", "get",
                        "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", false);
                target.visitTypeInsn(Opcodes.CHECKCAST, "lain/mods/skins/api/interfaces/ISkin");
                target.visitInsn(Opcodes.ARETURN);
                target.visitMaxs(2, 2);
                target.visitEnd();
            }
        };
    }

    /** return SkinBridgeModern.get(skinManager, profile) */
    private static MethodVisitor replaceGet(MethodVisitor target) {
        return new MethodVisitor(ASM_API) {
            @Override
            public void visitEnd() {
                target.visitCode();
                target.visitVarInsn(Opcodes.ALOAD, 0);
                target.visitVarInsn(Opcodes.ALOAD, 1);
                target.visitMethodInsn(Opcodes.INVOKESTATIC, "nmpc/skin/SkinBridgeModern", "get",
                        "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/util/concurrent/CompletableFuture;", false);
                target.visitInsn(Opcodes.ARETURN);
                target.visitMaxs(2, 2);
                target.visitEnd();
            }
        };
    }
}