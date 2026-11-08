using System.Buffers.Binary;

namespace NPMC.P2P.DeviceIdentity.NamedPipe;

public sealed class PipeProtocolException : Exception
{
    public PipeProtocolException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// 帧格式：4 字节小端长度 + UTF-8 JSON 主体；单帧上限由调用方传入。
/// 零长度、超限、半包 EOF 都属于协议错误，直接拒绝。
/// </summary>
public static class PipeFraming
{
    public static async ValueTask<byte[]?> ReadMessageAsync(
        Stream stream,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];

        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(header.AsMemory(1, 3), cancellationToken).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxMessageBytes)
        {
            throw new PipeProtocolException(
                Contracts.NamedPipe.PipeErrorCodes.InvalidFrame,
                $"帧长度非法：{length}（上限 {maxMessageBytes}）。");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public static async ValueTask WriteMessageAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        if (payload.Length == 0 || payload.Length > maxMessageBytes)
        {
            throw new PipeProtocolException(
                Contracts.NamedPipe.PipeErrorCodes.InvalidFrame,
                $"响应长度非法：{payload.Length}。");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
