using System.Text;
using System.Text.Json;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Rendering.Web;

public enum WebMessageValidationError
{
    None,
    TooLarge,
    MalformedJson,
    InvalidEnvelope,
    UnsupportedSchemaVersion,
    UnknownType,
    InvalidPayload,
}

public sealed record WebMessageValidationResult(
    bool IsValid,
    WebMessageEnvelope? Message,
    WebMessageValidationError Error)
{
    public static WebMessageValidationResult Valid(WebMessageEnvelope message) =>
        new(true, message, WebMessageValidationError.None);

    public static WebMessageValidationResult Invalid(WebMessageValidationError error) =>
        new(false, null, error);
}

public static class WebMessageValidator
{
    public const int MaximumUtf8Bytes = 64 * 1024;

    public static WebMessageValidationResult Validate(string? json)
    {
        if (json is null)
        {
            return WebMessageValidationResult.Invalid(WebMessageValidationError.MalformedJson);
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumUtf8Bytes)
        {
            return WebMessageValidationResult.Invalid(WebMessageValidationError.TooLarge);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion)
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return WebMessageValidationResult.Invalid(WebMessageValidationError.InvalidEnvelope);
            }

            if (schemaVersion != 1)
            {
                return WebMessageValidationResult.Invalid(WebMessageValidationError.UnsupportedSchemaVersion);
            }

            var typeName = type.GetString();
            if (typeName is not ("ready" or "interaction-state"))
            {
                return WebMessageValidationResult.Invalid(WebMessageValidationError.UnknownType);
            }

            if (typeName == "interaction-state"
                && (!payload.TryGetProperty("active", out var active)
                    || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            {
                return WebMessageValidationResult.Invalid(WebMessageValidationError.InvalidPayload);
            }

            return WebMessageValidationResult.Valid(new WebMessageEnvelope(
                schemaVersion,
                typeName!,
                payload.Clone()));
        }
        catch (JsonException)
        {
            return WebMessageValidationResult.Invalid(WebMessageValidationError.MalformedJson);
        }
    }
}
