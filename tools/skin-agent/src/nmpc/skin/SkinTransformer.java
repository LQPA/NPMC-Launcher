package nmpc.skin;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

/**
 * 把 authlib 的 YggdrasilMinecraftSessionService.getTextures(profile, requireSecure)
 * 整体替换成 SkinBridge.textures(profile)。
 *
 * 为什么不在 fvu（SkinManager）上打补丁：
 *   1.20.1 走的是会话服务的 getTextures，而它会对离线 textures 做签名校验；
 *   直接在 authlib 层返回纹理表，既绕开校验，也不依赖混淆类名。
 * 其它类一律原样返回。
 */
final class SkinTransformer implements ClassFileTransformer {
    private static final String TARGET = "com/mojang/authlib/yggdrasil/YggdrasilMinecraftSessionService";
    private static final String METHOD = "getTextures";
    private static final String DESCRIPTOR =
            "(Lcom/mojang/authlib/GameProfile;Z)Ljava/util/Map;";
    private static final String BRIDGE = "nmpc/skin/SkinBridge";
    private static final String BRIDGE_DESCRIPTOR =
            "(Lcom/mojang/authlib/GameProfile;)Ljava/util/Map;";

    @Override
    public byte[] transform(ClassLoader loader, String className, Class<?> classBeingRedefined,
                            ProtectionDomain domain, byte[] bytes) {
        if (!TARGET.equals(className)) return null;

        try {
            ClassReader reader = new ClassReader(bytes);
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);

            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                @Override
                public MethodVisitor visitMethod(int access, String name, String descriptor,
                                                 String signature, String[] exceptions) {
                    MethodVisitor target = super.visitMethod(access, name, descriptor, signature, exceptions);

                    if (!METHOD.equals(name) || !DESCRIPTOR.equals(descriptor)) return target;

                    SkinAgent.log("replacing " + TARGET + "." + name + descriptor);

                    // 丢弃原方法体，visitEnd 时写入新的返回语句
                    return new MethodVisitor(Opcodes.ASM9) {
                        @Override
                        public void visitEnd() {
                            target.visitCode();
                            target.visitVarInsn(Opcodes.ALOAD, 1);
                            target.visitMethodInsn(Opcodes.INVOKESTATIC, BRIDGE, "textures",
                                    BRIDGE_DESCRIPTOR, false);
                            target.visitInsn(Opcodes.ARETURN);
                            target.visitMaxs(1, 2);
                            target.visitEnd();
                        }
                    };
                }
            }, 0);

            return writer.toByteArray();
        } catch (Throwable error) {
            SkinAgent.log("transform failed: " + error);
            return null;
        }
    }
}
