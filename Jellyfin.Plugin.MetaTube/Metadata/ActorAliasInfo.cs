using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTube.Metadata;

/// <summary>演员身份：一个艺名（搜索名）+ 一个译名。</summary>
public class ActorAliasPersonInfo
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("stage_name")]
    public string StageName { get; set; }

    [JsonPropertyName("translated_name")]
    public string TranslatedName { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; }

    [JsonPropertyName("actor_id")]
    public string ActorId { get; set; }
}

/// <summary>查询演员身份的返回。</summary>
public class ActorAliasResult
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("ambiguous")]
    public bool Ambiguous { get; set; }

    [JsonPropertyName("person")]
    public ActorAliasPersonInfo Person { get; set; }

    [JsonPropertyName("matches")]
    public ActorAliasPersonInfo[] Matches { get; set; }
}

/// <summary>PUT /v1/actor-aliases 的请求体。</summary>
public class ActorAliasRequest
{
    [JsonPropertyName("stage_name")]
    public string StageName { get; set; }

    [JsonPropertyName("translated_name")]
    public string TranslatedName { get; set; }

    [JsonPropertyName("translation_source")]
    public string TranslationSource { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; }

    [JsonPropertyName("actor_id")]
    public string ActorId { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; }
}

/// <summary>POST /v1/actor-aliases/lookup 的请求体。</summary>
public class ActorAliasLookupRequest
{
    [JsonPropertyName("names")]
    public string[] Names { get; set; }
}

/// <summary>批量查询里单个名字的结果。</summary>
public class ActorAliasLookupItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("ambiguous")]
    public bool Ambiguous { get; set; }

    [JsonPropertyName("person")]
    public ActorAliasPersonInfo Person { get; set; }

    [JsonPropertyName("matches")]
    public ActorAliasPersonInfo[] Matches { get; set; }
}

public class ActorAliasLookupResult
{
    [JsonPropertyName("items")]
    public ActorAliasLookupItem[] Items { get; set; }
}

/// <summary>
/// 服务端没有演员身份接口（通常是旧版本）。上层收到它应降级为
/// 「只按演员搜索解析」，不能当成普通 API 错误打断刮削。
/// </summary>
public sealed class ActorAliasApiUnavailableException : Exception
{
    public ActorAliasApiUnavailableException()
        : base("Actor alias API is not available on the server")
    {
    }
}
