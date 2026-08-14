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
        this.Import<Modules.JumpAssist.Configuration>("JumpAssist", target, force, value => target.JumpAssist = value, results);

        if (!target.Migrations.ContainsKey("WhiteMageCureRedirect"))
        {
            target.Migrations["WhiteMageCureRedirect"] = new MigrationRecord(
                "无配置文件", DateTime.UtcNow, "没有需要迁移的持久数据");
            results.Add("WhiteMageCureRedirect：无需迁移持久数据");
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
            results.Add($"{id}：未找到旧版配置");
            return;
        }

        try
        {
            var imported = JsonConvert.DeserializeObject<T>(File.ReadAllText(source));
            if (imported == null)
                throw new InvalidDataException("Configuration deserialized to null.");
            assign(imported);
            target.Migrations[id] = new MigrationRecord(source, DateTime.UtcNow, "导入成功，原文件已保留");
            results.Add($"{id}：已导入");
        }
        catch (Exception exception)
        {
            this.log.Error(exception, $"Failed to import legacy configuration for {id}.");
            results.Add($"{id}：导入失败");
        }
    }
}
