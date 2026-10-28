import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.commons.ClassRemapper;
import org.objectweb.asm.commons.Remapper;

import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.Enumeration;
import java.util.stream.Stream;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * 把 jar / 目录里的类重定位到另一个包前缀。
 *
 * 用途：agent 需要自包含 ASM（原版客户端没有 ASM），但 Fabric 的启动校验
 * 一旦在 classpath 上发现两份 org.objectweb.asm 就拒绝启动。所以把内嵌的 ASM
 * 改成私有包（nmpc/skinned/asmx），我们的类里对 ASM 的引用也一起重写。
 *
 * 用法：
 *   java -cp asm.jar;asm-commons.jar;asm-tree.jar;类目录 Relocator jar <asm.jar> <目标目录> org/objectweb/asm nmpc/skinned/asmx
 *   java -cp ...                                       Relocator dir <类目录>     org/objectweb/asm nmpc/skinned/asmx
 */
public final class Relocator {
    public static void main(String[] args) throws Exception {
        String mode = args[0];
        String from = args[args.length - 2];
        String to = args[args.length - 1];

        final String prefix = from;
        final String replacement = to;
        Remapper remapper = new Remapper() {
            @Override
            public String map(String internalName) {
                if (internalName.startsWith(prefix + "/")) {
                    return replacement + internalName.substring(prefix.length());
                }
                return internalName;
            }
        };

        if (mode.equals("jar")) {
            try (ZipFile zip = new ZipFile(args[1])) {
                Enumeration<? extends ZipEntry> entries = zip.entries();
                while (entries.hasMoreElements()) {
                    ZipEntry entry = entries.nextElement();
                    if (!entry.getName().endsWith(".class")) continue;
                    if (!entry.getName().startsWith(from + "/")) continue;

                    ClassReader reader = new ClassReader(readAll(zip.getInputStream(entry)));
                    ClassWriter writer = new ClassWriter(0);
                    reader.accept(new ClassRemapper(writer, remapper), ClassReader.EXPAND_FRAMES);

                    String name = entry.getName().substring(0, entry.getName().length() - 6);
                    String target = to + name.substring(from.length());
                    Path path = Paths.get(args[2], target + ".class");
                    Files.createDirectories(path.getParent());
                    Files.write(path, writer.toByteArray());
                }
            }
            return;
        }

        try (Stream<Path> files = Files.walk(Paths.get(args[1]))) {
            files.filter(p -> p.toString().endsWith(".class")).forEach(p -> {
                try {
                    ClassReader reader = new ClassReader(Files.readAllBytes(p));
                    ClassWriter writer = new ClassWriter(0);
                    reader.accept(new ClassRemapper(writer, remapper), ClassReader.EXPAND_FRAMES);
                    Files.write(p, writer.toByteArray());
                } catch (Exception error) {
                    throw new RuntimeException(p.toString(), error);
                }
            });
        }
    }

    private static byte[] readAll(InputStream input) throws Exception {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = input.read(buffer)) != -1) out.write(buffer, 0, read);
        input.close();
        return out.toByteArray();
    }

    private Relocator() {
    }
}
