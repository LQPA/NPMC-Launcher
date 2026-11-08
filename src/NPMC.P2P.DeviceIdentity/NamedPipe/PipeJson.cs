using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPMC.P2P.DeviceIdentity.NamedPipe;

public static class PipeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(byte[] utf8)
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(utf8, Options);
        }
        catch (JsonException ex)
        {
            throw new PipeProtocolException(
                Contracts.NamedPipe.PipeErrorCodes.InvalidRequest,
                $"JSON 解析失败：{ex.Message}");
        }

        return value
               ?? throw new PipeProtocolException(
                   Contracts.NamedPipe.PipeErrorCodes.InvalidRequest,
                   "JSON 反序列化结果为空。");
    }

    public static bool TryDeserialize<T>(byte[] utf8, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(utf8, Options);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
