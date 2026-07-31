using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CombatModelBlocker;

public enum BlockingMode
{
    CombatOnly,
    Always,
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public BlockingMode Mode { get; set; } = BlockingMode.CombatOnly;

    public bool KeepDeadPlayersVisible { get; set; } = true;

    public bool HideNameplates { get; set; }

    public bool KeepFriendsVisible { get; set; } = true;

    public bool KeepPlayersTargetingMeVisible { get; set; } = true;

    public void Save(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);
}
