package nmpc.skin;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
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

    private static final String OLD_METHOD = "getTextures";
    private static final String OLD_DESCRIPTOR =
            "(Lcom/mojang/authlib/GameProfile;Z)Ljava/util/Map;";

    private static final String MODERN_METHOD = "unpackTextures";
    private static final String MODERN_DESCRIPTOR =
            "(Lcom/mojang/authlib/properties/Property;)Lcom/mojang/authlib/minecraft/MinecraftProfileTextures;";

    private static final String GET_DESCRIPTOR =
            "(Lcom/mojang/authlib/GameProfile;)Ljava/util/concurrent/CompletableFuture;";

    @Override
    public byte[] transform(ClassLoader loader, String className, Class<?> classBeingRedefined,
                            ProtectionDomain domain, byte[] bytes) {
        boolean isYggdrasil = YGGDRASIL.equals(className);
        boolean isSkinManager = SKIN_MANAGER.equals(className);

        if (!isYggdrasil && !isSkinManager) return null;

        try {
            ClassReader reader = new ClassReader(bytes);
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);

            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                @Override
                public MethodVisitor visitMethod(int access, String name, String descriptor,
                                                 String signature, String[] exceptions) {
                    MethodVisitor target = super.visitMethod(access, name, descriptor, signature, exceptions);

                    if (isSkinManager && "get".equals(name) && GET_DESCRIPTOR.equals(descriptor)) {
                        SkinAgent.log("replacing " + className + "." + name + descriptor);
                        return replaceGet(target);
                    }

                    if (!isYggdrasil) return target;

                    if (OLD_METHOD.equals(name) && OLD_DESCRIPTOR.equals(descriptor)) {
                        SkinAgent.log("replacing " + className + "." + name + descriptor);
                        return replaceOld(target);
                    }

                    if (MODERN_METHOD.equals(name) && MODERN_DESCRIPTOR.equals(descriptor)) {
                        SkinAgent.log("replacing " + className + "." + name + descriptor);
                        return replaceModern(target);
                    }

                    return target;
                }
            }, 0);

            return writer.toByteArray();
        } catch (Throwable error) {
            SkinAgent.log("transform failed: " + error);
            return null;
        }
    }

    /** return SkinBridge.textures(profile) */
    private static MethodVisitor replaceOld(MethodVisitor target) {
        return new MethodVisitor(Opcodes.ASM9) {
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

    /** return SkinBridgeModern.textures() */
    private static MethodVisitor replaceModern(MethodVisitor target) {
        return new MethodVisitor(Opcodes.ASM9) {
            @Override
            public void visitEnd() {
                target.visitCode();
                target.visitMethodInsn(Opcodes.INVOKESTATIC, "nmpc/skin/SkinBridgeModern", "textures",
                        "()Lcom/mojang/authlib/minecraft/MinecraftProfileTextures;", false);
                target.visitInsn(Opcodes.ARETURN);
                target.visitMaxs(1, 2);
                target.visitEnd();
            }
        };
    }

    /** return SkinBridgeModern.get(skinManager, profile) */
    private static MethodVisitor replaceGet(MethodVisitor target) {
        return new MethodVisitor(Opcodes.ASM9) {
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