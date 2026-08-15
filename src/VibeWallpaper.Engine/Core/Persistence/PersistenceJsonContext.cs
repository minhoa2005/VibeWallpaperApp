using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Core.Persistence;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = [typeof(WallpaperIdJsonConverter)])]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PersistedState))]
[JsonSerializable(typeof(WindowPlacementSettings))]
[JsonSerializable(typeof(WallpaperLibraryItem))]
[JsonSerializable(typeof(WallpaperAssignment))]
[JsonSerializable(typeof(PersistedDisplayGroup))]
[JsonSerializable(typeof(WallpaperDefinition))]
[JsonSerializable(typeof(WallpaperSource))]
[JsonSerializable(typeof(VideoSource))]
[JsonSerializable(typeof(WebSource))]
[JsonSerializable(typeof(SolidColorSource))]
[JsonSerializable(typeof(MonitorIdentity))]
[JsonSerializable(typeof(JsonObject))]
public partial class PersistenceJsonContext : JsonSerializerContext;

public sealed class WallpaperIdJsonConverter : JsonConverter<WallpaperId>
{
    public override WallpaperId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Wallpaper ID must be an object.");
        }

        Guid value = Guid.Empty;
        var foundValue = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Invalid wallpaper ID.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("Incomplete wallpaper ID.");
            }

            if (string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase))
            {
                if (foundValue || reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out value))
                {
                    throw new JsonException("Wallpaper ID value must be one GUID string.");
                }

                foundValue = true;
            }
            else
            {
                reader.Skip();
            }
        }

        try
        {
            return foundValue ? new WallpaperId(value) : throw new JsonException("Wallpaper ID value is required.");
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Wallpaper ID cannot be empty.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, WallpaperId value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("value", value.Value);
        writer.WriteEndObject();
    }
}
