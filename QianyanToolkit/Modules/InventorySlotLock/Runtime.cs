using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Inventory;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace QToolKit.Modules.InventorySlotLock;

using QToolKit.Core;

internal sealed class Runtime : IDisposable
{
    private const string ChatTag = "QT";
    private const ushort ChatTagColor = 17;
    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IContextMenu ContextMenu { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IGameInteropProvider InteropProvider { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;

    private const string Command = "/isl";
    private const int SlotsPerPage = 35;
    private const int Columns = 5;

    private static readonly InventoryType[] PlayerInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private readonly Hook<MoveItemSlotDelegate> moveItemHook;
    private readonly Hook<DiscardItemDelegate> discardItemHook;
    private readonly Dictionary<nint, Hook<InventoryContextCallbackDelegate>> inventoryContextHooks = new();
    private Configuration configuration;
    private readonly Dictionary<int, PhysicalSlot> protectedEntries = new();
    private readonly Dictionary<(uint IconId, bool IsHq), ISharedImmediateTexture> iconCache = new();
    private bool windowOpen;
    private bool trackingInitialized;
    private string itemBrowserSearch = string.Empty;
    private int fakeItemQuantity = 1;
    private int locatedInventoryGridCount;
    private int drawnInventoryMarkerCount;
    private int matchedLockedSlotCount;
    private int missingSlotNodeCount;
    private int invalidSlotBoundsCount;
    private int occludedMarkerCount;
    private int draggingFakeDisplayIndex = -1;
    private bool fakeDropHandledThisFrame;
    private bool fakeTooltipHoveredThisFrame;
    private int shownFakeTooltipDisplayIndex = -1;
    private ushort shownFakeTooltipParentId;
    private int nativeFakeTargetCount;
    private int nativeFakeLoadedCount;
    private int nativeFakeVisibleCount;

    public unsafe Runtime(ModuleContext context, Configuration configuration)
    {
        PluginInterface = context.PluginInterface;
        CommandManager = context.CommandManager;
        ContextMenu = context.ContextMenu;
        Framework = context.Framework;
        ClientState = context.ClientState;
        InteropProvider = context.GameInteropProvider;
        Log = context.Log;
        DataManager = context.DataManager;
        TextureProvider = context.TextureProvider;
        ChatGui = context.ChatGui;
        GameGui = context.GameGui;
        this.configuration = configuration;
        this.configuration.FakeItems ??= [];

        this.moveItemHook = InteropProvider.HookFromAddress<MoveItemSlotDelegate>(
            (nint)InventoryManager.MemberFunctionPointers.MoveItemSlot,
            this.MoveItemSlotDetour);
        this.discardItemHook = InteropProvider.HookFromAddress<DiscardItemDelegate>(
            (nint)InventoryManager.MemberFunctionPointers.DiscardItem,
            this.DiscardItemDetour);
        this.moveItemHook.Enable();
        this.discardItemHook.Enable();

        CommandManager.AddHandler(Command, new Dalamud.Game.Command.CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开设置；/isl create <物品ID> <数量> 生成纯本地整蛊物品。",
            ShowInHelp = true,
        });

        ContextMenu.OnMenuOpened += this.OnMenuOpened;
        Framework.Update += this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += this.DrawWindow;


    }

    public unsafe void Dispose()
    {
        this.HideFakeTooltip();
        foreach (var fakeItem in this.configuration.FakeItems)
            this.ClearNativeFakeVisual(fakeItem.DisplayIndex);
        PluginInterface.UiBuilder.Draw -= this.DrawWindow;


        Framework.Update -= this.OnFrameworkUpdate;
        ContextMenu.OnMenuOpened -= this.OnMenuOpened;
        CommandManager.RemoveHandler(Command);

        this.ClearTracking();
        foreach (var hook in this.inventoryContextHooks.Values)
            hook.Dispose();
        this.inventoryContextHooks.Clear();
        this.discardItemHook.Dispose();
        this.moveItemHook.Dispose();
    }

    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && parts[0].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length != 3
                || !uint.TryParse(parts[1], out var itemId)
                || !uint.TryParse(parts[2], out var quantity)
                || quantity == 0)
            {
                ChatGui.PrintError("用法：/isl create <物品ID> <数量>", ChatTag, ChatTagColor);
                return;
            }

            this.CreateFakeItem(itemId, quantity);
            return;
        }

        if (parts.Length == 1 && parts[0].Equals("clearfake", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var fakeItem in this.configuration.FakeItems)
                this.ClearNativeFakeVisual(fakeItem.DisplayIndex);
            this.configuration.FakeItems.Clear();
            this.configuration.Save(PluginInterface);
            ChatGui.Print("已清除全部本地整蛊物品。", ChatTag, ChatTagColor);
            return;
        }

        this.windowOpen = true;
    }

    private void CreateFakeItem(uint itemId, uint quantity)
    {
        if (!ClientState.IsLoggedIn)
        {
            ChatGui.PrintError("请登录角色后再生成本地整蛊物品。", ChatTag, ChatTagColor);
            return;
        }

        var sheet = DataManager.GetExcelSheet<LuminaItem>();
        if (itemId == 0 || !sheet.TryGetRow(itemId, out var row))
        {
            ChatGui.PrintError($"找不到物品 ID {itemId}。", ChatTag, ChatTagColor);
            return;
        }

        var name = row.Name.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            ChatGui.PrintError($"找不到物品 ID {itemId}。", ChatTag, ChatTagColor);
            return;
        }

        for (var displayIndex = 0; displayIndex < PlayerInventories.Length * SlotsPerPage; displayIndex++)
        {
            if (this.configuration.FakeItems.Any(item => item.DisplayIndex == displayIndex)
                || !this.IsRealDisplaySlotEmpty(displayIndex))
                continue;

            this.configuration.FakeItems.Add(new FakeItem(displayIndex, itemId, quantity));
            this.configuration.Save(PluginInterface);
            return;
        }

        ChatGui.PrintError("没有可用于显示整蛊物品的空格。", ChatTag, ChatTagColor);
    }

    private void RemoveFakeItem(int displayIndex)
    {
        this.ClearNativeFakeVisual(displayIndex);
        if (this.configuration.FakeItems.RemoveAll(item => item.DisplayIndex == displayIndex) == 0)
            return;

        this.configuration.Save(PluginInterface);
    }

    private void MoveFakeItem(int sourceDisplayIndex, int targetDisplayIndex)
    {
        if (sourceDisplayIndex == targetDisplayIndex
            || !this.IsRealDisplaySlotEmpty(targetDisplayIndex))
            return;

        var sourceListIndex = this.configuration.FakeItems.FindIndex(item => item.DisplayIndex == sourceDisplayIndex);
        if (sourceListIndex < 0)
            return;

        var targetListIndex = this.configuration.FakeItems.FindIndex(item => item.DisplayIndex == targetDisplayIndex);
        var source = this.configuration.FakeItems[sourceListIndex];
        this.ClearNativeFakeVisual(sourceDisplayIndex);

        if (targetListIndex >= 0)
        {
            var target = this.configuration.FakeItems[targetListIndex];
            this.ClearNativeFakeVisual(targetDisplayIndex);
            this.configuration.FakeItems[sourceListIndex] = source with { DisplayIndex = targetDisplayIndex };
            this.configuration.FakeItems[targetListIndex] = target with { DisplayIndex = sourceDisplayIndex };
        }
        else
        {
            this.configuration.FakeItems[sourceListIndex] = source with { DisplayIndex = targetDisplayIndex };
        }

        this.configuration.Save(PluginInterface);
    }
    private bool TryGetFakeItem(int displayIndex, out FakeItem fakeItem)
    {
        foreach (var candidate in this.configuration.FakeItems)
        {
            if (candidate.DisplayIndex != displayIndex)
                continue;

            fakeItem = candidate;
            return true;
        }

        fakeItem = default;
        return false;
    }

    public void OpenWindow() => this.windowOpen = true;

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
        {
            this.trackingInitialized = false;
            this.protectedEntries.Clear();
            return;
        }

        if (!this.trackingInitialized)
            this.InitializeTracking();

        this.EnsureInventoryContextHook();
        this.EnforceLockedDisplaySlots();
        this.RefreshNativeFakeVisuals();
    }

    private unsafe void RefreshNativeFakeVisuals()
    {
        this.nativeFakeTargetCount = 0;
        this.nativeFakeLoadedCount = 0;
        this.nativeFakeVisibleCount = 0;
        if (this.configuration.FakeItems.Count == 0)
            return;

        var expandedGridCount = 0;
        for (var page = 0; page < PlayerInventories.Length; page++)
        {
            var grid = GameGui.GetAddonByName<AddonInventoryGrid>($"InventoryGrid{page}E");
            if (grid == null || !grid->IsVisible)
                continue;

            expandedGridCount++;
            this.RefreshNativeFakeGrid(grid, page);
        }

        if (expandedGridCount > 0)
            return;

        var large = GameGui.GetAddonByName<AddonInventoryLarge>("InventoryLarge");
        if (large != null && large->IsVisible)
        {
            var firstPage = Math.Clamp(large->TabIndex, 0, 1) * 2;
            for (var index = 0; index < 2; index++)
            {
                var grid = GameGui.GetAddonByName<AddonInventoryGrid>($"InventoryGrid{index}");
                if (grid != null && grid->IsVisible)
                    this.RefreshNativeFakeGrid(grid, firstPage + index);
            }

            return;
        }

        var normalGrid = GameGui.GetAddonByName<AddonInventoryGrid>("InventoryGrid");
        if (normalGrid != null && normalGrid->IsVisible)
        {
            var page = (int)normalGrid->Param;
            if (page >= 0 && page < PlayerInventories.Length)
                this.RefreshNativeFakeGrid(normalGrid, page);
        }
    }

    private unsafe void RefreshNativeFakeGrid(AddonInventoryGrid* grid, int page)
    {
        foreach (var fakeItem in this.configuration.FakeItems)
        {
            if (fakeItem.DisplayIndex / SlotsPerPage != page
                || !this.IsRealDisplaySlotEmpty(fakeItem.DisplayIndex))
                continue;

            var slot = fakeItem.DisplayIndex % SlotsPerPage;
            if (slot < 0 || slot >= grid->Slots.Length)
                continue;

            var component = grid->Slots[slot].Value;
            if (component == null)
                continue;

            this.nativeFakeTargetCount++;
            if (this.ApplyNativeFakeVisual(component, fakeItem))
                this.nativeFakeLoadedCount++;
            if (IsNativeFakeVisualVisible(component, fakeItem.Quantity))
                this.nativeFakeVisibleCount++;
        }
    }
    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory || args.Target is not MenuTargetInventory target)
            return;

        var item = target.TargetItem;
        if (item is null || !TryToPlayerInventory(item.Value.ContainerType, out var container))
            return;

        var physical = new PhysicalSlot((ushort)container, checked((ushort)item.Value.InventorySlot));
        if (!this.TryFindDisplaySlot(physical, out var key))
            return;
        var isLocked = this.configuration.LockedSlots.Contains(key);
        args.AddMenuItem(new MenuItem
        {
            Name = isLocked ? "解除锁定此格" : "锁定此格",
            OnClicked = _ => this.SetLocked((InventoryType)key.Container, key.Slot, !isLocked),
            Priority = 100,
        });
    }

    private void InitializeTracking()
    {
        try
        {
            if (!this.CaptureProtectedEntries())
                return;

            this.trackingInitialized = true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "初始化受保护道具追踪时发生错误。");
        }
    }

    private void ClearTracking()
    {
        this.protectedEntries.Clear();
        this.trackingInitialized = false;
    }
    private unsafe void SetLocked(InventoryType container, ushort slot, bool locked)
    {
        var key = new LockedSlot((int)container, slot);
        var displayIndex = GetDisplayIndex(key);
        var hadPhysical = this.protectedEntries.TryGetValue(displayIndex, out var physical);

        if (locked && !hadPhysical && !this.TryGetPhysicalAtDisplayIndex(displayIndex, out physical))
            return;

        var changed = locked
            ? this.configuration.LockedSlots.Add(key)
            : this.configuration.LockedSlots.Remove(key);
        if (!changed)
            return;

        if (locked)
            this.protectedEntries[displayIndex] = physical;
        else if (hadPhysical)
            this.protectedEntries.Remove(displayIndex);

        this.configuration.Save(PluginInterface);
    }

    private unsafe int MoveItemSlotDetour(
        InventoryManager* manager,
        InventoryType sourceContainer,
        ushort sourceSlot,
        InventoryType targetContainer,
        ushort targetSlot,
        bool unknown)
    {
        var sourcePhysical = new PhysicalSlot((ushort)sourceContainer, sourceSlot);
        var targetPhysical = new PhysicalSlot((ushort)targetContainer, targetSlot);
        var hasSourceDisplay = this.TryFindDisplaySlot(sourcePhysical, out var sourceDisplay);
        var hasTargetDisplay = this.TryFindDisplaySlot(targetPhysical, out var targetDisplay);
        var sourceLocked = hasSourceDisplay && this.configuration.LockedSlots.Contains(sourceDisplay);
        var targetLocked = hasTargetDisplay && this.configuration.LockedSlots.Contains(targetDisplay);

        var result = this.moveItemHook.Original(
            manager, sourceContainer, sourceSlot, targetContainer, targetSlot, unknown);
        if (result < 0 || (!sourceLocked && !targetLocked) || !hasSourceDisplay || !hasTargetDisplay)
            return result;

        this.UpdateTrackingAfterMove(
            sourceDisplay,
            targetDisplay,
            sourcePhysical,
            targetPhysical,
            sourceLocked,
            targetLocked);
        return result;
    }

    private void UpdateTrackingAfterMove(
        LockedSlot sourceDisplay,
        LockedSlot targetDisplay,
        PhysicalSlot sourcePhysical,
        PhysicalSlot targetPhysical,
        bool sourceLocked,
        bool targetLocked)
    {
        if (sourceLocked && targetLocked)
            return;

        var sourceIndex = GetDisplayIndex(sourceDisplay);
        var targetIndex = GetDisplayIndex(targetDisplay);
        if (sourceLocked)
        {
            this.configuration.LockedSlots.Remove(sourceDisplay);
            this.configuration.LockedSlots.Add(targetDisplay);
            this.protectedEntries.Remove(sourceIndex);
            this.protectedEntries[targetIndex] = targetPhysical;
        }
        else if (targetLocked)
        {
            this.configuration.LockedSlots.Remove(targetDisplay);
            this.configuration.LockedSlots.Add(sourceDisplay);
            this.protectedEntries.Remove(targetIndex);
            this.protectedEntries[sourceIndex] = sourcePhysical;
        }

        this.configuration.Save(PluginInterface);
    }

    private unsafe void EnsureInventoryContextHook()
    {
        var inventoryAgent = AgentInventory.Instance();
        var callback = inventoryAgent == null ? null : inventoryAgent->CurrentInventoryContextEvent;
        if (callback == null || callback->VirtualTable == null)
            return;

        var address = (nint)callback->VirtualTable->HandleCallback;
        if (address == 0 || this.inventoryContextHooks.ContainsKey(address))
            return;

        var hook = InteropProvider.HookFromAddress<InventoryContextCallbackDelegate>(
            address,
            this.InventoryContextCallbackDetour);
        hook.Enable();
        this.inventoryContextHooks[address] = hook;
        Log.Debug("已挂接物品交付回调：{Address:X}", address);
    }

    private unsafe void InventoryContextCallbackDetour(
        AgentInventoryContext.InventoryContextEvent* callback,
        uint slot,
        InventoryType inventoryType,
        InventoryContextFlag flags,
        ulong callbackParam)
    {
        var address = callback == null || callback->VirtualTable == null
            ? 0
            : (nint)callback->VirtualTable->HandleCallback;

        if (this.IsDangerousTransferContext()
            && slot <= ushort.MaxValue
            && this.IsProtectedPhysicalSlot(inventoryType, (ushort)slot))
        {
            ChatGui.PrintError("该道具已被保护，不能出售或上交任务。", ChatTag, ChatTagColor);
            Log.Debug("已阻止锁定道具进入商店/任务：{Container}/{Slot}", inventoryType, slot);
            return;
        }

        if (address != 0 && this.inventoryContextHooks.TryGetValue(address, out var hook))
            hook.Original(callback, slot, inventoryType, flags, callbackParam);
    }

    private unsafe bool IsDangerousTransferContext()
    {
        var shop = AgentShop.Instance();
        if (shop != null && shop->IsAgentActive())
            return true;

        var npcTrade = AgentNpcTrade.Instance();
        if (npcTrade != null && npcTrade->IsAgentActive())
            return true;

        var request = GameGui.GetAddonByName("Request");
        return !request.IsNull && request.IsVisible;
    }
    private unsafe int DiscardItemDetour(
        InventoryManager* manager,
        InventoryType container,
        ushort slot)
    {
        if (this.IsProtectedPhysicalSlot(container, slot))
        {
            Log.Debug("已阻止丢弃锁定格子中的物品：{Container}/{Slot}", container, slot);
            return -1;
        }

        return this.discardItemHook.Original(manager, container, slot);
    }

    private unsafe bool CaptureProtectedEntries()
    {
        var module = ItemOrderModule.Instance();
        var sorter = module == null ? null : module->InventorySorter;
        if (sorter == null || sorter->Items.Count < PlayerInventories.Length * SlotsPerPage)
            return false;

        this.protectedEntries.Clear();
        foreach (var locked in this.configuration.LockedSlots)
        {
            var displayIndex = GetDisplayIndex(locked);
            if (displayIndex < 0 || displayIndex >= sorter->Items.Count)
                continue;

            var entry = sorter->Items[displayIndex].Value;
            if (entry != null)
                this.protectedEntries[displayIndex] = new PhysicalSlot(entry->Page, entry->Slot);
        }

        return true;
    }

    private unsafe void EnforceLockedDisplaySlots()
    {
        if (this.protectedEntries.Count == 0)
            return;

        var module = ItemOrderModule.Instance();
        var sorter = module == null ? null : module->InventorySorter;
        if (sorter == null)
            return;

        // Framework.Update 位于插件 UI 绘制之前：排序进行中也逐帧固定锁定条目，
        // 避免用户看到物品先被整理挪走、结束后再突然跳回。

        var items = sorter->Items.AsSpan();
        if (items.Length < PlayerInventories.Length * SlotsPerPage)
            return;

        var needsRestore = false;
        foreach (var pair in this.protectedEntries)
        {
            if (pair.Key < items.Length && EntryMatches(items[pair.Key], pair.Value))
                continue;

            needsRestore = true;
            break;
        }

        if (!needsRestore)
            return;

        var current = items.ToArray();
        var protectedSet = this.protectedEntries.Values.ToHashSet();
        var protectedPointers = new Dictionary<PhysicalSlot, Pointer<ItemOrderModuleSorterItemEntry>>();
        var unlocked = new Queue<Pointer<ItemOrderModuleSorterItemEntry>>();

        foreach (var pointer in current)
        {
            if (pointer.Value == null)
                return;

            var physical = new PhysicalSlot(pointer.Value->Page, pointer.Value->Slot);
            if (protectedSet.Contains(physical))
                protectedPointers[physical] = pointer;
            else
                unlocked.Enqueue(pointer);
        }

        if (protectedPointers.Count != this.protectedEntries.Count)
        {
            Log.Warning("无法恢复锁定格：排序映射中的条目数量不一致。");
            return;
        }

        for (var index = 0; index < items.Length; index++)
        {
            if (this.protectedEntries.TryGetValue(index, out var protectedPhysical))
                items[index] = protectedPointers[protectedPhysical];
            else
                items[index] = unlocked.Dequeue();
        }

        // 让游戏自己的用户文件队列异步保存，避免在框架线程同步写盘造成卡顿。
        module->HasChanges = true;
        if (sorter->SortFunctionIndex == -1 && sorter->PercentComplete == 100)
            Log.Debug("已完成包含 {Count} 个锁定格的自动整理。", this.protectedEntries.Count);
    }

    private unsafe bool TryGetPhysicalAtDisplayIndex(int displayIndex, out PhysicalSlot physical)
    {
        physical = default;
        var module = ItemOrderModule.Instance();
        var sorter = module == null ? null : module->InventorySorter;
        if (sorter == null || displayIndex < 0 || displayIndex >= sorter->Items.Count)
            return false;

        var entry = sorter->Items[displayIndex].Value;
        if (entry == null)
            return false;

        physical = new PhysicalSlot(entry->Page, entry->Slot);
        return true;
    }

    private unsafe bool TryFindDisplaySlot(PhysicalSlot physical, out LockedSlot display)
    {
        display = default;
        var module = ItemOrderModule.Instance();
        var sorter = module == null ? null : module->InventorySorter;
        if (sorter == null)
            return false;

        var items = sorter->Items.AsSpan();
        for (var index = 0; index < items.Length; index++)
        {
            if (!EntryMatches(items[index], physical))
                continue;

            display = new LockedSlot(index / SlotsPerPage, (ushort)(index % SlotsPerPage));
            return display.Container < PlayerInventories.Length;
        }

        return false;
    }

    private static int GetDisplayIndex(LockedSlot slot)
        => (slot.Container * SlotsPerPage) + slot.Slot;

    private static unsafe bool EntryMatches(
        Pointer<ItemOrderModuleSorterItemEntry> pointer,
        PhysicalSlot physical)
        => pointer.Value != null
           && pointer.Value->Page == physical.Page
           && pointer.Value->Slot == physical.Slot;

    private bool IsProtectedPhysicalSlot(InventoryType container, ushort slot)
        => this.protectedEntries.Values.Contains(new PhysicalSlot((ushort)container, slot));
    private bool IsLocked(InventoryType container, ushort slot)
        => this.configuration.LockedSlots.Contains(new LockedSlot((int)container, slot));

    private static bool IsPlayerInventory(InventoryType container)
        => container is InventoryType.Inventory1
            or InventoryType.Inventory2
            or InventoryType.Inventory3
            or InventoryType.Inventory4;

    private static bool TryToPlayerInventory(GameInventoryType source, out InventoryType container)
    {
        container = (InventoryType)(int)source;
        return IsPlayerInventory(container);
    }

    private unsafe void DrawInventoryMarkers()
    {
        this.locatedInventoryGridCount = 0;
        this.drawnInventoryMarkerCount = 0;
        this.matchedLockedSlotCount = 0;
        this.missingSlotNodeCount = 0;
        this.invalidSlotBoundsCount = 0;
        this.occludedMarkerCount = 0;
        this.fakeDropHandledThisFrame = false;
        this.fakeTooltipHoveredThisFrame = false;

        if (!ClientState.IsLoggedIn)
        {
            this.HideFakeTooltip();
            return;
        }

        var drawList = ImGui.GetForegroundDrawList();
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.04f, 0.01f, 0.90f));
        var fillColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.43f, 0.10f, 0.96f));

        // Expanded inventory uses four distinct E-suffixed grids. Prefer these as
        // the non-E grids can remain loaded for a different inventory layout.
        var expandedGridCount = 0;
        for (var page = 0; page < PlayerInventories.Length; page++)
        {
            var grid = GameGui.GetAddonByName<AddonInventoryGrid>($"InventoryGrid{page}E");
            if (grid == null || !grid->IsVisible)
                continue;

            expandedGridCount++;
            this.DrawInventoryGridMarkers(
                grid,
                page,
                drawList,
                shadowColor,
                fillColor);
        }

        if (expandedGridCount > 0)
        {
            this.FinishFakeDrag(drawList);
            return;
        }

        // Large inventory displays two pages at once. Tab 0 is pages 1-2 and
        // tab 1 is pages 3-4.
        var large = GameGui.GetAddonByName<AddonInventoryLarge>("InventoryLarge");
        if (large != null && large->IsVisible)
        {
            var firstPage = Math.Clamp(large->TabIndex, 0, 1) * 2;
            for (var index = 0; index < 2; index++)
            {
                var grid = GameGui.GetAddonByName<AddonInventoryGrid>($"InventoryGrid{index}");
                if (grid != null)
                    this.DrawInventoryGridMarkers(
                        grid,
                        firstPage + index,
                        drawList,
                        shadowColor,
                        fillColor);
            }

            this.FinishFakeDrag(drawList);
            return;
        }

        // Normal inventory has one reusable grid. Its parameter is the selected
        // zero-based inventory page.
        var normalGrid = GameGui.GetAddonByName<AddonInventoryGrid>("InventoryGrid");
        if (normalGrid != null)
        {
            var page = (int)normalGrid->Param;
            if (page < 0 || page >= PlayerInventories.Length)
                page = 0;
            this.DrawInventoryGridMarkers(
                normalGrid,
                page,
                drawList,
                shadowColor,
                fillColor);
        }

        this.FinishFakeDrag(drawList);
    }

    private unsafe void DrawInventoryGridMarkers(
        AddonInventoryGrid* grid,
        int page,
        ImDrawListPtr drawList,
        uint shadowColor,
        uint fillColor)
    {
        if (!grid->IsVisible)
            return;

        this.locatedInventoryGridCount++;
        var slots = grid->Slots;
        for (ushort slot = 0; slot < SlotsPerPage && slot < slots.Length; slot++)
        {
            var displayIndex = (page * SlotsPerPage) + slot;
            var locked = this.configuration.LockedSlots.Contains(new LockedSlot(page, slot));
            var hasFake = this.TryGetFakeItem(displayIndex, out var fakeItem)
                && this.IsRealDisplaySlotEmpty(displayIndex);
            if (!locked && !hasFake && this.draggingFakeDisplayIndex < 0)
                continue;

            if (locked)
                this.matchedLockedSlotCount++;

            var component = slots[slot].Value;
            var node = component == null ? null : component->OwnerNode;
            if (node == null && component != null)
                node = (AtkComponentNode*)&component->AtkResNode;
            if (node == null)
            {
                if (locked)
                    this.missingSlotNodeCount++;
                continue;
            }

            if (!TryGetNodeBounds((AtkUnitBase*)grid, &node->AtkResNode, out var min, out var max))
            {
                if (locked)
                    this.invalidSlotBoundsCount++;
                continue;
            }

            var center = (min + max) * 0.5f;
            if (!IsInventoryTopmostAt((AtkUnitBase*)grid, center))
            {
                if (locked)
                    this.occludedMarkerCount++;
                continue;
            }

            this.HandleFakeInventoryInput(displayIndex, min, max, hasFake);

            if (hasFake)
            {
                this.DrawFakeInventoryTooltip(
                    (AtkUnitBase*)grid,
                    &node->AtkResNode,
                    displayIndex,
                    min,
                    max,
                    fakeItem);
            }

            if (locked)
            {
                DrawPadlock(drawList, new Vector2(max.X - 10f, min.Y + 10f), shadowColor, fillColor);
                this.drawnInventoryMarkerCount++;
            }
        }
    }

    private void HandleFakeInventoryInput(int displayIndex, Vector2 min, Vector2 max, bool hasFake)
    {
        var hovered = ImGui.IsMouseHoveringRect(min, max);
        if (hovered && hasFake && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            // Deliberately no game action: local "use" is acknowledged and discarded.
            this.draggingFakeDisplayIndex = -1;
            return;
        }

        if (hovered && hasFake && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            this.RemoveFakeItem(displayIndex);
            this.draggingFakeDisplayIndex = -1;
            return;
        }

        if (hovered && hasFake && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            this.draggingFakeDisplayIndex = displayIndex;

        if (this.draggingFakeDisplayIndex >= 0
            && hovered
            && ImGui.IsMouseReleased(ImGuiMouseButton.Left)
            && this.IsRealDisplaySlotEmpty(displayIndex))
        {
            this.MoveFakeItem(this.draggingFakeDisplayIndex, displayIndex);
            this.fakeDropHandledThisFrame = true;
            this.draggingFakeDisplayIndex = -1;
        }
    }

    private unsafe void FinishFakeDrag(ImDrawListPtr drawList)
    {
        if (!this.fakeTooltipHoveredThisFrame)
            this.HideFakeTooltip();
        if (this.draggingFakeDisplayIndex < 0)
            return;

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!this.fakeDropHandledThisFrame)
                this.draggingFakeDisplayIndex = -1;
            return;
        }

        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left)
            || !this.TryGetFakeItem(this.draggingFakeDisplayIndex, out var fakeItem))
            return;

        var sheet = DataManager.GetExcelSheet<LuminaItem>();
        if (!sheet.TryGetRow(fakeItem.ItemId, out var row))
            return;
        var texture = this.GetItemIcon(row.Icon, false);
        if (!texture.TryGetWrap(out var wrap, out _))
            return;

        var min = ImGui.GetMousePos() + new Vector2(12f, 12f);
        var max = min + new Vector2(42f, 42f);
        drawList.AddImage(wrap.Handle, min, max);
    }
    private unsafe bool ApplyNativeFakeVisual(AtkComponentDragDrop* component, FakeItem fakeItem)
    {
        if (component == null)
            return false;

        var sheet = DataManager.GetExcelSheet<LuminaItem>();
        if (!sheet.TryGetRow(fakeItem.ItemId, out var row) || row.Icon == 0)
            return false;

        component->LoadIcon(row.Icon);
        component->SetQuantity((int)Math.Min(fakeItem.Quantity, int.MaxValue));

        var icon = component->AtkComponentIcon;
        if (icon == null)
            return false;

        if (icon->OwnerNode != null)
        {
            icon->OwnerNode->IsDrawDisabled = false;
            icon->OwnerNode->SetAlpha(255);
            icon->OwnerNode->ToggleVisibility(true);
        }
        if (icon->UldManager.RootNode != null)
        {
            icon->UldManager.RootNode->IsDrawDisabled = false;
            icon->UldManager.RootNode->SetAlpha(255);
            icon->UldManager.RootNode->ToggleVisibility(true);
        }
        if (icon->OuterResNode != null)
        {
            icon->OuterResNode->IsDrawDisabled = false;
            icon->OuterResNode->SetAlpha(255);
            icon->OuterResNode->ToggleVisibility(true);
        }
        if (icon->FrameContainer != null)
        {
            icon->FrameContainer->IsDrawDisabled = false;
            icon->FrameContainer->SetAlpha(255);
            icon->FrameContainer->ToggleVisibility(true);
        }
        if (icon->IconImage != null)
        {
            icon->IconImage->IsDrawDisabled = false;
            icon->IconImage->SetAlpha(255);
            icon->IconImage->ToggleVisibility(true);
        }
        if (icon->QuantityText != null)
        {
            icon->QuantityText->IsDrawDisabled = false;
            icon->QuantityText->SetAlpha(255);
            icon->QuantityText->ToggleVisibility(fakeItem.Quantity > 1);
        }

        return component->GetIconId() == row.Icon;
    }

    private static unsafe bool IsNativeFakeVisualVisible(AtkComponentDragDrop* component, uint quantity)
    {
        var icon = component == null ? null : component->AtkComponentIcon;
        return icon != null
            && icon->OwnerNode != null
            && icon->OwnerNode->IsVisible()
            && icon->IconImage != null
            && icon->IconImage->IsVisible()
            && !icon->IconImage->IsDrawDisabled
            && (quantity <= 1 || (icon->QuantityText != null && icon->QuantityText->IsVisible()));
    }
    private unsafe void HideFakeTooltip()
    {
        if (this.shownFakeTooltipDisplayIndex < 0)
            return;

        var stage = AtkStage.Instance();
        if (stage != null)
            stage->TooltipManager.HideTooltip(this.shownFakeTooltipParentId);
        this.shownFakeTooltipDisplayIndex = -1;
        this.shownFakeTooltipParentId = 0;
    }
    private unsafe void DrawFakeInventoryTooltip(
        AtkUnitBase* grid,
        AtkResNode* targetNode,
        int displayIndex,
        Vector2 min,
        Vector2 max,
        FakeItem fakeItem)
    {
        if (!ImGui.IsMouseHoveringRect(min, max))
            return;

        this.fakeTooltipHoveredThisFrame = true;
        if (this.shownFakeTooltipDisplayIndex == displayIndex)
            return;

        var stage = AtkStage.Instance();
        if (stage == null || grid == null || targetNode == null)
            return;

        var tooltipArgs = stackalloc AtkTooltipManager.AtkTooltipArgs[1];
        tooltipArgs->Ctor();
        tooltipArgs->ItemArgs.ItemId = (int)fakeItem.ItemId;
        tooltipArgs->ItemArgs.Kind = DetailKind.Item;
        stage->TooltipManager.ShowTooltip(
            AtkTooltipType.Item,
            grid->Id,
            targetNode,
            tooltipArgs);

        this.shownFakeTooltipDisplayIndex = displayIndex;
        this.shownFakeTooltipParentId = grid->Id;
    }
    private unsafe void ClearNativeFakeVisual(int displayIndex)
    {
        if (displayIndex < 0 || displayIndex >= PlayerInventories.Length * SlotsPerPage)
            return;
        if (!this.IsRealDisplaySlotEmpty(displayIndex))
            return;

        var page = displayIndex / SlotsPerPage;
        var slot = displayIndex % SlotsPerPage;
        AddonInventoryGrid* grid = GameGui.GetAddonByName<AddonInventoryGrid>($"InventoryGrid{page}E");

        if (grid == null)
        {
            var large = GameGui.GetAddonByName<AddonInventoryLarge>("InventoryLarge");
            if (large != null)
            {
                var firstPage = Math.Clamp(large->TabIndex, 0, 1) * 2;
                if (page >= firstPage && page < firstPage + 2)
                    grid = GameGui.GetAddonByName<AddonInventoryGrid>($"InventoryGrid{page - firstPage}");
            }
        }

        if (grid == null)
        {
            var normal = GameGui.GetAddonByName<AddonInventoryGrid>("InventoryGrid");
            if (normal != null && (int)normal->Param == page)
                grid = normal;
        }

        if (grid == null || slot >= grid->Slots.Length)
            return;

        var component = grid->Slots[slot].Value;
        if (component == null)
            return;

        component->LoadIcon(0);
        component->SetQuantity(0);
        var icon = component->AtkComponentIcon;
        if (icon != null)
        {
            icon->UnloadIcon();
            if (icon->IconImage != null)
                icon->IconImage->ToggleVisibility(false);
            if (icon->QuantityText != null)
                icon->QuantityText->ToggleVisibility(false);
            if (icon->OuterResNode != null)
                icon->OuterResNode->ToggleVisibility(false);
        }
    }
    private static unsafe bool IsInventoryTopmostAt(AtkUnitBase* inventoryGrid, Vector2 point)
    {
        var stage = AtkStage.Instance();
        var manager = stage == null ? null : stage->RaptureAtkUnitManager;
        if (inventoryGrid == null || manager == null)
            return true;

        AddonCollision collision = default;
        var x = (short)Math.Clamp((int)MathF.Round(point.X), short.MinValue, short.MaxValue);
        var y = (short)Math.Clamp((int)MathF.Round(point.Y), short.MinValue, short.MaxValue);
        manager->GetAddonCollision(&collision, x, y);

        var topmost = collision.UnitBase;
        if (topmost == null)
            return true;

        return topmost == inventoryGrid
            || topmost->Id == inventoryGrid->Id
            || topmost->Id == inventoryGrid->ParentId
            || topmost->Id == inventoryGrid->HostId
            || topmost->ParentId == inventoryGrid->Id
            || topmost->HostId == inventoryGrid->Id;
    }
    private static unsafe bool TryGetNodeBounds(
        AtkUnitBase* addon,
        AtkResNode* node,
        out Vector2 min,
        out Vector2 max)
    {
        min = default;
        max = default;
        if (addon == null || node == null)
            return false;

        float scaleX = 1f;
        float scaleY = 1f;
        node->GetScale(&scaleX, &scaleY);

        min = new Vector2(node->ScreenX, node->ScreenY);
        max = min + new Vector2(node->Width * scaleX, node->Height * scaleY);
        return max.X > min.X && max.Y > min.Y;
    }
    private static void DrawPadlock(
        ImDrawListPtr drawList,
        Vector2 center,
        uint shadowColor,
        uint fillColor)
    {
        var bodyMin = center + new Vector2(-5.5f, -1f);
        var bodyMax = center + new Vector2(5.5f, 7f);
        drawList.AddCircle(center + new Vector2(0, -1f), 4.3f, shadowColor, 16, 4.5f);
        drawList.AddCircle(center + new Vector2(0, -1f), 4.0f, fillColor, 16, 2.2f);
        drawList.AddRectFilled(bodyMin - Vector2.One, bodyMax + Vector2.One, shadowColor, 2f);
        drawList.AddRectFilled(bodyMin, bodyMax, fillColor, 1.5f);
    }
    private void DrawWindow()
    {
        this.DrawInventoryMarkers();

        if (!this.windowOpen)
            return;

        ImGui.SetNextWindowSizeConstraints(new(620, 520), new(1100, 1000));
        if (!ImGui.Begin("背包格子锁###InventorySlotLockConfig", ref this.windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("点击格子即可锁定或解锁。显示顺序与游戏背包一致；锁定道具不会参与自动整理；仍可选中、使用和拖动，但不能出售、丢弃或上交任务。");
        ImGui.TextDisabled("命令：/isl");
        ImGui.Separator();

        this.DrawFakeItemBrowser();
        ImGui.Separator();

        if (!ClientState.IsLoggedIn)
        {
            ImGui.TextDisabled("登录角色后才能读取背包内容。");
        }
        else if (ImGui.BeginTabBar("InventoryPages"))
        {
            for (var page = 0; page < PlayerInventories.Length; page++)
            {
                if (!ImGui.BeginTabItem($"第 {page + 1} 页"))
                    continue;

                this.DrawPage(PlayerInventories[page]);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.Separator();
        ImGui.TextDisabled($"原生背包标记：格子页 {this.locatedInventoryGridCount}，匹配 {this.matchedLockedSlotCount}，绘制 {this.drawnInventoryMarkerCount}，被窗口遮挡 {this.occludedMarkerCount}，缺少节点 {this.missingSlotNodeCount}，坐标失败 {this.invalidSlotBoundsCount}");
        ImGui.TextDisabled($"幽灵原生图标：目标 {this.nativeFakeTargetCount}，已加载 {this.nativeFakeLoadedCount}，节点可见 {this.nativeFakeVisibleCount}");
        ImGui.TextUnformatted($"已锁定 {this.configuration.LockedSlots.Count} 个格子");
        ImGui.SameLine();
        if (ImGui.Button("全部解锁"))
        {
            this.ClearTracking();
            this.configuration.LockedSlots.Clear();
            this.configuration.Save(PluginInterface);
            this.trackingInitialized = true;
        }

        ImGui.End();
    }

    private void DrawFakeItemBrowser()
    {
        if (!ImGui.CollapsingHeader("本地整蛊物品 / 物品 ID 浏览器"))
            return;

        ImGui.TextDisabled("宏命令：/isl create <物品ID> <数量>    清空命令：/isl clearfake");

        ImGui.SetNextItemWidth(360f);
        ImGui.InputText("搜索名称或物品 ID", ref this.itemBrowserSearch, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        ImGui.InputInt("生成数量", ref this.fakeItemQuantity);
        this.fakeItemQuantity = Math.Max(1, this.fakeItemQuantity);
        ImGui.SameLine();
        if (ImGui.Button($"清除全部幽灵物品 ({this.configuration.FakeItems.Count})"))
        {
            foreach (var fakeItem in this.configuration.FakeItems)
                this.ClearNativeFakeVisual(fakeItem.DisplayIndex);
            this.configuration.FakeItems.Clear();
            this.configuration.Save(PluginInterface);
        }

        if (string.IsNullOrWhiteSpace(this.itemBrowserSearch))
        {
            ImGui.TextDisabled("输入名称或 ID 后显示匹配物品。每行会显示物品 ID、名称和图标。");
            return;
        }

        var search = this.itemBrowserSearch.Trim();
        ImGui.BeginChild("FakeItemBrowserResults", new Vector2(0f, 220f), true);
        var shown = 0;
        foreach (var row in DataManager.GetExcelSheet<LuminaItem>())
        {
            var name = row.Name.ToString();
            if (row.RowId == 0 || string.IsNullOrWhiteSpace(name))
                continue;
            if (!row.RowId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
                && !name.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            ImGui.PushID((int)row.RowId);
            var texture = this.GetItemIcon(row.Icon, false);
            if (texture.TryGetWrap(out var wrap, out _))
            {
                ImGui.Image(wrap.Handle, new Vector2(30f, 30f));
                ImGui.SameLine();
            }

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"ID {row.RowId}  {name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("生成"))
                this.CreateFakeItem(row.RowId, (uint)this.fakeItemQuantity);
            ImGui.PopID();

            shown++;
            if (shown >= 100)
                break;
        }

        if (shown == 0)
            ImGui.TextDisabled("没有找到匹配物品。");
        else if (shown >= 100)
            ImGui.TextDisabled("仅显示前 100 条结果，请继续缩小搜索范围。");
        ImGui.EndChild();
    }
    private void DrawPage(InventoryType container)
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var cellWidth = MathF.Max(96f, (availableWidth - (spacing * (Columns - 1))) / Columns);
        const float CellHeight = 118f;

        for (ushort slot = 0; slot < SlotsPerPage; slot++)
        {
            var key = new LockedSlot((int)container, slot);
            var displayIndex = GetDisplayIndex(key);
            var locked = this.configuration.LockedSlots.Contains(key);
            var item = this.GetDisplayItem(displayIndex);

            var background = item.IsFake
                ? new Vector4(0.15f, 0.10f, 0.24f, 0.92f)
                : locked
                    ? new Vector4(0.34f, 0.12f, 0.10f, 0.92f)
                    : new Vector4(0.08f, 0.08f, 0.09f, 0.86f);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
            ImGui.PushStyleColor(ImGuiCol.Border,
                item.IsFake
                    ? new Vector4(0.58f, 0.38f, 0.92f, 1.00f)
                    : locked ? new Vector4(1.00f, 0.43f, 0.24f, 1.00f) : new Vector4(0.30f, 0.30f, 0.33f, 1.00f));

            ImGui.BeginChild(
                $"slot_{(int)container}_{slot}",
                new Vector2(cellWidth, CellHeight),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

            if (item.ItemId != 0)
            {
                var texture = this.GetItemIcon(item.IconId, item.IsHq);
                if (texture.TryGetWrap(out var wrap, out _))
                {
                    ImGui.Image(wrap.Handle, new Vector2(42f, 42f));
                    ImGui.SameLine();
                }

                ImGui.BeginGroup();
                ImGui.TextColored(
                    item.IsHq ? new Vector4(0.80f, 0.74f, 0.40f, 1f) : Vector4.One,
                    item.Quantity > 1 ? $"×{item.Quantity}" : "×1");
                if (item.IsFake)
                    ImGui.TextColored(new Vector4(0.72f, 0.52f, 1.00f, 1f), "[本地幽灵]");
                else if (locked)
                    ImGui.TextColored(new Vector4(1f, 0.58f, 0.35f, 1f), "[已锁定]");
                ImGui.EndGroup();

                ImGui.TextWrapped(item.Name);
            }
            else
            {
                ImGui.Dummy(new Vector2(0, 35f));
                ImGui.TextDisabled($"空格  {slot + 1}");
                if (item.IsFake)
                    ImGui.TextColored(new Vector4(0.72f, 0.52f, 1.00f, 1f), "[本地幽灵]");
                else if (locked)
                    ImGui.TextColored(new Vector4(1f, 0.58f, 0.35f, 1f), "[已锁定]");
            }

            var hovered = ImGui.IsWindowHovered();
            if (hovered && item.ItemId != 0 && !item.IsFake && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                this.SetLocked(container, slot, !locked);
            if (hovered && item.IsFake && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                this.RemoveFakeItem(displayIndex);

            if (hovered && item.ItemId != 0)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(item.Name);
                ImGui.TextDisabled($"物品 ID：{item.ItemId}  |  数量：{item.Quantity}  |  第 {(int)container + 1} 页第 {slot + 1} 格");
                ImGui.TextUnformatted(item.IsFake
                    ? "纯本地幽灵物品：双击使用无效果；可拖动；右键丢弃"
                    : locked ? "点击解除锁定" : "点击锁定此格");
                ImGui.EndTooltip();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor(2);

            if ((slot + 1) % Columns != 0)
                ImGui.SameLine();
        }
    }

    private DisplayItem GetDisplayItem(int displayIndex)
    {
        var realItem = this.GetRealDisplayItem(displayIndex);
        if (realItem.ItemId != 0
            || !this.IsRealDisplaySlotEmpty(displayIndex)
            || !this.TryGetFakeItem(displayIndex, out var fakeItem))
            return realItem;

        var sheet = DataManager.GetExcelSheet<LuminaItem>();
        if (!sheet.TryGetRow(fakeItem.ItemId, out var row))
            return default;

        var name = row.Name.ToString();
        if (string.IsNullOrWhiteSpace(name))
            return default;

        return new DisplayItem(fakeItem.ItemId, name, row.Icon, fakeItem.Quantity, false, true);
    }
    private unsafe bool IsRealDisplaySlotEmpty(int displayIndex)
    {
        var module = ItemOrderModule.Instance();
        var sorter = module == null ? null : module->InventorySorter;
        var manager = InventoryManager.Instance();
        if (sorter == null
            || manager == null
            || displayIndex < 0
            || displayIndex >= sorter->Items.Count)
            return false;

        var entry = sorter->Items[displayIndex].Value;
        if (entry == null)
            return false;

        var nativeItem = manager->GetInventorySlot((InventoryType)entry->Page, entry->Slot);
        return nativeItem != null && nativeItem->ItemId == 0;
    }
    private unsafe DisplayItem GetRealDisplayItem(int displayIndex)
    {
        var module = ItemOrderModule.Instance();
        var sorter = module == null ? null : module->InventorySorter;
        var manager = InventoryManager.Instance();
        if (sorter == null || manager == null || displayIndex < 0 || displayIndex >= sorter->Items.Count)
            return default;

        var entry = sorter->Items[displayIndex].Value;
        if (entry == null)
            return default;

        var nativeItem = manager->GetInventorySlot((InventoryType)entry->Page, entry->Slot);
        if (nativeItem == null || nativeItem->ItemId == 0)
            return default;

        var baseItemId = nativeItem->GetBaseItemId();
        var row = DataManager.GetExcelSheet<LuminaItem>().GetRow(baseItemId);
        return new DisplayItem(
            nativeItem->ItemId,
            row.Name.ToString(),
            row.Icon,
            nativeItem->GetQuantity(),
            nativeItem->IsHighQuality(),
            false);
    }

    private ISharedImmediateTexture GetItemIcon(uint iconId, bool isHq)
    {
        var key = (iconId, isHq);
        if (this.iconCache.TryGetValue(key, out var texture))
            return texture;

        texture = TextureProvider.GetFromGameIcon(new GameIconLookup(iconId, isHq));
        this.iconCache[key] = texture;
        return texture;
    }
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int MoveItemSlotDelegate(
        InventoryManager* manager,
        InventoryType sourceContainer,
        ushort sourceSlot,
        InventoryType targetContainer,
        ushort targetSlot,
        bool unknown);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate void InventoryContextCallbackDelegate(
        AgentInventoryContext.InventoryContextEvent* callback,
        uint slot,
        InventoryType inventoryType,
        InventoryContextFlag flags,
        ulong callbackParam);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int DiscardItemDelegate(
        InventoryManager* manager,
        InventoryType container,
        ushort slot);

    private readonly record struct PhysicalSlot(ushort Page, ushort Slot);

    private readonly record struct DisplayItem(
        uint ItemId,
        string Name,
        uint IconId,
        uint Quantity,
        bool IsHq,
        bool IsFake);
}
