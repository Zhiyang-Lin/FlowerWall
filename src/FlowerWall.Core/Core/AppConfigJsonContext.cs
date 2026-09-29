using System.Text.Json.Serialization;

namespace FlowerWall.Core;

/// <summary>
/// System.Text.Json 源生成上下文。
/// 使用源生成而非反射序列化：启动更快，且将来若启用裁剪不会因为反射而丢字段。
/// 新增可序列化的配置类型时，记得在下面的列表里补一行。
/// </summary>
/// <remarks>
/// 枚举用字符串形式（<c>"mode": "Image"</c>）而不是数字（<c>"mode": 2</c>）：
/// 配置文件是给人看、给人改的，数字含义不明显，而且一旦枚举顺序变化就会改错语义。
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppConfig))]
public sealed partial class AppConfigJsonContext : JsonSerializerContext
{
}
