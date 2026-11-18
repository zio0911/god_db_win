using System.Globalization;
using System.Text;

namespace GodDB.Core.Redis;

/// RESP2 값의 종류
public enum RespKind { Simple, Error, Integer, Bulk, Array }

/// Redis 응답 값 (RESP2)
public sealed class RespValue : IEquatable<RespValue>
{
    public RespKind Kind { get; }
    /// Simple / Error 의 글
    public string? Text { get; }
    public long Integer { get; }
    /// Bulk 의 바이트 (nil 이면 null)
    public byte[]? Bytes { get; }
    /// Array 의 원소 (nil 이면 null)
    public IReadOnlyList<RespValue>? Items { get; }

    private RespValue(RespKind kind, string? text = null, long integer = 0, byte[]? bytes = null, IReadOnlyList<RespValue>? items = null)
    {
        Kind = kind; Text = text; Integer = integer; Bytes = bytes; Items = items;
    }

    public static RespValue Simple(string s) => new(RespKind.Simple, text: s);
    public static RespValue Error(string s) => new(RespKind.Error, text: s);
    public static RespValue Int(long i) => new(RespKind.Integer, integer: i);
    public static RespValue Bulk(byte[]? b) => new(RespKind.Bulk, bytes: b);
    /// UTF-8 글을 Bulk 로
    public static RespValue BulkString(string s) => Bulk(Encoding.UTF8.GetBytes(s));
    public static RespValue Array(IReadOnlyList<RespValue>? items) => new(RespKind.Array, items: items);
    public static RespValue Arr(params RespValue[] items) => Array(items);
    public static readonly RespValue NilBulk = Bulk(null);
    public static readonly RespValue NilArray = Array(null);

    public bool IsError => Kind == RespKind.Error;

    /// 글자로 (bulk 는 UTF-8, 아니면 redis-cli 처럼 \xNN)
    public string? AsString => Kind switch
    {
        RespKind.Simple or RespKind.Error => Text,
        RespKind.Integer => Integer.ToString(CultureInfo.InvariantCulture),
        RespKind.Bulk => Bytes == null ? null : RedisText.Display(Bytes),
        _ => null,
    };

    public long? AsInt => Kind switch
    {
        RespKind.Integer => Integer,
        RespKind.Bulk or RespKind.Simple => RedisText.ParseInt(AsString),
        _ => null,
    };

    public IReadOnlyList<RespValue>? AsArray => Kind == RespKind.Array ? Items : null;

    public bool IsNil => (Kind == RespKind.Bulk && Bytes == null) || (Kind == RespKind.Array && Items == null);

    /// redis-cli 와 비슷한 모양의 텍스트
    public string Formatted(string indent = "")
    {
        switch (Kind)
        {
            case RespKind.Simple: return Text!;
            case RespKind.Error: return "(error) " + Text;
            case RespKind.Integer: return "(integer) " + Integer.ToString(CultureInfo.InvariantCulture);
        }
        if (IsNil) return "(nil)";
        if (Kind == RespKind.Bulk) return "\"" + RedisText.Display(Bytes!).Replace("\"", "\\\"") + "\"";
        var a = Items!;
        if (a.Count == 0) return "(empty array)";
        int width = a.Count.ToString(CultureInfo.InvariantCulture).Length;
        var lines = new List<string>(a.Count);
        for (int i = 0; i < a.Count; i++)
        {
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            var prefix = new string(' ', width - n.Length) + n + ") ";
            var inner = a[i].Formatted(indent + new string(' ', prefix.Length));
            lines.Add((i == 0 ? "" : indent) + prefix + inner);
        }
        return string.Join("\n", lines);
    }

    public bool Equals(RespValue? o)
    {
        if (o is null || o.Kind != Kind) return false;
        return Kind switch
        {
            RespKind.Simple or RespKind.Error => Text == o.Text,
            RespKind.Integer => Integer == o.Integer,
            RespKind.Bulk => Bytes == null ? o.Bytes == null : o.Bytes != null && Bytes.AsSpan().SequenceEqual(o.Bytes),
            _ => Items == null ? o.Items == null : o.Items != null && Items.SequenceEqual(o.Items),
        };
    }

    public override bool Equals(object? obj) => Equals(obj as RespValue);

    public override int GetHashCode() => HashCode.Combine(Kind, Text, Integer, Bytes?.Length ?? -1, Items?.Count ?? -1);

    public override string ToString() => Formatted();
}

/// Redis 서버 오류 응답 (-ERR …)
public sealed class RedisServerException(string message) : Exception(message)
{
    /// WRONGPASS, NOAUTH, ERR … 같은 앞 단어
    public string Code => Message.Split(' ')[0];
}

/// RESP 형식이 깨졌을 때
public sealed class RedisProtocolException(string detail) : Exception("Redis protocol error: " + detail);

/// 연결이 끊겼거나 열리지 않았을 때 (Mac 판 DBError.notConnected)
public sealed class RedisNotConnectedException : Exception
{
    public RedisNotConnectedException() : base(L10n.Tr("DB 연결이 끊어졌어요. 다시 연결해 주세요.")) { }
    public RedisNotConnectedException(Exception inner) : base(L10n.Tr("DB 연결이 끊어졌어요. 다시 연결해 주세요."), inner) { }
}

public static class RedisText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// 바이트를 화면 글자로. UTF-8 이면 그대로, 아니면 출력 불가 바이트를 \xNN 으로.
    public static string Display(byte[] bytes)
    {
        if (System.Array.IndexOf(bytes, (byte)0) < 0)
        {
            try { return StrictUtf8.GetString(bytes); } catch (DecoderFallbackException) { }
        }
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else if (b == 0x0A) sb.Append("\\n");
            else if (b == 0x0D) sb.Append("\\r");
            else if (b == 0x09) sb.Append("\\t");
            else sb.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// Swift Int("…") 처럼 엄격하게 정수로 (앞뒤 공백 불가)
    internal static long? ParseInt(string? s) =>
        s != null && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static readonly char[] Newlines = ['\n', '\r', '\u000B', '\u000C', '\u0085', (char)0x2028, (char)0x2029];

    /// Workbench 편집기 글을 실행할 명령 줄들로 (빈 줄, // 와 # 주석 줄은 건너뜀)
    public static List<string> ScriptLines(string text) =>
        text.Split(Newlines)
            .Select(l => l.Trim(' ', '\t'))
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith('#'))
            .ToList();

    /// 콘솔 한 줄을 인자로 나눈다. "…" '…' 따옴표와 \n \t \xNN 이스케이프 지원 (redis-cli 와 같은 규칙)
    public static List<string>? SplitArgs(string line)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool inArg = false;
        char? quote = null;
        var it = line;
        int i = 0;
        while (i < it.Length)
        {
            char c = it[i];
            if (quote is char q)
            {
                if (c == '\\' && q == '"' && i + 1 < it.Length)
                {
                    char n = it[i + 1];
                    switch (n)
                    {
                        case 'n': cur.Append('\n'); i += 2; continue;
                        case 't': cur.Append('\t'); i += 2; continue;
                        case 'r': cur.Append('\r'); i += 2; continue;
                        case 'x' when i + 3 < it.Length:
                            if (byte.TryParse(it.AsSpan(i + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
                            {
                                cur.Append((char)v); i += 4; continue;
                            }
                            break;
                        default: cur.Append(n); i += 2; continue;
                    }
                }
                if (c == '\\' && q == '\'' && i + 1 < it.Length && it[i + 1] == '\'')
                {
                    cur.Append('\''); i += 2; continue;
                }
                if (c == q)
                {
                    quote = null;
                    // 닫는 따옴표 뒤에는 공백이 와야 한다
                    if (i + 1 < it.Length && !char.IsWhiteSpace(it[i + 1])) return null;
                }
                else cur.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inArg) { args.Add(cur.ToString()); cur.Clear(); inArg = false; }
            }
            else if ((c == '"' || c == '\'') && !inArg)
            {
                quote = c;
                inArg = true;
            }
            else
            {
                cur.Append(c);
                inArg = true;
            }
            i += 1;
        }
        if (quote != null) return null;
        if (inArg) args.Add(cur.ToString());
        return args;
    }
}

/// RESP2 인코딩 / 디코딩
public static class RespCoder
{
    public static byte[] Encode(IReadOnlyList<byte[]> args)
    {
        using var ms = new MemoryStream(64);
        EncodeInto(args, ms);
        return ms.ToArray();
    }

    internal static void EncodeInto(IReadOnlyList<byte[]> args, Stream s)
    {
        void W(string t) { var b = Encoding.ASCII.GetBytes(t); s.Write(b, 0, b.Length); }
        W("*" + args.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
        foreach (var a in args)
        {
            W("$" + a.Length.ToString(CultureInfo.InvariantCulture) + "\r\n");
            s.Write(a, 0, a.Length);
            W("\r\n");
        }
    }

    /// 버퍼에서 값 하나를 읽는다. 아직 다 안 왔으면 null 이고 consumed 는 0 (읽은 위치 그대로).
    public static RespValue? Decode(ReadOnlySpan<byte> buffer, out int consumed)
    {
        int pos = 0;
        var v = Parse(buffer, ref pos);
        consumed = v == null ? 0 : pos;
        return v;
    }

    private static string? Line(ReadOnlySpan<byte> b, ref int pos)
    {
        var rest = b[pos..];
        int cr = rest.IndexOf((byte)0x0D);
        if (cr < 0 || cr + 1 >= rest.Length || rest[cr + 1] != 0x0A) return null;
        var s = Encoding.UTF8.GetString(rest[..cr]);
        pos += cr + 2;
        return s;
    }

    private static RespValue? Parse(ReadOnlySpan<byte> b, ref int pos)
    {
        if (pos >= b.Length) return null;
        byte type = b[pos++];
        var l = Line(b, ref pos);
        if (l == null) return null;
        switch ((char)type)
        {
            case '+': return RespValue.Simple(l);
            case '-': return RespValue.Error(l);
            case ':':
                return RespValue.Int(RedisText.ParseInt(l) ?? throw new RedisProtocolException("integer " + l));
            case '$':
                {
                    long n = RedisText.ParseInt(l) ?? throw new RedisProtocolException("bulk " + l);
                    if (n < 0) return RespValue.NilBulk;
                    if (b.Length - pos < n + 2) return null;
                    var bytes = b.Slice(pos, (int)n).ToArray();
                    pos += (int)n + 2;
                    return RespValue.Bulk(bytes);
                }
            case '*':
                {
                    long n = RedisText.ParseInt(l) ?? throw new RedisProtocolException("array " + l);
                    if (n < 0) return RespValue.NilArray;
                    var items = new List<RespValue>((int)Math.Min(n, 1 << 16));
                    for (long i = 0; i < n; i++)
                    {
                        var v = Parse(b, ref pos);
                        if (v == null) return null;
                        items.Add(v);
                    }
                    return RespValue.Array(items);
                }
            default:
                throw new RedisProtocolException("type byte " + type.ToString(CultureInfo.InvariantCulture));
        }
    }
}
