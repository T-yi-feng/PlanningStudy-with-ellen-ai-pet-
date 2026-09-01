namespace KaoyanPlanner.WPF.Services;

/// <summary>字幕语言值 → 中文标签（黑板头部与设置共用，从 CaptionSettingsWindow 迁出）。</summary>
public static class CaptionLabels
{
    public static string LangLabel(string value) => value switch
    {
        "zh" => "中文",
        "en" => "英文",
        "yue" => "粤语",
        "ja" => "日语",
        "ko" => "韩语",
        _ => "自动识别",
    };
}
