using System.Text.Json.Nodes;

namespace VibeWallpaper.Engine.Persistence.Migrations;

public sealed class StateV0ToV1Migration : IPersistenceMigration
{
    public int FromVersion => 0;
    public int ToVersion => 1;

    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var migrated = (JsonObject)document.DeepClone();
        if (!migrated.ContainsKey("library")) migrated["library"] = new JsonArray();
        if (!migrated.ContainsKey("assignments")) migrated["assignments"] = new JsonArray();
        if (!migrated.ContainsKey("groups")) migrated["groups"] = new JsonArray();
        if (!migrated.ContainsKey("audioOwner")) migrated["audioOwner"] = null;
        migrated["schemaVersion"] = 1;
        return migrated;
    }
}
