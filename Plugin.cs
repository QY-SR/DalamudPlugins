using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatModelBlocker;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IPartyList PartyList { get; set; } = null!;
    [PluginService] private static INamePlateGui NamePlateGui { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private const string Command = "/cmb";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly INamePlateGui namePlateGui;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, HiddenObject> hiddenObjects = new();

    private Configuration configuration;
    private bool configWindowOpen;
    // E0000000 是客户端预览/无网络实体对象使用的无效 EntityId。
    // 只有真实地图玩家会进入模型屏蔽流程，铭牌和肖像预览对象会被排除。
    private const uint InvalidEntityId = 0xE0000000;
    private const VisibilityFlags HiddenFlags = VisibilityFlags.Model;

    public Plugin()
    {
        this.pluginInterface = PluginInterface;
        this.commandManager = CommandManager;
        this.framework = Framework;
        this.condition = Condition;
        this.objectTable = ObjectTable;
        this.partyList = PartyList;
        this.namePlateGui = NamePlateGui;
        this.log = Log;

        this.configuration =
            this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        this.commandManager.AddHandler(
            Command,
            new CommandInfo(this.OnCommand)
            {
                HelpMessage = "打开“战斗模型屏蔽”设置。",
                ShowInHelp = true,
            });

        this.framework.Update += this.OnFrameworkUpdate;
        this.namePlateGui.OnDataUpdate += this.OnNamePlateDataUpdate;
        this.pluginInterface.UiBuilder.Draw += this.DrawConfigWindow;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigWindow;
        this.pluginInterface.UiBuilder.OpenMainUi += this.OpenConfigWindow;
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.namePlateGui.OnDataUpdate -= this.OnNamePlateDataUpdate;
        this.pluginInterface.UiBuilder.Draw -= this.DrawConfigWindow;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigWindow;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenConfigWindow;
        this.commandManager.RemoveHandler(Command);
        this.RestoreAll();
        this.namePlateGui.RequestRedraw();
    }

    private void OnCommand(string command, string arguments)
        => this.configWindowOpen = true;

    private void OpenConfigWindow()
        => this.configWindowOpen = true;

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            var shouldBlock = this.configuration.Enabled
                              && (this.configuration.Mode == BlockingMode.Always
                                  || this.condition[ConditionFlag.InCombat]);

            if (!shouldBlock)
            {
                this.RestoreAll();
                return;
            }

            foreach (var gameObject in this.objectTable)
            {
                if (gameObject is not IPlayerCharacter player)
                    continue;

                if (this.ShouldHide(player))
                    this.Hide(player);
                else
                    this.Restore(player.EntityId, player.Address);
            }

            this.RemoveStaleRecords();
        }
        catch (Exception exception)
        {
            this.log.Error(exception, "更新玩家模型可见性时发生错误。");
        }
    }

    private void OnNamePlateDataUpdate(
        INamePlateUpdateContext _,
        IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!this.configuration.HideNameplates || !this.ShouldBlock())
            return;

        foreach (var handler in handlers)
        {
            var player = handler.PlayerCharacter;
            if (player is not null && this.ShouldHide(player))
                handler.VisibilityFlags = 0;
        }
    }

    private bool ShouldBlock()
        => this.configuration.Enabled
           && (this.configuration.Mode == BlockingMode.Always
               || this.condition[ConditionFlag.InCombat]);

    private bool ShouldHide(IPlayerCharacter player)
    {
        if (player.EntityId == InvalidEntityId)
            return false;

        var localPlayer = this.objectTable.LocalPlayer;
        if (player.EntityId == localPlayer?.EntityId)
            return false;

        if (this.partyList.Any(member => member.EntityId == player.EntityId))
            return false;

        if (this.configuration.KeepDeadPlayersVisible && player.IsDead)
            return false;

        if (this.configuration.KeepFriendsVisible
            && player.StatusFlags.HasFlag(StatusFlags.Friend))
            return false;

        return !this.configuration.KeepPlayersTargetingMeVisible
               || localPlayer is null
               || player.TargetObjectId != localPlayer.GameObjectId;
    }

    private unsafe void Hide(IPlayerCharacter player)
    {
        var nativeObject = (GameObject*)player.Address;
        if (nativeObject == null)
            return;

        if (this.hiddenObjects.TryGetValue(player.EntityId, out var existing))
        {
            if (existing.Address == player.Address)
            {
                nativeObject->RenderFlags |= existing.ManagedFlags;
                return;
            }

            this.hiddenObjects.Remove(player.EntityId);
        }

        var originalFlags = nativeObject->RenderFlags & HiddenFlags;
        nativeObject->RenderFlags |= HiddenFlags;
        this.hiddenObjects[player.EntityId] =
            new HiddenObject(player.Address, HiddenFlags, originalFlags);
    }

    private unsafe void Restore(uint entityId, nint address)
    {
        if (!this.hiddenObjects.Remove(entityId, out var hidden) || hidden.Address != address)
            return;

        var nativeObject = (GameObject*)address;
        if (nativeObject == null)
            return;

        nativeObject->RenderFlags =
            (nativeObject->RenderFlags & ~hidden.ManagedFlags) | hidden.OriginalFlags;
    }
    private void RestoreAll()
    {
        if (this.hiddenObjects.Count == 0)
            return;

        foreach (var player in this.objectTable.OfType<IPlayerCharacter>())
            this.Restore(player.EntityId, player.Address);

        this.hiddenObjects.Clear();
    }

    private void RemoveStaleRecords()
    {
        if (this.hiddenObjects.Count == 0)
            return;

        var visibleEntityIds = this.objectTable
            .OfType<IPlayerCharacter>()
            .Select(player => player.EntityId)
            .ToHashSet();

        foreach (var entityId in this.hiddenObjects.Keys
                     .Where(entityId => !visibleEntityIds.Contains(entityId))
                     .ToArray())
        {
            this.hiddenObjects.Remove(entityId);
        }
    }

    private void DrawConfigWindow()
    {
        if (!this.configWindowOpen)
            return;

        ImGui.SetNextWindowSizeConstraints(new(420, 0), new(800, 800));
        if (!ImGui.Begin("战斗模型屏蔽###CombatModelBlockerConfig", ref this.configWindowOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = this.configuration.Enabled;
        if (ImGui.Checkbox("启用插件", ref enabled))
        {
            this.configuration.Enabled = enabled;
            this.SaveConfiguration();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("屏蔽模式");

        if (ImGui.RadioButton(
                "仅在战斗中屏蔽",
                this.configuration.Mode == BlockingMode.CombatOnly))
        {
            this.configuration.Mode = BlockingMode.CombatOnly;
            this.SaveConfiguration();
        }

        if (ImGui.RadioButton(
                "常驻屏蔽",
                this.configuration.Mode == BlockingMode.Always))
        {
            this.configuration.Mode = BlockingMode.Always;
            this.SaveConfiguration();
        }

        var keepDeadVisible = this.configuration.KeepDeadPlayersVisible;
        if (ImGui.Checkbox("不屏蔽已经死亡的玩家", ref keepDeadVisible))
        {
            this.configuration.KeepDeadPlayersVisible = keepDeadVisible;
            this.SaveConfiguration();
        }

        var keepFriendsVisible = this.configuration.KeepFriendsVisible;
        if (ImGui.Checkbox("不屏蔽好友", ref keepFriendsVisible))
        {
            this.configuration.KeepFriendsVisible = keepFriendsVisible;
            this.SaveConfiguration();
        }

        var keepTargetingMeVisible = this.configuration.KeepPlayersTargetingMeVisible;
        if (ImGui.Checkbox("不屏蔽目标为我的玩家", ref keepTargetingMeVisible))
        {
            this.configuration.KeepPlayersTargetingMeVisible = keepTargetingMeVisible;
            this.SaveConfiguration();
        }

        var hideNameplates = this.configuration.HideNameplates;
        if (ImGui.Checkbox("同时隐藏姓名板", ref hideNameplates))
        {
            this.configuration.HideNameplates = hideNameplates;
            this.SaveConfiguration();
        }

        ImGui.Spacing();
        ImGui.TextWrapped("仅影响非小队成员的玩家模型；自己和小队成员始终显示。");
        ImGui.TextWrapped("命令：/cmb");
        ImGui.End();
    }

    private void SaveConfiguration()
    {
        this.RestoreAll();
        this.namePlateGui.RequestRedraw();
        this.configuration.Save(this.pluginInterface);
    }

    private readonly record struct HiddenObject(
        nint Address,
        VisibilityFlags ManagedFlags,
        VisibilityFlags OriginalFlags);
}
