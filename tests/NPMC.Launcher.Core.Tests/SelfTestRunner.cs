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
/// 用例本身仍是标准 xunit 的 [Fact] / [Theory]，所以在正常环境里 dotnet test 照常工作；
/// 这里只负责在本机把同样的用例跑起来。
///
/// 用法：
///   dotnet build -c Release -p:SelfTestRunner=true
///   tests\NPMC.Launcher.Core.Tests\bin\Release\net8.0\NPMC.Launcher.Core.Tests.exe
///
/// 支持范围：[Fact]、[Theory] + [InlineData]（含 async Task）。
/// **不支持** [MemberData] / [ClassData] / Skip —— 用到它们会在这里被静默跳过，
/// 所以本项目的测试只用 InlineData。若将来必须用 MemberData，先扩展这里。
///
/// 这个提醒不是多余的：运行器最初只认 [Fact]，于是带参数的 [Theory] 一条都不会执行，
/// 而汇总行依然显示"全部通过" —— 一类测试就这样静默消失了。
/// </summary>
internal static class SelfTestRunner
{
    private sealed record TestCase(
        Type Type,
        MethodInfo Method,
        object?[] Arguments,
        string Display);

    private static int Main()
    {
        var assembly = typeof(SelfTestRunner).Assembly;

        var cases = new List<TestCase>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                // xunit 支持 void 与 async Task 两种用例；其余一律跳过
                var isVoid = method.ReturnType == typeof(void);
                var isTask = typeof(Task).IsAssignableFrom(method.ReturnType);
                if (!isVoid && !isTask) continue;

                var attributes = method.GetCustomAttributes(inherit: true);
                var hasFact = attributes.Any(a => a.GetType().Name == "FactAttribute");
                var hasTheory = attributes.Any(a => a.GetType().Name == "TheoryAttribute");

                if (hasFact && method.GetParameters().Length == 0)
                {
                    cases.Add(new TestCase(type, method, Array.Empty<object?>(), method.Name));
                    continue;
                }

                if (!hasTheory) continue;

                // 通过 DataAttribute.GetData(MethodInfo) 取数据行。
                // 不能直接读属性：xunit 的 InlineDataAttribute 把数组放在私有字段里，
                // 只暴露 GetData；这样同时也就顺带支持了 MemberData / ClassData。
                var rows = new List<object?[]>();
                var dataAttributes = attributes
                    .Where(a => a.GetType().Name is "InlineDataAttribute" or "MemberDataAttribute" or "ClassDataAttribute")
                    .ToList();

                foreach (var attribute in dataAttributes)
                {
                    var getData = attribute.GetType().GetMethod("GetData", new[] { typeof(MethodInfo) });
                    if (getData is null) continue;

                    if (getData.Invoke(attribute, new object?[] { method }) is not System.Collections.IEnumerable produced)
                        continue;

                    foreach (var row in produced)
                    {
                        if (row is object?[] values) rows.Add(values);
                    }
                }

                if (rows.Count == 0)
                {
                    // 数据源拿不到 —— 明确报出来，绝不假装通过
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  跳过  {type.Name}.{method.Name}：这个 [Theory] 没有可用的数据行");
                    Console.ResetColor();
                    continue;
                }

                foreach (var row in rows)
                {
                    var display = $"{method.Name}({string.Join(", ", row.Select(FormatArgument))})";
                    cases.Add(new TestCase(type, method, row, display));
                }
            }
        }

        cases.Sort((a, b) => string.CompareOrdinal($"{a.Type.Name}.{a.Display}", $"{b.Type.Name}.{b.Display}"));

        if (cases.Count == 0)
        {
            Console.WriteLine("没有发现任何用例 —— 检查 SelfTestRunner 是否被正确编译进来。");
            return 2;
        }

        var passed = 0;
        var failures = new List<string>();
        string? currentClass = null;

        foreach (var testCase in cases)
        {
            if (currentClass != testCase.Type.Name)
            {
                currentClass = testCase.Type.Name;
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(currentClass);
                Console.ResetColor();
            }

            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(testCase.Type);
                var result = testCase.Method.Invoke(instance, testCase.Arguments);

                // async 用例必须等它跑完，否则异常会被吞掉、用例变成假通过
                if (result is Task task) task.GetAwaiter().GetResult();

                passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  PASS  {testCase.Display}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: not null } tie
                    ? tie.InnerException!
                    : ex;

                failures.Add($"{testCase.Type.Name}.{testCase.Display}{Environment.NewLine}"
                             + $"        {inner.GetType().Name}: {inner.Message}");

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAIL  {testCase.Display}");
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

    private static string FormatArgument(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "?",
    };
}
