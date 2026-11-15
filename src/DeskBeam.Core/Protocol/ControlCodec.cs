using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBeam.Core.Protocol;

/// <summary>
/// Serializes control messages with a type discriminator so the receiving end can
/// reconstruct the concrete record without relying on reflection-based polymorphism.
/// </summary>
public static class ControlCodec
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Dictionary<string, Type> TypesByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, string> NamesByType = new();

    static ControlCodec()
    {
        foreach (var type in typeof(ControlMessage).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(ControlMessage).IsAssignableFrom(type))
                continue;

            var name = type.Name;
            TypesByName[name] = type;
            NamesByType[type] = name;
        }
    }

    public static byte[] Encode(ControlMessage message)
    {
        var type = message.GetType();
        if (!NamesByType.TryGetValue(type, out var name))
            throw new InvalidOperationException($"Unregistered control message: {type.FullName}");

        var payload = JsonSerializer.SerializeToElement(message, type, JsonOptions);
        var envelope = new Envelope(name, payload);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    public static ControlMessage? Decode(ReadOnlySpan<byte> utf8)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(utf8, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.Type) || envelope.Data.ValueKind == JsonValueKind.Undefined)
            return null;

        if (!TypesByName.TryGetValue(envelope.Type, out var type))
            return null;

        try
        {
            return (ControlMessage?)envelope.Data.Deserialize(type, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Envelope(string Type, JsonElement Data)
    {
        // Parameterless ctor for the deserializer when the payload property is missing.
        public Envelope() : this(string.Empty, default) { }
    }
}
