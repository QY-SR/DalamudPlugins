using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using QToolKit.Core;
using CombatRuntime = QToolKit.Modules.CombatModelBlocker.Runtime;
using CrescentRuntime = QToolKit.Modules.CrescentMarkers.Runtime;
using SlotLockRuntime = QToolKit.Modules.InventorySlotLock.Runtime;
using TranslateRuntime = QToolKit.Modules.QuickAutoTranslate.Runtime;
using SearchRuntime = QToolKit.Modules.InventorySearch.Runtime;

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
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private readonly Configuration configuration;
    private readonly ModuleContext context;
    private readonly ModuleHost moduleHost = new();
    private readonly LegacyMigrationService migrationService;
    private IReadOnlyList<string> migrationResults = Array.Empty<string>();
    private bool windowOpen;

    public Plugin()
    {
        this.configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.configuration.AttachSaveActions(PluginInterface);
        this.context = new ModuleContext(
            PluginInterface, CommandManager, Framework, Condition, ObjectTable, PartyList,
            NamePlateGui, ContextMenu, ClientState, GameInteropProvider, DataManager,
            TextureProvider, ChatGui, GameGui, SeStringEvaluator, PlayerState, Log);

        this.migrationService = new LegacyMigrationService(PluginInterface, Log);
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
            HelpMessage = "Open QToolKit.",
            ShowInHelp = true,
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
            "CombatModelBlocker", "Combat Model Blocker", "Hide non-party player models using the original /cmb module.",
            () => new CombatRuntime(this.context, this.configuration.CombatModelBlocker), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new RuntimeModule<CrescentRuntime>(
            "CrescentMarkers", "Crescent Markers", "Track Crescent Isle chests and carrots using /ocmark.",
            () => new CrescentRuntime(this.context, this.configuration.CrescentMarkers), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new Modules.WhiteMageCureRedirectModule(this.context));
        this.moduleHost.Register(new RuntimeModule<SlotLockRuntime>(
            "InventorySlotLock", "Inventory Slot Lock", "Protect inventory slots and local fake items using /isl.",
            () => new SlotLockRuntime(this.context, this.configuration.InventorySlotLock), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new RuntimeModule<TranslateRuntime>(
            "QuickAutoTranslate", "Quick Auto Translate", "Search auto-translate phrases and historical actions using /qat.",
            () => new TranslateRuntime(this.context, this.configuration.QuickAutoTranslate), runtime => runtime.OpenWindow()));
        this.moduleHost.Register(new RuntimeModule<SearchRuntime>(
            "InventorySearch", "Inventory Search", "Search and organize saved inventory snapshots using /ebsearch.",
            () => new SearchRuntime(this.context, this.configuration.InventorySearch), runtime => runtime.OpenWindow()));
    }

    private void OnCommand(string _, string arguments) => this.windowOpen = true;
    private void OpenWindow() => this.windowOpen = true;

    private void Draw()
    {
        if (!this.windowOpen)
            return;
        ImGui.SetNextWindowSize(new Vector2(720f, 590f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("QToolKit###QToolKit", ref this.windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("QToolKit");
        ImGui.TextDisabled("Standalone plugin data is imported without deleting the original files.");
        ImGui.TextWrapped("Disable each standalone plugin before enabling its QToolKit module to avoid command and hook conflicts.");
        ImGui.Separator();

        foreach (var module in this.moduleHost.Modules)
        {
            ImGui.PushID(module.Id);
            var enabled = module.IsRunning;
            if (ImGui.Checkbox(module.DisplayName, ref enabled))
            {
                try
                {
                    if (enabled)
                        module.Start();
                    else
                        module.Stop();
                    this.configuration.SetModuleEnabled(module.Id, enabled);
                    this.configuration.Save(PluginInterface);
                }
                catch (Exception exception)
                {
                    Log.Error(exception, $"Failed to change module state for {module.Id}.");
                    this.configuration.SetModuleEnabled(module.Id, module.IsRunning);
                    this.configuration.Save(PluginInterface);
                }
            }
            ImGui.SameLine();
            ImGui.TextDisabled(module.IsRunning ? "Running" : "Disabled");
            ImGui.TextWrapped(module.Description);
            module.DrawSettings();
            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.CollapsingHeader("Legacy Data Migration"))
        {
            foreach (var pair in this.configuration.Migrations)
                ImGui.BulletText($"{pair.Key}: {pair.Value.Summary}");
            foreach (var result in this.migrationResults)
                ImGui.TextDisabled(result);
            if (ImGui.Button("Import legacy data again"))
            {
                this.migrationResults = this.migrationService.ImportAvailable(this.configuration, true);
                this.configuration.AttachSaveActions(PluginInterface);
            }
        }
        ImGui.End();
    }
}
