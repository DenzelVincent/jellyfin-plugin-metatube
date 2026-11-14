using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTube.Metadata;

/// <summary>演员身份：一个艺名 + 来源 pid。译名已废除，显示名即艺名。</summary>
public class ActorAliasPersonInfo
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("stage_name")]
    public string StageName { get; set; }

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

/// <summary>
/// PUT /v1/actor-aliases 的请求体：候选名字池由服务端按汉字数优选艺名。
/// source 取 auto（解析器）或 person-edit（手动改名写回）。
/// </summary>
public class ActorAliasRequest
{
    [JsonPropertyName("names")]
    public string[] Names { get; set; }

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
