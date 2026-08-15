using System.Text.Json.Nodes;

namespace VibeWallpaper.Engine.Persistence;

public interface IPersistenceMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    JsonObject Migrate(JsonObject document);
}
