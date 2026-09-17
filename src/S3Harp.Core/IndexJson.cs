using System.Text.Json.Serialization;

namespace S3Harp.Core;

/// <summary>The JSON shapes the metadata index stores in its columns, serialized without reflection.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<CompletedPart>))]
[JsonSerializable(typeof(ContentHeaders))]
internal sealed partial class IndexJsonContext : JsonSerializerContext;

/// <summary>The stored checksum, with its algorithm and type written by name.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(Checksum))]
internal sealed partial class ChecksumJsonContext : JsonSerializerContext;
