using System;
using Dalamud.Bindings.ImGui;

namespace QToolKit.Core;

internal sealed class RuntimeModule<TRuntime> : IToolkitModule
    where TRuntime : class, IDisposable
{
    private readonly Func<TRuntime> factory;
    private readonly Action<TRuntime> openSettings;
    private TRuntime? runtime;

    public RuntimeModule(string id, string displayName, string description, Func<TRuntime> factory, Action<TRuntime> openSettings)
    {
        this.Id = id;
        this.DisplayName = displayName;
        this.Description = description;
        this.factory = factory;
        this.openSettings = openSettings;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public bool IsRunning => this.runtime != null;

    public void Start() => this.runtime ??= this.factory();

    public void Stop()
    {
        this.runtime?.Dispose();
        this.runtime = null;
    }

    public void DrawSettings()
    {
        if (this.runtime != null && ImGui.SmallButton($"Open##{this.Id}"))
            this.openSettings(this.runtime);
    }

    public void Dispose() => this.Stop();
}
