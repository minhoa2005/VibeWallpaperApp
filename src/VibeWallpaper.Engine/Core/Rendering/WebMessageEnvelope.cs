using System.Text.Json;

namespace VibeWallpaper.Engine.Core.Rendering;

public sealed record WebMessageEnvelope(
    int SchemaVersion,
    string Type,
    JsonElement Payload);
