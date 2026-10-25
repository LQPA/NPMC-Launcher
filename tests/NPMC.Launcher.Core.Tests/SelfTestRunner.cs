using System.Reflection;

namespace NPMC.Launcher.Core.Tests;

/// <summary>
/// 极简反射测试运行器。
///
/// 存在的唯一原因：部分受限环境里 vstest 的 testhost 会在
/// DefaultEngineInvoker.SetParentProcessExitCallback 处崩溃
/// （它要 OpenProcess 父进程来监听退出，返回 Win32Exception 5: 拒绝访问），
/// 使得 dotnet test 完全不可用。
///
/// 用例本身仍是标准 xunit 的 [Fact]，所以在正常环境里 dotnet test 照常工作；
/// 这里只负责在本机把同样的用例跑起来。
///
/// 用法：
///   dotnet build -c Release -p:SelfTestRunner=true
///   tests\NPMC.Launcher.Core.Tests\bin\Release\net8.0\NPMC.Launcher.Core.Tests.exe
///
/// 注意：只识别 [Fact]，不支持 [Theory] / Skip。
/// </summary>
internal static class SelfTestRunner
{
    private static int Main()
    {
        var assembly = typeof(SelfTestRunner).Assembly;

        var cases = new List<(Type Type, MethodInfo Method)>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetParameters().Length != 0) continue;

                // xunit 支持 void 与 async Task 两种用例；其余一律跳过
                var isVoid = method.ReturnType == typeof(void);
                var isTask = typeof(Task).IsAssignableFrom(method.ReturnType);
                if (!isVoid && !isTask) continue;

                var isFact = method.GetCustomAttributes(inherit: true)
                    .Any(a => a.GetType().Name == "FactAttribute");

                if (isFact) cases.Add((type, method));
            }
        }

        cases.Sort((a, b) => string.CompareOrdinal(
            $"{a.Type.Name}.{a.Method.Name}", $"{b.Type.Name}.{b.Method.Name}"));

        if (cases.Count == 0)
        {
            Console.WriteLine("没有发现任何 [Fact] 用例 —— 检查 SelfTestRunner 是否被正确编译进来。");
            return 2;
        }

        var passed = 0;
        var failures = new List<string>();
        string? currentClass = null;

        foreach (var (type, method) in cases)
        {
            if (currentClass != type.Name)
            {
                currentClass = type.Name;
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(currentClass);
                Console.ResetColor();
            }

            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
                var result = method.Invoke(instance, null);

                // async 用例必须等它跑完，否则异常会被吞掉、用例变成假通过
                if (result is Task task) task.GetAwaiter().GetResult();

                passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  PASS  {method.Name}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: not null } tie
                    ? tie.InnerException!
                    : ex;

                failures.Add($"{type.Name}.{method.Name}{Environment.NewLine}        {inner.GetType().Name}: {inner.Message}");

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAIL  {method.Name}");
                Console.ResetColor();
                Console.WriteLine($"        {inner.GetType().Name}: {inner.Message}");
            }
            finally
            {
                if (instance is IDisposable disposable) disposable.Dispose();
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('-', 60));

        if (failures.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"全部通过：{passed}/{cases.Count}");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"失败 {failures.Count} 个 / 共 {cases.Count} 个（通过 {passed} 个）");
        Console.ResetColor();
        Console.WriteLine();
        foreach (var failure in failures) Console.WriteLine($"  {failure}");

        return 1;
    }
}
