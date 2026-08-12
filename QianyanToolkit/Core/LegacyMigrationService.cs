using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace QToolKit.Core;

internal sealed class LegacyMigrationService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    public LegacyMigrationService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public IReadOnlyList<string> ImportAvailable(Configuration target, bool force)
    {
        var results = new List<string>();
        this.Import<Modules.CombatModelBlocker.Configuration>("CombatModelBlocker", target, force, value => target.CombatModelBlocker = value, results);
        this.Import<Modules.CrescentMarkers.Configuration>("CrescentMarkers", target, force, value => target.CrescentMarkers = value, results);
        this.Import<Modules.InventorySlotLock.Configuration>("InventorySlotLock", target, force, value => target.InventorySlotLock = value, results);
        this.Import<Modules.QuickAutoTranslate.Configuration>("QuickAutoTranslate", target, force, value => target.QuickAutoTranslate = value, results);
        this.Import<Modules.InventorySearch.Configuration>("InventorySearch", target, force, value => target.InventorySearch = value, results);

        if (!target.Migrations.ContainsKey("WhiteMageCureRedirect"))
        {
            target.Migrations["WhiteMageCureRedirect"] = new MigrationRecord(
                "No configuration file", DateTime.UtcNow, "No persistent data to import");
            results.Add("WhiteMageCureRedirect: no persistent data");
        }

        target.AttachSaveActions(this.pluginInterface);
        target.Save(this.pluginInterface);
        return results;
    }

    private void Import<T>(string id, Configuration target, bool force, Action<T> assign, List<string> results)
        where T : class
    {
        if (!force && target.Migrations.ContainsKey(id))
            return;

        var root = this.pluginInterface.ConfigFile.DirectoryName;
        if (string.IsNullOrWhiteSpace(root))
            return;
        var source = Path.Combine(root, id + ".json");
        if (!File.Exists(source))
        {
            results.Add($"{id}: legacy configuration not found");
            return;
        }

        try
        {
            var imported = JsonConvert.DeserializeObject<T>(File.ReadAllText(source));
            if (imported == null)
                throw new InvalidDataException("Configuration deserialized to null.");
            assign(imported);
            target.Migrations[id] = new MigrationRecord(source, DateTime.UtcNow, "Imported successfully; source preserved");
            results.Add($"{id}: imported");
        }
        catch (Exception exception)
        {
            this.log.Error(exception, $"Failed to import legacy configuration for {id}.");
            results.Add($"{id}: import failed");
        }
    }
}
