using System.Text;
using NPMC.P2P.Contracts.Validation;

namespace NPMC.P2P.Contracts.Signing;

/// <summary>
/// 规范化签名声明。
///
/// 线上格式（UTF-8，LF 换行，末尾必须有一个 LF）：
/// <code>
/// nmpc-p2p/v1
/// type=auth-challenge
/// challenge=...
/// client_nonce=...
/// ...
/// </code>
///
/// 约束：
/// - 首行固定为 <see cref="P2PProtocol.StatementPrefix"/>；
/// - 第二行必须是 type；
/// - 其余字段按 key 的 ordinal 顺序排列，不允许重复、缺省或乱序；
/// - 解析时会重新序列化并与输入逐字节比较，拒绝任何非规范形式。
///
/// 这样 Gateway / Named Pipe / 测试对“签的到底是什么字节”只有一个答案。
/// </summary>
public sealed class SigningStatement
{
    public const int MaxFields = 24;
    public const int MaxValueLength = 512;
    public const int MaxStatementBytes = 4096;
    public const string TypeField = "type";

    private readonly KeyValuePair<string, string>[] _fields;

    private SigningStatement(string type, KeyValuePair<string, string>[] sortedFields)
    {
        Type = type;
        _fields = sortedFields;
    }

    public string Type { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Fields => _fields;

    public static SigningStatement Create(string type, IEnumerable<KeyValuePair<string, string>> fields)
    {
        ValidateType(type);

        var list = fields.ToArray();
        if (list.Length > MaxFields)
        {
            throw new ArgumentException($"声明字段过多（上限 {MaxFields}）。", nameof(fields));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in list)
        {
            ValidateFieldKey(field.Key);
            ValidateFieldValue(field.Value, field.Key);
            if (!seen.Add(field.Key))
            {
                throw new ArgumentException($"声明字段重复：{field.Key}", nameof(fields));
            }
        }

        Array.Sort(list, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return new SigningStatement(type, list);
    }

    public string GetField(string key)
    {
        foreach (var field in _fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal))
            {
                return field.Value;
            }
        }

        throw new KeyNotFoundException($"声明中不存在字段 {key}。");
    }

    public bool TryGetField(string key, out string value)
    {
        foreach (var field in _fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal))
            {
                value = field.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public byte[] ToUtf8Bytes()
    {
        var sb = new StringBuilder(128);
        sb.Append(P2PProtocol.StatementPrefix).Append('\n');
        sb.Append(TypeField).Append('=').Append(Type).Append('\n');
        foreach (var field in _fields)
        {
            sb.Append(field.Key).Append('=').Append(field.Value).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public override string ToString() => Encoding.UTF8.GetString(ToUtf8Bytes());

    public static SigningStatement Parse(ReadOnlySpan<byte> utf8)
    {
        if (!TryParse(utf8, out var statement, out var error))
        {
            throw new FormatException(error);
        }

        return statement!;
    }

    public static bool TryParse(ReadOnlySpan<byte> utf8, out SigningStatement? statement)
    {
        return TryParse(utf8, out statement, out _);
    }

    public static bool TryParse(ReadOnlySpan<byte> utf8, out SigningStatement? statement, out string error)
    {
        statement = null;
        error = string.Empty;

        if (utf8.Length == 0 || utf8.Length > MaxStatementBytes)
        {
            error = $"声明长度必须在 1-{MaxStatementBytes} 字节之间。";
            return false;
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            error = "声明不是合法的 UTF-8。";
            return false;
        }

        if (!text.EndsWith('\n'))
        {
            error = "声明必须以 LF 结尾。";
            return false;
        }

        var lines = text.Split('\n');
        // 末尾空串来自结尾 LF。
        if (lines.Length < 3 || lines[^1].Length != 0)
        {
            error = "声明结构不完整。";
            return false;
        }

        if (!string.Equals(lines[0], P2PProtocol.StatementPrefix, StringComparison.Ordinal))
        {
            error = "声明首行不是受支持的协议版本。";
            return false;
        }

        var typeLine = lines[1];
        if (!typeLine.StartsWith(TypeField + "=", StringComparison.Ordinal))
        {
            error = "声明第二行必须是 type。";
            return false;
        }

        var type = typeLine[(TypeField.Length + 1)..];
        try
        {
            ValidateType(type);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        var fields = new List<KeyValuePair<string, string>>(lines.Length - 3);
        var previousKey = string.Empty;
        for (var i = 2; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                error = $"第 {i + 1} 行不是 key=value。";
                return false;
            }

            var key = line[..eq];
            var value = line[(eq + 1)..];
            try
            {
                ValidateFieldKey(key);
                ValidateFieldValue(value, key);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }

            if (previousKey.Length > 0 && string.CompareOrdinal(previousKey, key) >= 0)
            {
                error = $"字段必须按 ordinal 升序且不得重复：{previousKey} -> {key}";
                return false;
            }

            previousKey = key;
            fields.Add(new KeyValuePair<string, string>(key, value));
        }

        var parsed = new SigningStatement(type, fields.ToArray());

        // 关键：非规范形式（例如字段顺序不同、缺少末尾 LF）一律拒绝。
        if (!utf8.SequenceEqual(parsed.ToUtf8Bytes()))
        {
            error = "声明不是规范形式。";
            return false;
        }

        statement = parsed;
        return true;
    }

    private static void ValidateType(string type)
    {
        if (string.IsNullOrEmpty(type) || type.Length > 32)
        {
            throw new ArgumentException("type 长度必须在 1-32 之间。", nameof(type));
        }

        if (type[0] < 'a' || type[0] > 'z')
        {
            throw new ArgumentException("type 必须以小写字母开头。", nameof(type));
        }

        foreach (var c in type)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok)
            {
                throw new ArgumentException($"type 含非法字符：{type}", nameof(type));
            }
        }
    }

    private static void ValidateFieldKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 32)
        {
            throw new ArgumentException($"字段名长度必须在 1-32 之间：{key}");
        }

        if (key[0] < 'a' || key[0] > 'z')
        {
            throw new ArgumentException($"字段名必须以小写字母开头：{key}");
        }

        foreach (var c in key)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
            if (!ok)
            {
                throw new ArgumentException($"字段名含非法字符：{key}");
            }
        }
    }

    private static void ValidateFieldValue(string value, string key)
    {
        if (value.Length is 0 or > MaxValueLength)
        {
            throw new ArgumentException($"字段 {key} 的值长度必须在 1-{MaxValueLength} 之间。");
        }

        foreach (var c in value)
        {
            if (c is '\r' or '\n' or '\0')
            {
                throw new ArgumentException($"字段 {key} 的值不得包含换行或 NUL。");
            }
        }
    }
}
