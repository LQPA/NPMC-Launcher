using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPMC.Launcher.Infrastructure.Storage;

/// <summary>
/// 配置类 JSON 的统一序列化选项。
///
/// 三个选择都是为了让文件**可以手改**：
///   - WriteIndented：换行缩进，diff 友好
///   - JsonStringEnumConverter：枚举写成 "Bmclapi" 而不是 1
///   - UnsafeRelaxedJsonEscaping：中文不转成 \uXXXX
///     （名字里的 Unsafe 指的是不转义 HTML 敏感字符，对配置文件没有影响）
/// </summary>
internal static class JsonConfig
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
