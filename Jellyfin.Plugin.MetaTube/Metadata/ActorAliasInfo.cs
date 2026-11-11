using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTube.Metadata;

/// <summary>演员别名表里的一条名字。</summary>
public class ActorAliasNameInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("lang")]
    public string Lang { get; set; }
}

/// <summary>演员身份（一个 person 下的名字集合的宿主）。</summary>
public class ActorAliasPersonInfo
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; }

    [JsonPropertyName("actor_id")]
    public string ActorId { get; set; }
}

/// <summary>查询/写入演员别名的统一返回。</summary>
public class ActorAliasResult
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("ambiguous")]
    public bool Ambiguous { get; set; }

    [JsonPropertyName("person")]
    public ActorAliasPersonInfo Person { get; set; }

    [JsonPropertyName("names")]
    public ActorAliasNameInfo[] Names { get; set; }

    [JsonPropertyName("translated_name")]
    public string TranslatedName { get; set; }

    [JsonPropertyName("matches")]
    public ActorAliasPersonInfo[] Matches { get; set; }

    [JsonPropertyName("conflicts")]
    public string[] Conflicts { get; set; }
}

public class ActorAliasTranslationInfo
{
    [JsonPropertyName("lang")]
    public string Lang { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; }
}

/// <summary>PUT /v1/actor-aliases 的请求体。</summary>
public class ActorAliasRequest
{
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; }

    [JsonPropertyName("actor_id")]
    public string ActorId { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; }

    [JsonPropertyName("names")]
    public ActorAliasNameInfo[] Names { get; set; }

    [JsonPropertyName("translation")]
    public ActorAliasTranslationInfo Translation { get; set; }
}

/// <summary>
/// 服务端没有演员别名接口（通常是旧版本）。上层收到它应降级为
/// 「只按演员搜索解析」，不能当成普通 API 错误打断刮削。
/// </summary>
public sealed class ActorAliasApiUnavailableException : Exception
{
    public ActorAliasApiUnavailableException()
        : base("Actor alias API is not available on the server")
    {
    }
}
