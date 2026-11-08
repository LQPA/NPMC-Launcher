using System.Text.RegularExpressions;
using NPMC.Launcher.App.Mods;

namespace NPMC.Launcher.App.Diagnostics;

public enum LaunchDiagnosisSeverity
{
    Warning,
    Error,
}

/// <summary>主因之外的一条次生症状（通常是被主因拖垮、或容易误导人的日志）。</summary>
public sealed record LaunchDiagnosisSymptom(string Title, string Detail);

/// <summary>一次启动失败/游戏崩溃的可展示诊断结果。</summary>
public sealed record LaunchDiagnosisReport(
    string Title,
    string Summary,
    string Suggestion,
    string Evidence,
    LaunchDiagnosisSeverity Severity,
    string? LogPath,
    string? CrashReportPath)
{
    /// <summary>次生症状：屏幕上要折叠显示，避免用户把症状当病因。</summary>
    public IReadOnlyList<LaunchDiagnosisSymptom> Symptoms { get; init; } = Array.Empty<LaunchDiagnosisSymptom>();

    /// <summary>本次涉及到的模组（显示名 + jar 文件名），可直接拿去禁用。</summary>
    public IReadOnlyList<string> InvolvedMods { get; init; } = Array.Empty<string>();

    public bool HasEvidence => !string.IsNullOrWhiteSpace(Evidence);

    public bool HasSymptoms => Symptoms.Count > 0;

    public string SymptomsText => Symptoms.Count == 0
        ? string.Empty
        : string.Join('\n', Symptoms.Select(s => $"· {s.Title}：{s.Detail}"));

    public string InvolvedModsText => InvolvedMods.Count == 0
        ? string.Empty
        : "涉及模组：" + string.Join("、", InvolvedMods);
}

/// <summary>
/// 启动诊断。
///
/// 核心原则：**按证据强度分层的判定阶梯，而不是“哪条正则先命中就听谁的”**。
/// 加载器自己给出的结论往往是最表层的（例：iris_shader_folder requires oculus），
/// 而真实断裂点可能在更下面一层（oculus 缺 embeddium 而根本没加载）。
/// 因此顺序固定为：
///
///   1. 崩溃报告里的结构化缺失依赖（-- MOD -- 段 / Missing and unsupported dependencies）
///   2. 与 mods 目录交叉校验：jar 在却被判定“未安装” ⇒ 是该模组自己加载失败
///   3. 崩溃点之前最近的一条“找不到某个类” + 包名反查（在已装 jar 里找，找不到才查内置包名表）
///   4. Mixin 注入失败 / Class version 警告 ⇒ 只作为次生症状，且明确标注是噪声
///   5. 其余正则兜底
///
/// 第 4 条是这次踩坑的重点：Oculus 的 Class version 警告（JAVA_8 支持 52 / 实际 61）
/// 看得最像“Mixin 不兼容”，实际只是它自己声明的 compatibilityLevel 导致个别 mixin 被跳过，
/// 真正的崩溃点是缺 Sodium/Embeddium 提供的 net.caffeinemc.mods.sodium.api.* 。
/// </summary>
public static class LaunchDiagnostics
{
    private sealed record Rule(
        Regex Pattern,
        string Title,
        string Summary,
        string Suggestion,
        LaunchDiagnosisSeverity Severity);

    private static readonly Rule[] Rules =
    {
        new(
            new Regex(
                @"Missing or unsupported mandatory dependencies|Missing dependency|requires .* which is missing|Mod .* requires .*",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "缺少前置模组",
            "崩溃报告显示有模组依赖未满足。",
            "按证据中的模组名安装对应前置；或暂时移除依赖它的模组。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"Duplicate mods?|duplicate mod|已存在.*模组|重复.*模组",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "模组重复安装",
            "同一个 modId 存在多份文件，加载器无法决定使用哪一份。",
            "在 mods 目录中只保留一个版本，删除重复的 jar。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"Mixin apply failed|MixinTransformerError|Mixin.*apply.*failed|Mixin.*injection.*failed|Mixin.*transform.*failed",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "模组 Mixin 注入失败",
            "某个模组的 Mixin 在改写游戏类时抛异常。",
            "看崩溃点之前最近的一条 “Error loading class / Caused by”：Mixin 崩溃几乎都是它的前置没装或版本不对；" +
            "只凭 MixinTransformerError 本身无法定位到具体模组。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"NoClassDefFoundError", RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "缺少类/前置模组",
            "游戏运行时找不到某个类，通常是前置未安装、被禁用，或模组版本不兼容。",
            "检查是否禁用了其他模组依赖的前置；必要时重新启用或安装对应前置。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"UnsupportedClassVersionError|class file version|requires Java \d+",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "Java 版本不匹配",
            "当前 Java 无法运行该版本或模组。",
            "在实例设置中选择匹配的 Java，或让启动器重新安装对应运行时。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"OutOfMemoryError|Java heap space|GC overhead limit exceeded",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "内存不足",
            "游戏在分配内存时失败。",
            "提高实例内存上限，或减少同时加载的模组/资源包。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"GLFW error|Pixel format|OpenGL|WGL|Failed to create window",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "图形驱动/OpenGL 问题",
            "游戏无法创建渲染上下文或窗口。",
            "更新显卡驱动；关闭覆盖层/录屏软件；确认没有强制使用不兼容的 OpenGL 版本。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"AuthenticationException|InvalidCredentialsException|Failed to login|Invalid session",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "登录凭据失效",
            "账号令牌被服务器拒绝。",
            "在账号页重新登录该账号后再启动。",
            LaunchDiagnosisSeverity.Warning),

        new(
            new Regex(@"NoSuchFileException|FileNotFoundException|missing asset|missing library|Failed to download",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "游戏文件缺失或损坏",
            "启动所需的库/资源文件不存在。",
            "对该版本执行修复/重新安装，重点检查 libraries 与 assets。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"Invalid or corrupt jarfile|zip END header not found|invalid LOC header|Error opening zip file",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "模组或游戏 jar 损坏",
            "某个 jar 文件不完整，Java 无法读取。",
            "重新下载最近安装的模组；必要时重新安装版本。",
            LaunchDiagnosisSeverity.Error),

        new(
            new Regex(@"ModLoadingException|LoadingFailedException|Incompatible mod set|Mod loading has failed",
                RegexOptions.IgnoreCase | RegexOptions.Multiline),
            "模组加载失败",
            "加载器在初始化模组时失败，常见于版本不兼容。",
            "根据证据中的模组名升级/降级到适配当前游戏版本的版本。",
            LaunchDiagnosisSeverity.Error),
    };

    /// <summary>崩溃报告里 -- MOD xxx -- 段的结构化信息。</summary>
    private sealed record ModFailureBlock(
        string ModName,
        string? ModFile,
        string? ModVersion,
        string? FailureMessage,
        string? MissingModId,
        string? MissingRange);

    /// <summary>加载器声明的缺失依赖（NeoForge/Forge 的 Missing and unsupported dependencies 段）。</summary>
    private sealed record MissingDependency(string MissingModId, string RequestedBy, string? ExpectedRange, string? ActualVersion);

    private sealed record MixinVersionWarning(
        string MixinName,
        string Config,
        string Target,
        string RequiredVersion,
        string SupportedVersion)
    {
        public string ModId => ExtractModIdFromConfig(Config);
    }

    /// <summary>
    /// 从 mixin 配置名猜 modId：mixins.oculus.json → oculus，
    /// oculus-batched-entity-rendering.mixins.json → oculus。
    /// 只是启发式，用来把配置名变成可以在 mods 里查的名字。
    /// </summary>
    private static string ExtractModIdFromConfig(string config)
    {
        var name = config.Trim();
        var slash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^5];
        }

        if (name.EndsWith(".mixins", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^7];
        }

        if (name.StartsWith("mixins.", StringComparison.OrdinalIgnoreCase))
        {
            name = name["mixins.".Length..];
        }

        return name.Split('.', '-', '_')[0];
    }

    /// <summary>一次 mixin 注入（“Mixing X from Y into Z”）。</summary>
    private sealed record MixinApplication(string MixinName, string Config, string Target, int Index)
    {
        public string ModId => ExtractModIdFromConfig(Config);
    }

    private sealed record MissingClassSignal(int Index, string ClassName);

    public static LaunchDiagnosisReport ForPreparation(
        string title,
        string summary,
        string suggestion,
        string? logPath = null)
    {
        return new LaunchDiagnosisReport(
            title,
            summary,
            suggestion,
            string.Empty,
            LaunchDiagnosisSeverity.Error,
            logPath,
            null);
    }

    public static LaunchDiagnosisReport AnalyzeGameExit(
        string instanceName,
        string gameDirectory,
        int exitCode,
        string? stderrTail,
        DateTimeOffset launchedAt,
        ModIndex? mods = null)
    {
        var cutoff = launchedAt.UtcDateTime.AddSeconds(-2);
        var crashReport = FindNewestCrashReport(gameDirectory, cutoff);
        var latestLog = Path.Combine(gameDirectory, "logs", "latest.log");
        var hasFreshLog = File.Exists(latestLog) &&
                          File.GetLastWriteTimeUtc(latestLog) >= cutoff;
        var crashText = crashReport is null ? string.Empty : ReadTail(crashReport, 240);
        var logText = hasFreshLog ? ReadTail(latestLog, 300) : string.Empty;

        return Analyze(
            instanceName,
            exitCode,
            crashText,
            logText,
            stderrTail,
            mods,
            hasFreshLog ? latestLog : null,
            crashReport);
    }

    /// <summary>诊断本次启动会话的归档快照，完全不读取实例目录里可能过期的 live 文件。</summary>
    public static LaunchDiagnosisReport AnalyzeSession(
        string instanceName,
        int exitCode,
        LaunchLogSession session,
        ModIndex? mods = null)
    {
        var crashReport = FindNewestCrashReportInDirectory(session.CrashReportDirectory);
        var latestLog = File.Exists(session.LatestLogPath) ? session.LatestLogPath : null;
        var crashText = crashReport is null ? string.Empty : ReadTail(crashReport, 240);
        var logText = latestLog is null ? string.Empty : ReadTail(latestLog, 300);
        if (File.Exists(session.DebugLogPath))
        {
            logText += "\n" + ReadTail(session.DebugLogPath, 1200);
        }

        var stderr = File.Exists(session.StderrLogPath)
            ? ReadTail(session.StderrLogPath, 200)
            : null;

        return Analyze(
            instanceName,
            exitCode,
            crashText,
            logText,
            stderr,
            mods,
            latestLog,
            crashReport);
    }

    /// <summary>
    /// 纯文本诊断入口：不碰文件系统，方便单测直接喂真实日志片段。
    /// </summary>
    public static LaunchDiagnosisReport Analyze(
        string instanceName,
        int exitCode,
        string crashText,
        string logText,
        string? stderrTail,
        ModIndex? mods = null,
        string? latestLog = null,
        string? crashReport = null)
    {
        var combined = crashText + "\n" + logText + "\n" + (stderrTail ?? string.Empty);

        var blocks = ParseModFailureBlocks(crashText);
        var declaredMissing = ParseMissingDependencies(combined);
        var mixinWarning = TryParseMixinVersionWarning(combined);
        var anchor = FindCrashAnchor(combined);
        var lastMixing = FindLastMixing(combined, anchor);
        var missingClass = FindNearestMissingClass(combined, anchor, lastMixing?.Index ?? -1);

        var symptoms = new List<LaunchDiagnosisSymptom>();
        if (mixinWarning is not null)
        {
            symptoms.Add(new LaunchDiagnosisSymptom(
                "Mixin Class version 警告（多半是噪声）",
                $"{mixinWarning.Config} 声明了较低的 compatibilityLevel：Class version " +
                $"{mixinWarning.RequiredVersion} > 支持 {mixinWarning.SupportedVersion}。" +
                "这条只影响该模组自己那一条 mixin 的注入，不能直接当成崩溃原因。"));
        }

        if (lastMixing is not null)
        {
            symptoms.Add(new LaunchDiagnosisSymptom(
                "崩溃点附近的注入",
                $"{lastMixing.MixinName} from {lastMixing.Config} into {lastMixing.Target}"));
        }

        var involved = new List<string>();
        void AddInvolved(string? modId, string? jarFile = null)
        {
            string text;
            if (!string.IsNullOrWhiteSpace(modId))
            {
                text = mods?.Find(modId) is { } meta
                    ? $"{meta.DisplayName}（{meta.FileName}）"
                    : modId!;
            }
            else if (!string.IsNullOrWhiteSpace(jarFile))
            {
                text = Path.GetFileName(jarFile);
            }
            else
            {
                return;
            }

            if (!involved.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                involved.Add(text);
            }
        }

        foreach (var block in blocks)
        {
            AddInvolved(null, block.ModFile);
            AddInvolved(block.MissingModId);
        }

        // ---- 阶梯 1/2：加载器声明的缺失依赖，并与 mods 目录交叉校验 ----
        var missingDeps = new List<MissingDependency>(declaredMissing);
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.MissingModId))
            {
                continue;
            }

            if (missingDeps.Any(d => string.Equals(d.MissingModId, block.MissingModId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            missingDeps.Add(new MissingDependency(
                block.MissingModId!,
                block.ModName,
                block.MissingRange,
                "MISSING"));
        }

        if (missingDeps.Count > 0 && mods is not null)
        {
            var presentButBroken = missingDeps
                .Where(d => mods.Contains(d.MissingModId))
                .ToList();
            var trulyMissing = missingDeps
                .Where(d => !mods.Contains(d.MissingModId))
                .ToList();

            if (presentButBroken.Count > 0)
            {
                return BuildBrokenModReport(
                    instanceName,
                    exitCode,
                    presentButBroken,
                    missingClass,
                    lastMixing,
                    mixinWarning,
                    mods,
                    symptoms,
                    involved,
                    blocks,
                    latestLog,
                    crashReport,
                    combined);
            }

            if (trulyMissing.Count > 0)
            {
                return BuildMissingDependencyReport(
                    instanceName,
                    exitCode,
                    trulyMissing,
                    mods,
                    symptoms,
                    involved,
                    latestLog,
                    crashReport,
                    combined);
            }
        }

        // ---- 阶梯 3：崩溃点之前最近一条“找不到某个类” + 包名反查 ----
        if (missingClass is not null && mods is not null)
        {
            var owner = mods.ExplainClass(missingClass.ClassName);
            var ownerInstalled = owner.ModId is not null && mods.Contains(owner.ModId);

            if (owner.HasKnownOwner && !ownerInstalled)
            {
                AddInvolved(owner.ModId);
                if (lastMixing is not null)
                {
                    AddInvolved(lastMixing.ModId);
                }

                var display = owner.DisplayName ?? owner.ModId ?? missingClass.ClassName;
                var marker = BuildClassEvidence(combined, missingClass, lastMixing);
                var summary = lastMixing is null
                    ? $"崩溃前日志里找不到类 {missingClass.ClassName}，它由 {display} 提供，而 mods 目录里没有这个前置。"
                    : $"模组 {DisplayMod(mods, lastMixing.ModId)} 的 mixin（{lastMixing.MixinName} → {lastMixing.Target}）"
                      + $"在应用时崩溃。崩溃前日志里找不到类 {missingClass.ClassName}，它由 {display} 提供，"
                      + "但 mods 目录里没有这个前置。";

                return new LaunchDiagnosisReport(
                    $"缺少前置：{display}",
                    summary,
                    $"安装 {display} 后重试（它提供 {missingClass.ClassName}）。" +
                    "只想先进游戏的话，先禁用崩溃点对应的模组。",
                    marker,
                    LaunchDiagnosisSeverity.Error,
                    latestLog,
                    crashReport)
                {
                    Symptoms = symptoms,
                    InvolvedMods = involved,
                };
            }

            if (owner.FoundInMods)
            {
                AddInvolved(owner.ModId, owner.JarPath);
                return new LaunchDiagnosisReport(
                    "模组未加载成功",
                    $"类 {missingClass.ClassName} 由 {Path.GetFileName(owner.JarPath ?? string.Empty)} 提供，"
                    + "该 jar 就在 mods 目录里，但没有被加载成功（通常是被别的缺失前置拖住了）。",
                    "看这个 jar 前面的第一条异常（NoClassDefFoundError / 缺依赖），先修它自己的前置。",
                    BuildClassEvidence(combined, missingClass, lastMixing),
                    LaunchDiagnosisSeverity.Error,
                    latestLog,
                    crashReport)
                {
                    Symptoms = symptoms,
                    InvolvedMods = involved,
                };
            }
        }

        // ---- 阶梯 4：Mixin 注入失败，但没有能定位的类证据 ----
        var mixinFailure = Rules[2].Pattern.Match(combined);
        if (mixinFailure.Success)
        {
            if (lastMixing is not null)
            {
                AddInvolved(lastMixing.ModId);
            }

            return new LaunchDiagnosisReport(
                "Mixin 注入失败",
                lastMixing is null
                    ? "某个模组的 Mixin 在改写游戏类时抛异常，但日志里没有可直接定位的缺失类。"
                    : $"模组 {DisplayMod(mods, lastMixing.ModId)} 的 {lastMixing.MixinName} "
                      + $"注入 {lastMixing.Target} 时抛异常，但日志里没有可直接定位的缺失类。",
                "先看崩溃点之前最近的一条 “Error loading class” 或 “Caused by”：那才是缺失的前置；"
                + "只凭 MixinTransformerError 无法定位到具体模组。",
                BuildEvidence(combined, mixinFailure),
                LaunchDiagnosisSeverity.Error,
                latestLog,
                crashReport)
            {
                Symptoms = symptoms,
                InvolvedMods = involved,
            };
        }

        // ---- 阶梯 4b：只有 Class version 警告 ----
        if (mixinWarning is not null)
        {
            AddInvolved(mixinWarning.ModId);
            return new LaunchDiagnosisReport(
                "模组 Mixin 兼容级别冲突",
                $"模组 {DisplayMod(mods, mixinWarning.ModId)} 的 mixin 配置声明了较低的 compatibilityLevel，"
                + $"导致 Class version {mixinWarning.RequiredVersion} 的类无法在 {mixinWarning.SupportedVersion} 级别下注入。",
                "升级该模组到匹配当前加载器版本的构建；若日志前面还有 “Error loading class”，先修那条缺失的前置。",
                $"{mixinWarning.Config}: {mixinWarning.MixinName} → {mixinWarning.Target}",
                LaunchDiagnosisSeverity.Warning,
                latestLog,
                crashReport)
            {
                Symptoms = symptoms,
                InvolvedMods = involved,
            };
        }

        // ---- 阶梯 5：其余正则兜底 ----
        foreach (var rule in Rules)
        {
            var match = rule.Pattern.Match(combined);
            if (!match.Success)
            {
                continue;
            }

            var evidence = BuildEvidence(combined, match);

            // 可选兼容目标不存在时，Mixin 会打一条 ClassNotFoundException WARN；
            // 这不是冲突，不能据此把整个崩溃归因到 Mixin。
            if (rule.Title.Contains("Mixin", StringComparison.Ordinal) &&
                IsOptionalMixinWarning(evidence))
            {
                continue;
            }

            return new LaunchDiagnosisReport(
                rule.Title,
                rule.Summary,
                rule.Suggestion,
                evidence,
                rule.Severity,
                latestLog,
                crashReport)
            {
                Symptoms = symptoms,
                InvolvedMods = involved,
            };
        }

        var fallback = FirstUsefulLine(stderrTail)
                       ?? FirstUsefulLine(crashText)
                       ?? FirstUsefulLine(logText)
                       ?? "没有找到可识别的崩溃原因。";

        // 兜底时补一条“mods 里能确定不满足的依赖”，至少别让用户两眼一抹黑
        var danglingHint = mods is null ? null : DescribeDanglingDependencies(mods);

        return new LaunchDiagnosisReport(
            "游戏异常退出",
            $"{instanceName} 以退出码 {exitCode} 结束，未匹配到已知崩溃模式。"
            + (danglingHint is null ? string.Empty : " " + danglingHint),
            "打开日志查看最后的异常堆栈；如果刚安装模组，先尝试移除最近安装的模组。",
            fallback,
            LaunchDiagnosisSeverity.Warning,
            latestLog,
            crashReport)
        {
            Symptoms = symptoms,
            InvolvedMods = involved,
        };
    }

    private static LaunchDiagnosisReport BuildBrokenModReport(
        string instanceName,
        int exitCode,
        List<MissingDependency> presentButBroken,
        MissingClassSignal? missingClass,
        MixinApplication? lastMixing,
        MixinVersionWarning? mixinWarning,
        ModIndex mods,
        List<LaunchDiagnosisSymptom> symptoms,
        List<string> involved,
        List<ModFailureBlock> blocks,
        string? latestLog,
        string? crashReport,
        string combined)
    {
        var first = presentButBroken[0];
        var brokenMod = mods.Find(first.MissingModId);
        var brokenName = brokenMod is null
            ? first.MissingModId
            : $"{brokenMod.DisplayName}（{brokenMod.FileName}）";
        if (brokenMod is not null)
        {
            involved.RemoveAll(x => x.StartsWith(first.MissingModId, StringComparison.OrdinalIgnoreCase));
            involved.Insert(0, brokenName);
        }

        var dependents = mods.DependentsOf(first.MissingModId)
            .Where(m => !string.Equals(m.ModId, first.MissingModId, StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.DisplayName}（{m.FileName}）")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chain = dependents.Count == 0
            ? $"{brokenName} 自己没加载成功。"
            : string.Join(" → ", dependents.Append(brokenName));

        symptoms.Add(new LaunchDiagnosisSymptom(
            "加载器报“未安装”的模组",
            $"{first.MissingModId}（请求方：{first.RequestedBy}）的文件其实在 mods 里 —— "
            + "加载器之所以判定它没装，是因为它自己在加载阶段就失败了。"));

        // 如果崩溃前的缺失类能定位到真正的底层前置，就把根因改写为那个前置
        if (missingClass is not null)
        {
            var owner = mods.ExplainClass(missingClass.ClassName);
            if (owner.HasKnownOwner && (owner.ModId is null || !mods.Contains(owner.ModId)))
            {
                var display = owner.DisplayName ?? owner.ModId ?? missingClass.ClassName;
                involved.RemoveAll(x => x.StartsWith(display, StringComparison.OrdinalIgnoreCase));
                involved.Insert(0, display);

                return new LaunchDiagnosisReport(
                    $"缺少底层前置：{display}",
                    $"{brokenName} 需要 {display}（提供 {missingClass.ClassName}），但 mods 里没有它，"
                    + $"于是 {brokenName} 加载失败；加载器因此认为 {first.MissingModId} 没装，"
                    + $"下游 {string.Join("、", dependents.DefaultIfEmpty(first.RequestedBy))} 随之启动失败。",
                    $"安装 {display} 后重试；链条：{chain}。"
                    + "只想先进游戏，就禁用链条最上面那个模组，避免它继续拖垮加载。",
                    BuildClassEvidence(combined, missingClass, lastMixing),
                    LaunchDiagnosisSeverity.Error,
                    latestLog,
                    crashReport)
                {
                    Symptoms = symptoms,
                    InvolvedMods = involved,
                };
            }
        }

        return new LaunchDiagnosisReport(
            "模组加载失败（不是没装）",
            $"{brokenName} 的文件在 mods 目录里，但加载器没注册它：它自己在加载阶段就失败了。"
            + $"依赖它的 {(dependents.Count == 0 ? first.RequestedBy : string.Join("、", dependents))} 因此报“缺少 {first.MissingModId}”。",
            $"看日志里 {first.MissingModId} 之前的第一条异常（通常是缺它自己的前置）；"
            + "定位不到时，先禁用依赖它的模组，确认能进游戏后再逐个加回来。",
            BuildFirstBlockEvidence(blocks, first.MissingModId),
            LaunchDiagnosisSeverity.Error,
            latestLog,
            crashReport)
        {
            Symptoms = symptoms,
            InvolvedMods = involved,
        };
    }

    private static LaunchDiagnosisReport BuildMissingDependencyReport(
        string instanceName,
        int exitCode,
        List<MissingDependency> trulyMissing,
        ModIndex mods,
        List<LaunchDiagnosisSymptom> symptoms,
        List<string> involved,
        string? latestLog,
        string? crashReport,
        string combined)
    {
        var first = trulyMissing[0];
        var dependents = mods.DependentsOf(first.MissingModId)
            .Select(m => $"{m.DisplayName}（{m.FileName}）")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var versionMismatch = !string.IsNullOrWhiteSpace(first.ActualVersion) &&
                              !first.ActualVersion.Equals("MISSING", StringComparison.OrdinalIgnoreCase);

        var title = versionMismatch ? "前置模组版本不匹配" : "缺少前置模组";
        var summary = versionMismatch
            ? $"模组 {first.RequestedBy} 需要 {first.MissingModId} 的版本 {first.ExpectedRange}，"
              + $"当前装的是 {first.ActualVersion}。"
            : $"模组 {first.RequestedBy} 需要 {first.MissingModId}"
              + (string.IsNullOrWhiteSpace(first.ExpectedRange) ? string.Empty : $"（版本 {first.ExpectedRange}）")
              + "，但 mods 目录里没有它。";

        if (dependents.Count > 0)
        {
            summary += " 依赖它的还有：" + string.Join("、", dependents) + "。";
        }

        var lines = new List<string> { summary };
        if (dependents.Count > 0)
        {
            lines.Add("依赖链：" + string.Join(" → ", dependents.Append(first.RequestedBy)) + " → 缺 " + first.MissingModId);
        }

        foreach (var dependent in dependents)
        {
            involved.RemoveAll(x => x.Equals(dependent, StringComparison.OrdinalIgnoreCase));
            involved.Insert(0, dependent);
        }

        return new LaunchDiagnosisReport(
            title,
            summary,
            versionMismatch
                ? $"把 {first.MissingModId} 换成 {first.ExpectedRange} 范围内的版本。"
                : $"安装 {first.MissingModId}；如果不需要 {first.RequestedBy}，直接禁用它更快。",
            BuildFirstBlockEvidence(Array.Empty<ModFailureBlock>(), first.MissingModId, combined),
            LaunchDiagnosisSeverity.Error,
            latestLog,
            crashReport)
        {
            Symptoms = symptoms,
            InvolvedMods = involved,
        };
    }

    /// <summary>在 mods 索引里找“必填依赖无对应 jar”的模组，用于兜底给一点线索。</summary>
    private static string? DescribeDanglingDependencies(ModIndex mods)
    {
        var dangling = new List<string>();
        foreach (var mod in mods.Mods)
        {
            foreach (var dependency in mod.Dependencies)
            {
                if (!dependency.IsRequired || mods.IsBuiltIn(dependency.ModId) || mods.Contains(dependency.ModId))
                {
                    continue;
                }

                dangling.Add($"{mod.DisplayName} 需要 {dependency.ModId}");
                break;
            }

            if (dangling.Count >= 3)
            {
                break;
            }
        }

        return dangling.Count == 0
            ? null
            : "另外 mods 里这些依赖没满足：" + string.Join("；", dangling) + "。";
    }

    private static string DisplayMod(ModIndex? mods, string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return "未知模组";
        }

        return mods?.Find(modId) is { } meta ? $"{meta.DisplayName}（{meta.FileName}）" : modId;
    }

    /// <summary>解析崩溃报告里所有 -- MOD xxx -- 段。</summary>
    private static List<ModFailureBlock> ParseModFailureBlocks(string crashText)
    {
        var blocks = new List<ModFailureBlock>();
        if (string.IsNullOrWhiteSpace(crashText))
        {
            return blocks;
        }

        var lines = crashText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("-- MOD ", StringComparison.OrdinalIgnoreCase) ||
                !line.EndsWith(" --", StringComparison.Ordinal))
            {
                continue;
            }

            var modName = line["-- MOD ".Length..^3].Trim();
            var body = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var next = lines[j].Trim();
                if (next.StartsWith("-- ", StringComparison.Ordinal) &&
                    next.EndsWith(" --", StringComparison.Ordinal))
                {
                    break;
                }

                if (next.StartsWith("Mod loading has failed", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                body.Add(next);
            }

            var bodyText = string.Join('\n', body);
            var modFile = MatchFirst(bodyText, @"Mod File:\s*(?<v>[^\r\n]+)");
            var modVersion = MatchFirst(bodyText, @"Mod Version:\s*(?<v>[^\r\n]+)");
            var failure = MatchFirst(bodyText, @"Failure message:\s*(?<v>[^\r\n]+)");
            var notInstalled = MatchFirst(bodyText, @"Currently,\s*(?<v>[A-Za-z0-9_.\-]+)\s+is not installed");
            var requires = MatchFirst(failure ?? string.Empty, @"requires\s+(?<v>[A-Za-z0-9_.\-]+)");
            var range = MatchFirst(failure ?? string.Empty, @"requires\s+[A-Za-z0-9_.\-]+\s+(?<v>[0-9][^\s]*)");

            blocks.Add(new ModFailureBlock(
                modName,
                modFile,
                modVersion,
                failure,
                notInstalled ?? requires,
                range));
        }

        return blocks;
    }

    /// <summary>解析 Missing and unsupported mandatory dependencies 段（NeoForge/Forge 通用行格式）。</summary>
    private static List<MissingDependency> ParseMissingDependencies(string text)
    {
        var result = new List<MissingDependency>();
        foreach (Match match in Regex.Matches(
                     text,
                     @"Mod ID:\s*'(?<id>[^']+)',\s*Requested by:\s*'(?<by>[^']+)'(?:,\s*Expected range:\s*'(?<range>[^']*)')?(?:,\s*Actual version:\s*'(?<actual>[^']*)')?",
                     RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var id = match.Groups["id"].Value.Trim();
            if (id.Length == 0)
            {
                continue;
            }

            if (result.Any(d => string.Equals(d.MissingModId, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new MissingDependency(
                id,
                match.Groups["by"].Value.Trim(),
                match.Groups["range"].Success ? match.Groups["range"].Value : null,
                match.Groups["actual"].Success ? match.Groups["actual"].Value : null));
        }

        return result;
    }

    private static MixinVersionWarning? TryParseMixinVersionWarning(string text)
    {
        var mixing = Regex.Match(
            text,
            @"Mixing\s+(?<mixin>\S+)\s+from\s+(?<config>\S+)\s+into\s+(?<target>\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!mixing.Success)
        {
            return null;
        }

        var classVersion = Regex.Match(
            text,
            @"Class version\s+(?<required>\d+)\s+required.*?Mixin.*?supports class version\s+(?<supported>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!classVersion.Success)
        {
            return null;
        }

        return new MixinVersionWarning(
            mixing.Groups["mixin"].Value.Trim(),
            mixing.Groups["config"].Value.Trim(),
            mixing.Groups["target"].Value.Trim(),
            classVersion.Groups["required"].Value,
            classVersion.Groups["supported"].Value);
    }

    /// <summary>找崩溃锚点：第一个“致命”信号在合并文本中的位置。</summary>
    private static int FindCrashAnchor(string text)
    {
        var patterns = new[]
        {
            @"Exception in thread """,
            @"MixinTransformerError",
            @"ExceptionInInitializerError",
            @"A fatal error has been detected",
            @"[Ff]atal",
        };

        var best = -1;
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.Multiline);
            if (!match.Success)
            {
                continue;
            }

            if (best < 0 || match.Index < best)
            {
                best = match.Index;
            }
        }

        return best;
    }

    /// <summary>崩溃点之前最后一次 “Mixing X from Y into Z”。</summary>
    private static MixinApplication? FindLastMixing(string text, int anchor)
    {
        var limit = anchor < 0 ? text.Length : anchor;
        Match? last = null;
        foreach (Match match in Regex.Matches(
                     text[..limit],
                     @"Mixing\s+(?<mixin>\S+)\s+from\s+(?<config>\S+)\s+into\s+(?<target>\S+)",
                     RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            last = match;
        }

        if (last is null)
        {
            return null;
        }

        return new MixinApplication(
            last.Groups["mixin"].Value.Trim(),
            last.Groups["config"].Value.Trim(),
            last.Groups["target"].Value.Trim(),
            last.Index);
    }

    /// <summary>
    /// 找“崩溃点之前、最后一次成功注入之后”最近的一条缺失类信号。
    /// 前面那些早期 WARN（例如可选兼容目标缺失）不参与归责。
    /// </summary>
    private static MissingClassSignal? FindNearestMissingClass(string text, int anchor, int afterIndex)
    {
        var signals = new List<MissingClassSignal>();
        AddClassSignals(signals, text, @"Error loading class:\s*(?<cls>[^\s()]+)\s*\(");
        AddClassSignals(signals, text, @"NoClassDefFoundError:\s*(?<cls>[^\s()]+)");
        AddClassSignals(signals, text, @"ClassNotFoundException:\s*(?<cls>[^\s()]+)");

        var relevant = signals
            .Where(s => !IsGameOrLoaderClass(s.ClassName))
            .OrderBy(s => s.Index)
            .ToList();
        if (relevant.Count == 0)
        {
            return null;
        }

        var limit = anchor < 0 ? int.MaxValue : anchor;
        var beforeAnchor = relevant.Where(s => s.Index < limit).ToList();
        if (beforeAnchor.Count == 0)
        {
            beforeAnchor = relevant;
        }

        var afterLastMixing = afterIndex < 0
            ? Array.Empty<MissingClassSignal>()
            : beforeAnchor.Where(s => s.Index > afterIndex).ToArray();

        return afterLastMixing.Length > 0 ? afterLastMixing[^1] : beforeAnchor[^1];
    }

    private static void AddClassSignals(List<MissingClassSignal> output, string text, string pattern)
    {
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var name = match.Groups["cls"].Value.Trim();
            if (name.Length == 0 || name.StartsWith("Could", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            output.Add(new MissingClassSignal(match.Index, name));
        }
    }

    private static bool IsGameOrLoaderClass(string className)
    {
        var name = className.Replace('\\', '/').Replace('.', '/').TrimStart('/');
        return name.StartsWith("net/minecraft/", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("com/mojang/", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("org/spongepowered/", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("cpw/mods/", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("net/minecraftforge/", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("net/neoforged/", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("java/", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("javax/", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildClassEvidence(
        string text,
        MissingClassSignal signal,
        MixinApplication? lastMixing)
    {
        var line = LineAt(text, signal.Index);
        var parts = new List<string> { line };
        if (lastMixing is not null)
        {
            parts.Add($"崩溃点注入：{lastMixing.MixinName} from {lastMixing.Config} into {lastMixing.Target}");
        }

        return string.Join('\n', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string BuildFirstBlockEvidence(
        IReadOnlyList<ModFailureBlock> blocks,
        string modId,
        string? fallbackText = null)
    {
        var block = blocks.FirstOrDefault(b =>
            string.Equals(b.MissingModId, modId, StringComparison.OrdinalIgnoreCase))
            ?? blocks.FirstOrDefault();
        if (block is null)
        {
            // 没有 -- MOD -- 段时，退回 Missing and unsupported dependencies 的原文行
            if (!string.IsNullOrWhiteSpace(fallbackText))
            {
                foreach (var line in fallbackText.Split('\n'))
                {
                    if (line.Contains($"'{modId}'", StringComparison.OrdinalIgnoreCase) &&
                        line.Contains("Requested by", StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Trim();
                    }
                }
            }

            return $"缺失的 modId：{modId}";
        }

        var lines = new List<string> { $"涉及模组：{block.ModName}" };
        if (!string.IsNullOrWhiteSpace(block.ModFile))
        {
            lines.Add($"Mod File：{Path.GetFileName(block.ModFile!.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(block.FailureMessage))
        {
            lines.Add($"Failure message：{block.FailureMessage!.Trim()}");
        }

        return string.Join('\n', lines);
    }

    private static string LineAt(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1));
        start = start < 0 ? 0 : start + 1;
        var end = text.IndexOf('\n', index);
        end = end < 0 ? text.Length : end;
        var line = text[start..end].Trim();
        return line.Length > 400 ? line[..400] : line;
    }

    private static string? MatchFirst(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups["v"].Value.Trim() : null;
    }

    private static bool IsOptionalMixinWarning(string evidence)
    {
        return evidence.Contains("[mixin/]", StringComparison.OrdinalIgnoreCase) &&
               evidence.Contains("Error loading class", StringComparison.OrdinalIgnoreCase) &&
               evidence.Contains("ClassNotFoundException", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindNewestCrashReport(string gameDirectory, DateTime cutoffUtc)
    {
        try
        {
            var directory = Path.Combine(gameDirectory, "crash-reports");
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory.EnumerateFiles(directory, "crash-*.txt")
                .Where(path => File.GetLastWriteTimeUtc(path) >= cutoffUtc)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindNewestCrashReportInDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            return Directory.EnumerateFiles(directory, "crash-*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string ReadTail(string path, int maxLines)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }

            return string.Join('\n', lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildEvidence(string text, Match match)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1));
        if (lineStart < 0)
        {
            lineStart = 0;
        }
        else
        {
            lineStart++;
        }

        var lineEnd = text.IndexOf('\n', match.Index + match.Length);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        var line = text[lineStart..lineEnd].Trim();
        if (line.Length == 0)
        {
            line = match.Value.Trim();
        }

        return line.Length > 500 ? line[..500] : line;
    }

    private static string? FirstUsefulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line =>
                line.Length > 0 &&
                !line.StartsWith("at ", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("...", StringComparison.Ordinal));
    }
}
