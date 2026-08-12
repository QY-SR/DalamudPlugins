using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace QToolKit;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public Dictionary<string, bool> EnabledModules { get; set; } = new();
    public Dictionary<string, MigrationRecord> Migrations { get; set; } = new();

    public Modules.CombatModelBlocker.Configuration CombatModelBlocker { get; set; } = new();
    public Modules.CrescentMarkers.Configuration CrescentMarkers { get; set; } = new();
    public Modules.InventorySlotLock.Configuration InventorySlotLock { get; set; } = new();
    public Modules.QuickAutoTranslate.Configuration QuickAutoTranslate { get; set; } = new();
    public Modules.InventorySearch.Configuration InventorySearch { get; set; } = new();

    public bool IsModuleEnabled(string moduleId)
        => this.EnabledModules.TryGetValue(moduleId, out var enabled) && enabled;

    public void SetModuleEnabled(string moduleId, bool enabled)
        => this.EnabledModules[moduleId] = enabled;

    public void AttachSaveActions(IDalamudPluginInterface pluginInterface)
    {
        void Save() => pluginInterface.SavePluginConfig(this);
        this.CombatModelBlocker.SaveAction = Save;
        this.CrescentMarkers.SaveAction = Save;
        this.InventorySlotLock.SaveAction = Save;
        this.QuickAutoTranslate.SaveAction = Save;
        this.InventorySearch.SaveAction = Save;
    }

    public void Save(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);
}

public sealed record MigrationRecord(string SourceFile, DateTime ImportedUtc, string Summary);
