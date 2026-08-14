using Dalamud.Configuration;

namespace QToolKit.Modules.JumpAssist;

public sealed class Configuration : IPluginConfiguration
{
    internal System.Action SaveAction { get; set; } = static () => { };
    public int Version { get; set; } = 1;
    public bool ShowCursorMeasurement { get; set; } = true;
    public bool DebugMode { get; set; }
}
