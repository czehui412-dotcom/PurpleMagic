using System.Text.Json.Serialization;

namespace ServerS5A3;

// Explicit metadata keeps the shared wire contract usable by the native AOT host.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PlayerProfile))]
[JsonSerializable(typeof(PlayerPreferences))]
[JsonSerializable(typeof(LoginReply))]
[JsonSerializable(typeof(AccountRequest))]
[JsonSerializable(typeof(ContextRequest))]
[JsonSerializable(typeof(ContextReply))]
[JsonSerializable(typeof(CdkRequest))]
[JsonSerializable(typeof(CdkReply))]
[JsonSerializable(typeof(CdkCharacter))]
[JsonSerializable(typeof(CdkCharactersReply))]
[JsonSerializable(typeof(SignedEnvelope))]
[JsonSerializable(typeof(UpdateSettings))]
[JsonSerializable(typeof(ReleaseManifest))]
public partial class CoreJson : JsonSerializerContext { }

public sealed record AccountRequest(string account, string password, string device)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? diagnosticCorrelationId { get; init; }
    public string? newPassword { get; init; }
    public bool requireQQBinding { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? invite { get; init; }
}

// CDK 兑换码（服务端 dnfbridge/cdk.go 的 /api/cdk/* 端点，账号密码验证由服务端完成）。
public sealed record CdkRequest(string account, string password, string code, string characterId);
public sealed record CdkReply(bool Ok, string? Message, string? Kind, long Cera, long ItemId, int ItemCount, string? CharacterId);
public sealed record CdkCharacter(string CharacterId, string Name, int Level, string Job);
public sealed record CdkCharactersReply(bool Ok, string? Message, CdkCharacter[] Characters);
