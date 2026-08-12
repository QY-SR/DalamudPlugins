using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace QToolKit.Modules.QuickAutoTranslate;

public sealed class Configuration : IPluginConfiguration
{
    internal System.Action SaveAction { get; set; } = static () => { };
    public int Version { get; set; } = 1;

    public List<uint> FavoriteRows { get; set; } = [];

    public List<uint> RecentRows { get; set; } = [];

    public int MaximumResults { get; set; } = 100;

    public bool CloseAfterInsert { get; set; } = true;

    public void Save(IDalamudPluginInterface pluginInterface)
        => this.SaveAction();
}
