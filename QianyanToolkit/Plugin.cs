using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Config;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using QToolKit.Core;
using CombatRuntime = QToolKit.Modules.CombatModelBlocker.Runtime;
using CrescentRuntime = QToolKit.Modules.CrescentMarkers.Runtime;
using SlotLockRuntime = QToolKit.Modules.InventorySlotLock.Runtime;
using TranslateRuntime = QToolKit.Modules.QuickAutoTranslate.Runtime;
using SearchRuntime = QToolKit.Modules.InventorySearch.Runtime;
using JumpRuntime = QToolKit.Modules.JumpAssist.Runtime;

namespace QToolKit;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/qtk";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IPartyList PartyList { get; set; } = null!;
    [PluginService] private static INamePlateGui NamePlateGui { get; set; } = null!;
    [PluginService] private static IContextMenu ContextMenu { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IGameInteropProvider GameInteropProvider { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static ISeStringEvaluator SeStringEvaluator { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] private static IGameConfig GameConfig { get; set; } = null!;
    [PluginService] private static IKeyState KeyState { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private readonly Configuration configuration;
    private readonly ModuleContext context;
    private readonly ModuleHost moduleHost = new();
    private readonly LegacyMigrationService migrationService;
    private readonly SelfUpdateService selfUpdateService;
    private IReadOnlyList<string> migrationResults = Array.Empty<string>();
    private bool windowOpen;
    private int selectedPage;

    public Plugin()
    {
        this.configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.configuration.AttachSaveActions(PluginInterface);
        this.context = new ModuleContext(
            PluginInterface, CommandManager, Framework, Condition, ObjectTable, PartyList,
            NamePlateGui, ContextMenu, ClientState, GameInteropProvider, DataManager,
            TextureProvider, ChatGui, GameGui, SeStringEvaluator, PlayerState, GameConfig, KeyState, Log);

        this.migrationService = new LegacyMigrationService(PluginInterface, Log);
        this.selfUpdateService = new SelfUpdateService(PluginInterface, Framework, Log);
        this.migrationResults = this.migrationService.ImportAvailable(this.configuration, false);
        this.RegisterModules();

        foreach (var module in this.moduleHost.Modules)
        {
            if (!this.configuration.IsModuleEnabled(module.Id))
                continue;
            try
            {
                module.Start();
            }
            catch (Exception exception)
            {
                Log.Error(exception, $"Failed to start module {module.Id}.");
                this.configuration.SetModuleEnabled(module.Id, false);
            }
        }
        this.configuration.Save(PluginInterface);

        CommandManager.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开 QToolKit。",
            ShowInHelp = false,
        });
        PluginInterface.UiBuilder.Draw += this.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenWindow;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= this.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenWindow;
        CommandManager.RemoveHandler(Command);
        this.moduleHost.Dispose();
    }

    private void RegisterModules()
    {
        this.moduleHost.Register(new RuntimeModule<CombatRuntime>(
            "CombatModelBlocker", "战斗模型屏蔽", "1.0.5.0", "按规则隐藏非小队玩家模型。", "/cmb：打开战斗模型屏蔽设置。",
            () => new CombatRuntime(this.context, this.configuration.CombatModelBlocker), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new RuntimeModule<CrescentRuntime>(
            "CrescentMarkers", "新月岛宝藏标记", "1.0.14.0", "记录并标记新月岛宝箱与胡萝卜。", "/ocmark：打开新月岛宝藏标记窗口。",
            () => new CrescentRuntime(this.context, this.configuration.CrescentMarkers), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new Modules.WhiteMageCureRedirectModule(this.context));
        this.moduleHost.Register(new RuntimeModule<SlotLockRuntime>(
            "InventorySlotLock", "背包格子锁", "1.3.5.0", "保护指定背包格子并管理本地幽灵物品。", "/isl：打开设置。\n/isl create <物品ID> <数量>：生成纯本地幽灵物品。\n/isl clearfake：清除全部本地幽灵物品。",
            () => new SlotLockRuntime(this.context, this.configuration.InventorySlotLock), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new RuntimeModule<TranslateRuntime>(
            "QuickAutoTranslate", "定型文快速筛选", "1.5.1.0", "通过中文或拼音检索定型文与历史技能。", "/qat：打开定型文快速筛选。\n/qat hotbar：打开历史技能热键栏。",
            () => new TranslateRuntime(this.context, this.configuration.QuickAutoTranslate), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new RuntimeModule<SearchRuntime>(
            "InventorySearch", "增强背包搜索", "1.5.3.0", "检索并整理保存的库存快照。", "/ebsearch：打开跨角色背包与收藏搜索窗口。\n/ebsearch refresh：立即刷新库存快照。",
            () => new SearchRuntime(this.context, this.configuration.InventorySearch), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new RuntimeModule<JumpRuntime>(
            "JumpAssist", "跳跳乐助手", "3.6.1.0", "观察落点、预览轨迹并辅助执行跳跃。", "/jumpassist：打开设置。\n/jumpassist observe：进入观察选点。\n/jumpassist go：执行跳跃。\n/jumpassist clear：清除目标。",
            () => new JumpRuntime(this.context, this.configuration.JumpAssist), runtime => runtime.OpenWindow()));
    }

    private void OnCommand(string _, string arguments) => this.OpenWindow();

    private void OpenWindow()
    {
        this.windowOpen = true;
        this.selfUpdateService.CheckWhenOpened();
    }

    private void Draw()
    {
        this.selfUpdateService.Draw();
        if (!this.windowOpen)
            return;
        ImGui.SetNextWindowSize(new Vector2(820f, 560f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("QToolKit###QToolKit", ref this.windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(new Vector4(0.96f, 0.22f, 0.26f, 1f), "QT");
        ImGui.SameLine();
        ImGui.TextUnformatted("QToolKit");
        ImGui.SameLine();
        ImGui.TextDisabled("模块化插件合集");
        ImGui.Separator();

        var availableHeight = ImGui.GetContentRegionAvail().Y;
        if (ImGui.BeginChild("qtk-navigation", new Vector2(220f, availableHeight), true))
        {
            ImGui.TextDisabled("功能模块");
            ImGui.Spacing();
            for (var index = 0; index < this.moduleHost.Modules.Count; index++)
            {
                var module = this.moduleHost.Modules[index];
                ImGui.TextColored(module.IsRunning
                    ? new Vector4(0.30f, 0.82f, 0.48f, 1f)
                    : new Vector4(0.48f, 0.50f, 0.54f, 1f), "●");
                ImGui.SameLine();
                if (ImGui.Selectable(
                        $"{module.DisplayName}\nv{module.Version}##module-{module.Id}",
                        this.selectedPage == index,
                        ImGuiSelectableFlags.None,
                        new Vector2(0f, 42f)))
                    this.selectedPage = index;
            }
            ImGui.Spacing();
            ImGui.Separator();
            if (ImGui.Selectable("数据迁移", this.selectedPage == this.moduleHost.Modules.Count, ImGuiSelectableFlags.None, new Vector2(0f, 30f)))
                this.selectedPage = this.moduleHost.Modules.Count;
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("qtk-content", Vector2.Zero, true))
        {
            if (this.selectedPage >= 0 && this.selectedPage < this.moduleHost.Modules.Count)
                this.DrawModulePage(this.moduleHost.Modules[this.selectedPage]);
            else
                this.DrawMigrationPage();
        }
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawModulePage(IToolkitModule module)
    {
        ImGui.TextUnformatted(module.DisplayName);
        ImGui.TextDisabled($"{module.Id} · v{module.Version}");
        ImGui.Spacing();
        ImGui.TextWrapped(module.Description);
        ImGui.Spacing();
        ImGui.TextDisabled("可用命令");
        ImGui.TextWrapped(module.CommandHelp);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("模块状态");
        ImGui.SameLine();
        ImGui.TextColored(module.IsRunning
            ? new Vector4(0.30f, 0.82f, 0.48f, 1f)
            : new Vector4(0.62f, 0.64f, 0.68f, 1f), module.IsRunning ? "运行中" : "已停用");

        var enabled = module.IsRunning;
        if (ImGui.Checkbox("启用此模块", ref enabled))
            this.SetModuleState(module, enabled);

        if (module.IsRunning)
        {
            ImGui.Spacing();
            module.DrawSettings();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped("启用合集模块前，请先在卫月插件安装器中停用对应的独立插件，避免命令或 Hook 冲突。");
    }

    private void SetModuleState(IToolkitModule module, bool enabled)
    {
        try
        {
            if (enabled)
                module.Start();
            else
                module.Stop();
            this.configuration.SetModuleEnabled(module.Id, enabled);
        }
        catch (Exception exception)
        {
            Log.Error(exception, $"切换模块 {module.Id} 状态失败。");
            this.configuration.SetModuleEnabled(module.Id, module.IsRunning);
        }
        this.configuration.Save(PluginInterface);
    }

    private void DrawMigrationPage()
    {
        ImGui.TextUnformatted("旧版数据迁移");
        ImGui.TextWrapped("QToolKit 会读取独立插件的旧配置并保存到合集配置中，原文件始终保留，不会自动删除。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        foreach (var pair in this.configuration.Migrations)
        {
            ImGui.TextColored(new Vector4(0.30f, 0.82f, 0.48f, 1f), "✓");
            ImGui.SameLine();
            ImGui.TextUnformatted(pair.Key);
            ImGui.TextDisabled(pair.Value.Summary);
        }
        foreach (var result in this.migrationResults)
            ImGui.TextDisabled(result);
        ImGui.Spacing();
        if (ImGui.Button("重新导入旧版数据"))
        {
            this.migrationResults = this.migrationService.ImportAvailable(this.configuration, true);
            this.configuration.AttachSaveActions(PluginInterface);
        }
    }
}
