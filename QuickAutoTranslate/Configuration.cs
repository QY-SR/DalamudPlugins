using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace QuickAutoTranslate;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<uint> FavoriteRows { get; set; } = [];

    public List<uint> RecentRows { get; set; } = [];

    public int MaximumResults { get; set; } = 100;

    public bool CloseAfterInsert { get; set; } = true;

    public void Save(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);
}
