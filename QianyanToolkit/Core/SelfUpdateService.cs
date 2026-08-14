using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace QToolKit.Core;

internal sealed class SelfUpdateService
{
    private const string InternalName = "QToolKit";
    private static readonly TimeSpan AutomaticUpdateDelay = TimeSpan.FromSeconds(4);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private volatile bool checking;
    private bool promptOpen;
    private volatile bool applying;
    private DateTime automaticUpdateAt;
    private string availableVersion = string.Empty;
    private string changelog = string.Empty;
    private string status = string.Empty;

    public SelfUpdateService(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
    }

    public void CheckWhenOpened()
    {
        if (this.checking || this.applying || this.promptOpen)
            return;

        _ = this.CheckAsync();
    }

    public void Draw()
    {
        if (!this.promptOpen)
            return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(520f, 0f), ImGuiCond.Appearing);
        var visible = ImGui.Begin(
            "QToolKit 更新提示###QToolKitSelfUpdate",
            ref this.promptOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings);

        if (visible)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.30f, 0.82f, 0.48f, 1f), "检测到 QToolKit 更新");
            ImGui.TextWrapped($"可用版本：{this.availableVersion}");
            if (!string.IsNullOrWhiteSpace(this.changelog))
            {
                ImGui.Spacing();
                ImGui.TextDisabled("更新说明");
                ImGui.TextWrapped(this.changelog);
            }

            ImGui.Spacing();
            if (this.applying)
            {
                ImGui.TextWrapped(string.IsNullOrWhiteSpace(this.status) ? "正在自动更新……" : this.status);
            }
            else
            {
                var remaining = Math.Max(0, (int)Math.Ceiling((this.automaticUpdateAt - DateTime.UtcNow).TotalSeconds));
                ImGui.TextWrapped($"将在 {remaining} 秒后自动更新。更新时 QToolKit 会短暂重载。");
                ImGui.Spacing();
                if (ImGui.Button("立即更新", new System.Numerics.Vector2(180f, 0f)))
                    this.StartAutomaticUpdate();
                ImGui.SameLine();
                if (ImGui.Button("本次暂不更新", new System.Numerics.Vector2(180f, 0f)))
                    this.promptOpen = false;

                if (DateTime.UtcNow >= this.automaticUpdateAt)
                    this.StartAutomaticUpdate();
            }
        }

        ImGui.End();
    }

    private async Task CheckAsync()
    {
        this.checking = true;
        try
        {
            var update = await this.pluginInterface.CheckForUpdateAsync().ConfigureAwait(false);
            if (update == null)
                return;

            this.availableVersion = update.Version.ToString();
            this.changelog = update.Changelog ?? string.Empty;
            this.status = string.Empty;
            this.automaticUpdateAt = DateTime.UtcNow + AutomaticUpdateDelay;
            this.promptOpen = true;
        }
        catch (Exception exception)
        {
            this.log.Warning(exception, "QToolKit update check failed.");
        }
        finally
        {
            this.checking = false;
        }
    }

    private void StartAutomaticUpdate()
    {
        if (this.applying)
            return;

        this.applying = true;
        this.status = "正在下载并应用更新……";
        _ = this.ApplyUpdateAsync();
    }

    private async Task ApplyUpdateAsync()
    {
        try
        {
            var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
            var managerType = dalamudAssembly.GetType("Dalamud.Plugin.Internal.PluginManager")
                              ?? throw new MissingMemberException("PluginManager type is unavailable.");
            var getService = this.pluginInterface.GetType().GetMethod(
                                 "GetService",
                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                 null,
                                 [typeof(Type)],
                                 null)
                             ?? throw new MissingMethodException("DalamudPluginInterface.GetService(Type) is unavailable.");
            var manager = getService.Invoke(this.pluginInterface, [managerType])
                          ?? throw new InvalidOperationException("PluginManager service is unavailable.");
            var updatableProperty = managerType.GetProperty(
                                        "UpdatablePlugins",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                    ?? throw new MissingMemberException("PluginManager.UpdatablePlugins is unavailable.");
            var updates = updatableProperty.GetValue(manager) as IEnumerable
                          ?? throw new InvalidOperationException("Updatable plugin list is unavailable.");
            var metadata = updates.Cast<object>().FirstOrDefault(IsQToolKitUpdate)
                           ?? throw new InvalidOperationException("QToolKit update metadata is not ready.");
            var updateMethod = managerType.GetMethod(
                                   "UpdateSinglePluginAsync",
                                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException("PluginManager.UpdateSinglePluginAsync is unavailable.");
            var result = updateMethod.Invoke(manager, [metadata, true, false]);
            if (result is Task task)
                await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this.log.Error(exception, "QToolKit automatic update failed; opening the plugin installer instead.");
            this.status = "自动更新未能完成，已为你打开插件安装器的更新页面。";
            this.applying = false;
            this.promptOpen = true;
            await this.framework.RunOnFrameworkThread(() =>
                this.pluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.UpdateablePlugins, InternalName));
        }
    }

    private static bool IsQToolKitUpdate(object update)
    {
        var installedPlugin = update.GetType().GetProperty(
            "InstalledPlugin",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(update);
        var internalName = installedPlugin?.GetType().GetProperty(
            "InternalName",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(installedPlugin) as string;
        return string.Equals(internalName, InternalName, StringComparison.Ordinal);
    }
}
