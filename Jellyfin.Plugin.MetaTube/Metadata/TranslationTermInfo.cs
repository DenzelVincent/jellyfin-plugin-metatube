using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTube.Metadata;

/// <summary>服务端术语字典的一条查询结果。</summary>
public class TranslationTermInfo
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("translated_text")]
    public string TranslatedText { get; set; }
}

/// <summary>PUT /v1/translations/term 的请求体，字段名与 Go 端 json tag 对齐。</summary>
public class TranslationTermRequest
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; }

    [JsonPropertyName("target_lang")]
    public string TargetLang { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; }

    [JsonPropertyName("translated_text")]
    public string TranslatedText { get; set; }

    [JsonPropertyName("engine")]
    public string Engine { get; set; }
}

/// <summary>
/// 服务端没有术语字典接口（通常是旧版本）。上层收到它应降级为
/// 「直接调引擎翻译、不落库」，不能当成普通 API 错误打断刮削。
/// </summary>
public sealed class TranslationTermApiUnavailableException : Exception
{
    public TranslationTermApiUnavailableException()
        : base("Translation term API is not available on the server")
    {
    }
}
