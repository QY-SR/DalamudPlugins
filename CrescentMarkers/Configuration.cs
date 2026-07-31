using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CrescentMarkers;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public bool ShowChests { get; set; } = true;

    public bool ShowCarrots { get; set; } = true;

    public bool ShowDistance { get; set; } = true;

    public bool EchoNewDetections { get; set; } = true;

    public float MaxDistance { get; set; } = 200f;

    public bool ShowScanner { get; set; }

    public List<uint> ChestBaseIds { get; set; } = [];

    public List<uint> CarrotBaseIds { get; set; } = [];

    public List<TrackedChestRecord> TrackedChests { get; set; } = [];

    public void Save(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);
}
