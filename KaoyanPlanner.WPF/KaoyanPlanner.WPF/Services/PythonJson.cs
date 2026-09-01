using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// Python 兼容的 JSON 写出器（保证 data.json 字节级往返）。
///
/// storage.py 用 json.dump(ensure_ascii=False, indent=2) 落盘，其浮点/转义规则与 .NET 默认不同：
///   浮点：Python 对整数值浮点写 "24.0"，.NET ToJsonString 会写 "24"（往返测试必挂）。
///   转义：ensure_ascii=False = 中文原样 UTF-8（.NET 需 UnsafeRelaxedJsonEscaping）。
/// 这里手写遍历 JsonObject，按 Python json.dump 的格式逐字节输出。
/// </summary>
public static class PythonJson
{
    // 只做字符串转义：引号/反斜杠/控制字符，Unicode 原样（与 ensure_ascii=False 等价）。
    private static readonly JsonSerializerOptions StringOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>整棵 JsonNode 按 Python json.dump(ensure_ascii=False, indent=2) 写出（无尾随换行）。</summary>
    public static string ToJson(JsonNode? node, int indentSize = 2)
    {
        var sb = new StringBuilder();
        WriteNode(sb, node, 0, indentSize);
        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, JsonNode? node, int depth, int indentSize)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
                WriteObject(sb, obj, depth, indentSize);
                return;
            case JsonArray arr:
                WriteArray(sb, arr, depth, indentSize);
                return;
            case JsonValue val:
                WriteScalar(sb, val);
                return;
            default:
                sb.Append("null");
                return;
        }
    }

    private static void WriteObject(StringBuilder sb, JsonObject obj, int depth, int indentSize)
    {
        if (obj.Count == 0)
        {
            sb.Append("{}");
            return;
        }
        sb.Append('{');
        int i = 0;
        foreach (var kv in obj)
        {
            sb.Append("\r\n");
            sb.Append(' ', (depth + 1) * indentSize);
            sb.Append(JsonSerializer.Serialize(kv.Key, StringOpts));
            sb.Append(": ");
            WriteNode(sb, kv.Value, depth + 1, indentSize);
            if (++i < obj.Count)
                sb.Append(',');
        }
        sb.Append("\r\n");
        sb.Append(' ', depth * indentSize);
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, JsonArray arr, int depth, int indentSize)
    {
        if (arr.Count == 0)
        {
            sb.Append("[]");
            return;
        }
        sb.Append('[');
        for (int i = 0; i < arr.Count; i++)
        {
            sb.Append("\r\n");
            sb.Append(' ', (depth + 1) * indentSize);
            WriteNode(sb, arr[i], depth + 1, indentSize);
            if (i < arr.Count - 1)
                sb.Append(',');
        }
        sb.Append("\r\n");
        sb.Append(' ', depth * indentSize);
        sb.Append(']');
    }

    private static void WriteScalar(StringBuilder sb, JsonValue val)
    {
        if (val.TryGetValue<bool>(out bool b))
        {
            sb.Append(b ? "true" : "false");
            return;
        }

        // 解析出来的值：底层是 JsonElement，据此区分整数/浮点/字符串。
        if (val.TryGetValue<JsonElement>(out JsonElement el))
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out long li))
                    {
                        sb.Append(li.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                    if (el.TryGetDouble(out double di))
                    {
                        sb.Append(PythonFloat(di));
                        return;
                    }
                    break;
                case JsonValueKind.String:
                    sb.Append(JsonSerializer.Serialize(el.GetString(), StringOpts));
                    return;
                case JsonValueKind.True:
                    sb.Append("true");
                    return;
                case JsonValueKind.False:
                    sb.Append("false");
                    return;
                case JsonValueKind.Null:
                    sb.Append("null");
                    return;
            }
            sb.Append(val.ToJsonString());
            return;
        }

        // 代码里新建的值：底层是 CLR 类型（int/long/double/string/bool）。
        if (val.TryGetValue<long>(out long l))
        {
            sb.Append(l.ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (val.TryGetValue<int>(out int iv))
        {
            sb.Append(iv.ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (val.TryGetValue<double>(out double d))
        {
            sb.Append(PythonFloat(d));
            return;
        }
        if (val.TryGetValue<string>(out string? s))
        {
            sb.Append(JsonSerializer.Serialize(s, StringOpts));
            return;
        }
        sb.Append(val.ToJsonString());
    }

    /// <summary>
    /// Python repr(float) 等价格式：最短往返 + 整数值补 ".0"。
    /// 例：24.0 → "24.0"（.NET "R" 给 "24"，须补 .0）；61.2 → "61.2"；0.034 → "0.034"。
    /// 极值走科学计数法：.NET "1E+20" → Python "1e+20"。
    /// </summary>
    public static string PythonFloat(double value)
    {
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (double.IsNaN(value)) return "NaN";

        string s = value.ToString("R", CultureInfo.InvariantCulture);
        int e = s.IndexOf('E');
        if (e >= 0)
        {
            // "1E+20" / "1E-05" → "1e+20" / "1e-05"
            return s[..e] + "e" + s[(e + 1)..];
        }
        if (!s.Contains('.'))
            return s + ".0";
        return s;
    }
}
