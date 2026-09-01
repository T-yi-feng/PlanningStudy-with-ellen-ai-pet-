using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KaoyanPlanner.WPF.Services;

/// <summary>
/// 本地密钥文件 secret.json 读写（移植 secret.py，.gitignore，绝不入库）。
/// 存放不入库的凭据：api_key（GLM 对话）、asr_api_key（硅基流动语音识别）等。
/// </summary>
public static class SecretService
{
    public static string Get(string key, string def = "")
    {
        try
        {
            if (File.Exists(AppPaths.SecretFile))
            {
                var node = JsonNode.Parse(File.ReadAllText(AppPaths.SecretFile, Encoding.UTF8));
                if (node is JsonObject obj && obj[key] is JsonValue v && v.TryGetValue<string>(out var s))
                    return s ?? "";
            }
        }
        catch
        {
            // 文件不存在/损坏 → 返回默认
        }
        return def;
    }

    public static void Set(string key, string value)
    {
        try
        {
            JsonObject data;
            if (File.Exists(AppPaths.SecretFile))
                data = JsonNode.Parse(File.ReadAllText(AppPaths.SecretFile, Encoding.UTF8)) as JsonObject ?? new JsonObject();
            else
                data = new JsonObject();
            data[key] = value;
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(AppPaths.SecretFile, data.ToJsonString(opts), new UTF8Encoding(false));
        }
        catch
        {
            // 只在有写权限时成功；失败静默
        }
    }
}
