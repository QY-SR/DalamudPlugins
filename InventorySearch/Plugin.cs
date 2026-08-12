using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Textures;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using LuminaCabinet = Lumina.Excel.Sheets.Cabinet;
using LuminaItem = Lumina.Excel.Sheets.Item;
using QianyanLegacy;

namespace InventorySearch;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private const string Command = "/ebsearch";
    private static readonly InventoryType[] InventoryPages =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];
    private static readonly InventoryType[] SaddlebagPages =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
    ];
    private static readonly InventoryType[] PremiumSaddlebagPages =
    [
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];
    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2,
        InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private readonly Configuration configuration;
    private readonly Dictionary<uint, ISharedImmediateTexture> iconCache = new();
    private readonly Dictionary<string, int> withdrawalQuantities = new();
    private readonly Dictionary<string, int> depositQuantities = new();
    private readonly Dictionary<string, BatchWithdrawalItem> selectedWithdrawals = new();
    private readonly Queue<BatchWithdrawalItem> batchWithdrawalQueue = new();
    private readonly Dictionary<string, BatchDepositItem> selectedDeposits = new();
    private readonly Queue<BatchDepositItem> batchDepositQueue = new();
    private readonly Dictionary<string, OrganizerSuggestion> selectedOrganizerSuggestions = new();
    private readonly Queue<OrganizerTransferGroup> organizerGroupQueue = new();
    private readonly Queue<OrganizerSuggestion> organizerWithdrawalQueue = new();
    private readonly Queue<OrganizerDepositItem> organizerDepositQueue = new();
    private bool windowOpen;
    private bool migrationNoticeRequested;
    private string search = string.Empty;
    private string selectedCharacter = "全部角色";
    private int selectedKind;
    private long nextScanTick;
    private ulong lastContentId;
    private string status = "等待角色登录";
    private WithdrawalRequest? withdrawal;
    private DepositRequest? deposit;
    private OrganizerHint? organizerHint;
    private bool showOrganizer;
    private bool batchWithdrawalActive;
    private int batchWithdrawalCompleted;
    private int batchWithdrawalSkipped;
    private bool batchDepositActive;
    private int batchDepositCompleted;
    private int batchDepositSkipped;
    private bool organizerActive;
    private OrganizerSuggestion? currentOrganizerSuggestion;
    private OrganizerTransferGroup? currentOrganizerGroup;
    private int organizerCompleted;
    private int organizerSkipped;

    public Plugin()
    {
        this.configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.configuration.Characters ??= [];
        foreach (var character in this.configuration.Characters)
            character.Storages ??= [];

        CommandManager.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开跨角色背包与收藏搜索窗口。",
            ShowInHelp = true,
        });
        Framework.Update += this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += this.DrawWindow;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenWindow;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= this.DrawWindow;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenWindow;
        Framework.Update -= this.OnFrameworkUpdate;
        CommandManager.RemoveHandler(Command);
        this.iconCache.Clear();
    }

    private void OnCommand(string _, string arguments)
    {
        this.windowOpen = true;
        this.migrationNoticeRequested = true;
        if (arguments.Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase))
            this.ScanNow(true);
    }

    private void OpenWindow()
    {
        this.windowOpen = true;
        this.migrationNoticeRequested = true;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!ClientState.IsLoggedIn)
        {
            this.withdrawal = null;
            this.deposit = null;
            this.batchWithdrawalQueue.Clear();
            this.batchWithdrawalActive = false;
            this.batchDepositQueue.Clear();
            this.batchDepositActive = false;
            this.organizerGroupQueue.Clear();
            this.organizerWithdrawalQueue.Clear();
            this.organizerDepositQueue.Clear();
            this.organizerActive = false;
            this.currentOrganizerSuggestion = null;
            this.currentOrganizerGroup = null;
            this.lastContentId = 0;
            this.status = "等待角色登录";
            return;
        }

        var now = Environment.TickCount64;
        this.ProcessWithdrawal(now);
        this.ProcessDeposit(now);
        if (now < this.nextScanTick)
            return;
        this.nextScanTick = now + 2500;
        this.ScanNow(false);
    }

    private bool CanStartWithdrawal(SearchResult result, int quantity, out string reason)
    {
        if (result.Storage.Kind != StorageKind.Retainer)
        {
            reason = "只有雇员背包里的道具可以直接取出";
            return false;
        }
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || PlayerState.ContentId != result.Character.ContentId)
        {
            reason = "请登录这条记录所属的角色";
            return false;
        }
        if (this.withdrawal != null || this.deposit != null)
        {
            reason = "已有一项取出操作正在进行";
            return false;
        }
        if (quantity < 1 || quantity > result.Item.Quantity)
        {
            reason = $"取出数量必须在 1 到 {result.Item.Quantity} 之间";
            return false;
        }
        if (this.FindNearbySummoningBell() == null)
        {
            reason = "需要站在传唤铃的可交互范围内";
            return false;
        }
        if (!this.TryFindEmptyInventorySlot(out _, out _))
        {
            reason = "角色背包没有空格";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private unsafe void BeginWithdrawal(
        SearchResult result,
        int quantity,
        bool closeAfterMove = false,
        bool organizerMode = false)
    {
        if (!this.CanStartWithdrawal(result, quantity, out var reason))
        {
            this.status = reason;
            return;
        }

        var now = Environment.TickCount64;
        this.withdrawal = new WithdrawalRequest
        {
            CharacterContentId = result.Character.ContentId,
            RetainerId = result.Storage.OwnerId,
            RetainerName = result.Storage.OwnerName,
            ItemId = result.Item.ItemId,
            IsHq = result.Item.IsHq,
            ExpectedQuantity = result.Item.Quantity,
            RequestedQuantity = (uint)quantity,
            SourceContainer = (InventoryType)result.Item.Container,
            SourceSlot = (ushort)result.Item.Slot,
            ItemName = result.Item.Name,
            CloseAfterMove = closeAfterMove,
            OrganizerMode = organizerMode,
            InventoryBeforeMove = organizerMode
                ? this.CaptureMatchingInventoryQuantities(result.Item.ItemId, result.Item.IsHq)
                : [],
            Stage = closeAfterMove ? WithdrawalStage.WaitForBellReady : WithdrawalStage.OpenBell,
            DeadlineTick = now + 45_000,
            NextActionTick = now,
        };
        this.status = $"准备从雇员 {result.Storage.OwnerName} 取出 {result.Item.Name}……";
    }

    private void StartBatchWithdrawal()
    {
        if (this.withdrawal != null || this.deposit != null || this.batchWithdrawalActive || this.batchDepositActive)
        {
            this.status = "已有一项雇员物品操作正在进行";
            return;
        }
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || this.FindNearbySummoningBell() == null)
        {
            this.status = "批量取出需要登录对应角色并站在传唤铃交互范围内";
            return;
        }

        var currentContentId = PlayerState.ContentId;
        var selected = this.selectedWithdrawals.Values
            .Where(entry => entry.Character.ContentId == currentContentId)
            .OrderBy(entry => entry.Storage.OwnerId)
            .ThenBy(entry => entry.Item.Container)
            .ThenBy(entry => entry.Item.Slot)
            .ToList();
        this.batchWithdrawalSkipped = this.selectedWithdrawals.Count - selected.Count;
        foreach (var entry in selected)
        {
            if (entry.Quantity < 1 || entry.Quantity > entry.Item.Quantity)
            {
                this.batchWithdrawalSkipped++;
                continue;
            }
            this.batchWithdrawalQueue.Enqueue(entry);
        }
        if (this.batchWithdrawalQueue.Count == 0)
        {
            this.status = "没有数量有效且属于当前角色的已勾选记录";
            return;
        }

        this.selectedWithdrawals.Clear();
        this.batchWithdrawalActive = true;
        this.batchWithdrawalCompleted = 0;
        this.StartNextBatchWithdrawal();
    }

    private void StartNextBatchWithdrawal()
    {
        if (!this.batchWithdrawalActive)
            return;
        if (!this.batchWithdrawalQueue.TryDequeue(out var entry))
        {
            this.batchWithdrawalActive = false;
            this.status = $"批量取出完成：成功 {this.batchWithdrawalCompleted} 条，跳过 {this.batchWithdrawalSkipped} 条";
            return;
        }

        var result = new SearchResult(entry.Character, entry.Storage, entry.Item);
        this.BeginWithdrawal(result, entry.Quantity, true);
        if (this.withdrawal == null)
        {
            this.batchWithdrawalQueue.Clear();
            this.batchWithdrawalActive = false;
            this.status += "；批量队列已停止";
        }
    }

    private unsafe Dictionary<InventorySlotKey, uint> CaptureMatchingInventoryQuantities(uint itemId, bool isHq)
    {
        var quantities = new Dictionary<InventorySlotKey, uint>();
        var manager = InventoryManager.Instance();
        if (manager == null)
            return quantities;
        foreach (var type in InventoryPages)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;
            for (ushort slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                quantities[new InventorySlotKey(type, slot)] = GetMatchingQuantity(item, itemId, isHq);
            }
        }
        return quantities;
    }

    private unsafe bool TryFindReceivedInventorySlot(
        WithdrawalRequest request,
        out InventoryType containerType,
        out ushort slot,
        out uint quantity)
    {
        containerType = default;
        slot = 0;
        quantity = 0;
        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;
        foreach (var type in InventoryPages)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;
            for (ushort currentSlot = 0; currentSlot < container->Size; currentSlot++)
            {
                var currentQuantity = GetMatchingQuantity(
                    container->GetInventorySlot(currentSlot), request.ItemId, request.IsHq);
                request.InventoryBeforeMove.TryGetValue(new InventorySlotKey(type, currentSlot), out var previousQuantity);
                if (currentQuantity >= previousQuantity
                    && currentQuantity - previousQuantity == request.RequestedQuantity)
                {
                    containerType = type;
                    slot = currentSlot;
                    quantity = currentQuantity;
                    return true;
                }
            }
        }
        return false;
    }

    private unsafe bool TryConfirmOperationDialog()
    {
        var talk = GameGui.GetAddonByName<AtkUnitBase>("Talk");
        if (talk != null && talk->IsVisible && talk->IsReady)
        {
            talk->FireCallbackInt(0);
            return true;
        }

        var addon = GameGui.GetAddonByName<AtkUnitBase>("SelectYesno");
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;
        addon->FireCallbackInt(0);
        return true;
    }

    private unsafe void ProcessWithdrawal(long now)
    {
        var request = this.withdrawal;
        if (request == null || now < request.NextActionTick)
            return;

        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || PlayerState.ContentId != request.CharacterContentId)
        {
            this.FailWithdrawal("角色状态已变化，已取消取出");
            return;
        }
        if (now > request.DeadlineTick)
        {
            if (request.MoveCompleted)
                this.FailWithdrawal("道具已成功取出，但自动切换雇员超时；批量队列已停止");
            else
                this.FailWithdrawal("取出超时，请保持在传唤铃附近后重试");
            return;
        }

        try
        {
            if (this.TryConfirmOperationDialog())
            {
                request.NextActionTick = now + 400;
                return;
            }
            var retainerManager = RetainerManager.Instance();
            var activeRetainer = retainerManager == null ? null : retainerManager->GetActiveRetainer();

            switch (request.Stage)
            {
                case WithdrawalStage.WaitForBellReady:
                    if (Condition[ConditionFlag.OccupiedSummoningBell])
                    {
                        if (activeRetainer != null && activeRetainer->RetainerId == request.RetainerId)
                        {
                            request.Stage = WithdrawalStage.WaitForRetainer;
                            request.NextActionTick = now + 200;
                            return;
                        }
                        request.NextActionTick = now + 200;
                        this.status = "正在等待上一位雇员对话完全结束……";
                        return;
                    }
                    request.Stage = WithdrawalStage.OpenBell;
                    request.NextActionTick = now + 200;
                    return;

                case WithdrawalStage.OpenBell:
                    if (Condition[ConditionFlag.OccupiedSummoningBell]
                        && activeRetainer != null && activeRetainer->RetainerId == request.RetainerId)
                    {
                        request.Stage = WithdrawalStage.WaitForRetainer;
                        request.NextActionTick = now + 300;
                        return;
                    }

                    var bell = this.FindNearbySummoningBell();
                    if (bell == null)
                    {
                        this.FailWithdrawal("已离开传唤铃的可交互范围");
                        return;
                    }
                    var targetSystem = TargetSystem.Instance();
                    if (targetSystem == null)
                    {
                        this.FailWithdrawal("无法访问游戏目标系统");
                        return;
                    }
                    targetSystem->InteractWithObject((NativeGameObject*)bell.Address, false);
                    request.Stage = WithdrawalStage.WaitForList;
                    request.NextActionTick = now + 500;
                    this.status = "正在打开雇员列表……";
                    return;

                case WithdrawalStage.WaitForList:
                    var listAddon = GameGui.GetAddonByName<AddonRetainerList>("RetainerList");
                    if (listAddon == null || !listAddon->AtkUnitBase.IsVisible || !listAddon->AtkUnitBase.IsReady)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    if (retainerManager == null)
                    {
                        this.FailWithdrawal("雇员列表数据尚未加载");
                        return;
                    }

                    var sortedIndex = -1;
                    for (var index = 0; index < 10; index++)
                    {
                        var retainer = retainerManager->GetRetainerBySortedIndex((uint)index);
                        if (retainer != null && retainer->RetainerId == request.RetainerId)
                        {
                            sortedIndex = index;
                            break;
                        }
                    }
                    if (sortedIndex < 0)
                    {
                        this.FailWithdrawal($"雇员列表中找不到 {request.RetainerName}");
                        return;
                    }

                    // RetainerList 的行选择不是单整数回调。原生列表要求：操作 2、排序索引、两个空值。
                    var callbackValues = stackalloc AtkValue[4]
                    {
                        new() { Type = AtkValueType.Int, Int = 2 },
                        new() { Type = AtkValueType.UInt, UInt = (uint)sortedIndex },
                        new() { Type = 0, Int = 0 },
                        new() { Type = 0, Int = 0 },
                    };
                    listAddon->AtkUnitBase.FireCallback(4, callbackValues, true);
                    request.Stage = WithdrawalStage.WaitForRetainer;
                    request.NextActionTick = now + 500;
                    this.status = $"正在呼叫雇员 {request.RetainerName}……";
                    return;

                case WithdrawalStage.WaitForRetainer:
                    if (!Condition[ConditionFlag.OccupiedSummoningBell]
                        || activeRetainer == null || activeRetainer->RetainerId != request.RetainerId)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }

                    if (this.IsRetainerInventoryVisible())
                    {
                        request.Stage = WithdrawalStage.RequestRetrieve;
                        request.NextActionTick = now + 500;
                        return;
                    }

                    var selectString = GameGui.GetAddonByName<AtkUnitBase>("SelectString");
                    if (selectString == null || !selectString->IsVisible)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }

                    // 雇员指令菜单的第 1 项是原生“查看雇员所持物品”。只有进入该界面后才允许取物。
                    selectString->FireCallbackInt(0);
                    request.Stage = WithdrawalStage.WaitForInventory;
                    request.NextActionTick = now + 500;
                    this.status = $"正在打开 {request.RetainerName} 的背包……";
                    return;

                case WithdrawalStage.WaitForInventory:
                    if (!Condition[ConditionFlag.OccupiedSummoningBell]
                        || activeRetainer == null || activeRetainer->RetainerId != request.RetainerId)
                    {
                        this.FailWithdrawal("当前雇员已经变化，已取消取出");
                        return;
                    }
                    if (!this.IsRetainerInventoryVisible())
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }

                    request.Stage = WithdrawalStage.RequestRetrieve;
                    request.NextActionTick = now + 500;
                    return;

                case WithdrawalStage.RequestRetrieve:
                    if (!Condition[ConditionFlag.OccupiedSummoningBell]
                        || activeRetainer == null || activeRetainer->RetainerId != request.RetainerId
                        || !this.IsRetainerInventoryVisible())
                    {
                        this.FailWithdrawal("原生雇员背包界面未就绪，已取消取出");
                        return;
                    }

                    var manager = InventoryManager.Instance();
                    var source = manager == null ? null : manager->GetInventoryContainer(request.SourceContainer);
                    if (source == null || !source->IsLoaded || request.SourceSlot >= source->Size)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }

                    var sourceItem = source->GetInventorySlot(request.SourceSlot);
                    if (sourceItem == null || sourceItem->ItemId == 0
                        || sourceItem->GetBaseItemId() != request.ItemId
                        || sourceItem->IsHighQuality() != request.IsHq
                        || sourceItem->GetQuantity() != request.ExpectedQuantity)
                    {
                        this.FailWithdrawal("原栏位的道具已经变化，为避免取错已取消");
                        return;
                    }
                    if (!this.TryFindEmptyInventorySlot(out _, out _))
                    {
                        this.FailWithdrawal("角色背包没有空格");
                        return;
                    }

                    request.QuantityBeforeMove = sourceItem->GetQuantity();
                    var agentModule = AgentModule.Instance();
                    var retainerAgent = agentModule == null
                        ? null
                        : (AgentRetainer*)agentModule->GetAgentByInternalId(AgentId.Retainer);
                    if (retainerAgent == null || !retainerAgent->IsAgentActive())
                    {
                        this.FailWithdrawal("原生雇员物品代理未就绪，已取消取出");
                        return;
                    }

                    var isPartial = request.RequestedQuantity < request.QuantityBeforeMove;
                    retainerAgent->HandleCallback(
                        request.SourceSlot,
                        request.SourceContainer,
                        (InventoryContextFlag)0,
                        isPartial ? 3UL : 0UL);
                    request.Stage = isPartial ? WithdrawalStage.WaitForQuantity : WithdrawalStage.WaitForMove;
                    request.NextActionTick = now + 300;
                    this.status = $"正在取出 {request.ItemName} ×{request.RequestedQuantity}……";
                    return;

                case WithdrawalStage.WaitForQuantity:
                    var inputNumeric = GameGui.GetAddonByName<AddonInputNumeric>("InputNumeric");
                    if (inputNumeric == null || !inputNumeric->AtkUnitBase.IsVisible
                        || !inputNumeric->AtkUnitBase.IsReady || inputNumeric->AtkUnitBase.AtkValuesCount <= 3)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }

                    var nativeMinimum = inputNumeric->AtkUnitBase.AtkValues[2].UInt;
                    var nativeMaximum = inputNumeric->AtkUnitBase.AtkValues[3].UInt;
                    if (request.RequestedQuantity < nativeMinimum || request.RequestedQuantity > nativeMaximum)
                    {
                        this.FailWithdrawal($"游戏当前允许的数量是 {nativeMinimum}～{nativeMaximum}，已取消");
                        return;
                    }

                    inputNumeric->AtkUnitBase.FireCallbackInt((int)request.RequestedQuantity);
                    request.Stage = WithdrawalStage.WaitForMove;
                    request.NextActionTick = now + 300;
                    return;

                case WithdrawalStage.WaitForMove:
                    var inventoryManager = InventoryManager.Instance();
                    var currentContainer = inventoryManager == null ? null : inventoryManager->GetInventoryContainer(request.SourceContainer);
                    var currentItem = currentContainer == null || !currentContainer->IsLoaded
                        ? null
                        : currentContainer->GetInventorySlot(request.SourceSlot);
                    var expectedRemaining = request.QuantityBeforeMove - request.RequestedQuantity;
                    var currentQuantity = currentItem != null
                        && currentItem->ItemId != 0
                        && currentItem->GetBaseItemId() == request.ItemId
                        && currentItem->IsHighQuality() == request.IsHq
                            ? currentItem->GetQuantity()
                            : 0;
                    if (currentQuantity == request.QuantityBeforeMove)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    if (currentQuantity != expectedRemaining)
                    {
                        this.FailWithdrawal("栏位数量出现非预期变化，请手动检查雇员背包");
                        return;
                    }

                    this.ScanNow(true);
                    if (request.OrganizerMode)
                    {
                        if (!this.TryFindReceivedInventorySlot(
                                request,
                                out var receivedContainer,
                                out var receivedSlot,
                                out var receivedQuantity))
                        {
                            this.FailWithdrawal("已取出道具，但无法确认其角色背包格，跨雇员整理已停止");
                            return;
                        }
                        request.ReceivedContainer = receivedContainer;
                        request.ReceivedSlot = receivedSlot;
                        request.ReceivedQuantity = receivedQuantity;
                        request.MoveCompleted = true;
                        if (this.currentOrganizerSuggestion is not { } suggestion)
                        {
                            this.FailWithdrawal("跨雇员整理项目状态丢失，已停止");
                            return;
                        }
                        this.organizerDepositQueue.Enqueue(new OrganizerDepositItem(
                            suggestion, receivedContainer, receivedSlot, receivedQuantity));
                        if (this.organizerWithdrawalQueue.Count > 0)
                        {
                            this.withdrawal = null;
                            this.currentOrganizerSuggestion = null;
                            this.StartNextOrganizerWithdrawal();
                            return;
                        }
                        request.Stage = WithdrawalStage.CloseInventory;
                        request.NextActionTick = now + 500;
                        this.status = "已取出本组道具，正在切换到目标雇员……";
                        return;
                    }
                    if (!request.CloseAfterMove)
                    {
                        this.withdrawal = null;
                        this.status = $"已从 {request.RetainerName} 取出 {request.ItemName} ×{request.RequestedQuantity}";
                        return;
                    }

                    request.MoveCompleted = true;
                    this.batchWithdrawalCompleted++;
                    if (this.batchWithdrawalQueue.TryPeek(out var next)
                        && next.Storage.OwnerId == request.RetainerId)
                    {
                        this.withdrawal = null;
                        this.StartNextBatchWithdrawal();
                        return;
                    }
                    request.Stage = WithdrawalStage.CloseInventory;
                    request.NextActionTick = now + 500;
                    this.status = "正在切换或关闭雇员窗口……";
                    return;

                case WithdrawalStage.CloseInventory:
                    var closeAgentModule = AgentModule.Instance();
                    var closeAgent = closeAgentModule == null
                        ? null
                        : closeAgentModule->GetAgentByInternalId(AgentId.Retainer);
                    if (closeAgent != null && closeAgent->IsAgentActive() && this.IsRetainerInventoryVisible())
                        closeAgent->Hide();
                    request.Stage = WithdrawalStage.WaitForRetainerMenu;
                    request.NextActionTick = now + 500;
                    return;

                case WithdrawalStage.WaitForRetainerMenu:
                    var menu = GameGui.GetAddonByName<AddonSelectString>("SelectString");
                    if (menu == null || !menu->AtkUnitBase.IsVisible || !menu->AtkUnitBase.IsReady
                        || menu->PopupMenu.PopupMenu.EntryCount <= 0)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    menu->AtkUnitBase.FireCallbackInt(menu->PopupMenu.PopupMenu.EntryCount - 1);
                    request.Stage = WithdrawalStage.WaitForListClose;
                    request.NextActionTick = now + 500;
                    return;

                case WithdrawalStage.WaitForListClose:
                    var retainerList = GameGui.GetAddonByName<AddonRetainerList>("RetainerList");
                    if (retainerList == null || !retainerList->AtkUnitBase.IsVisible || !retainerList->AtkUnitBase.IsReady)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    retainerList->AtkUnitBase.FireCallbackInt(-1);
                    request.Stage = WithdrawalStage.WaitForClosed;
                    request.NextActionTick = now + 300;
                    return;

                case WithdrawalStage.WaitForClosed:
                    var remainingList = GameGui.GetAddonByName("RetainerList");
                    if (!remainingList.IsNull && remainingList.IsVisible)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    if (request.OrganizerMode)
                    {
                        this.withdrawal = null;
                        this.currentOrganizerSuggestion = null;
                        this.StartNextOrganizerDeposit();
                    }
                    else
                    {
                        this.withdrawal = null;
                        this.StartNextBatchWithdrawal();
                    }
                    return;
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Direct retainer withdrawal failed.");
            this.FailWithdrawal("取出失败，请查看 Dalamud 日志");
        }
    }

    private void FailWithdrawal(string message)
    {
        this.withdrawal = null;
        if (this.batchWithdrawalActive)
        {
            this.batchWithdrawalQueue.Clear();
            this.batchWithdrawalActive = false;
            message += "；批量队列已停止";
        }
        if (this.organizerActive)
        {
            this.organizerGroupQueue.Clear();
            this.organizerWithdrawalQueue.Clear();
            this.organizerDepositQueue.Clear();
            this.organizerActive = false;
            this.currentOrganizerSuggestion = null;
            this.currentOrganizerGroup = null;
            message += "；跨雇员整理已停止";
        }
        this.status = message;
    }

    private void StartOrganizer()
    {
        if (this.withdrawal != null || this.deposit != null || this.batchWithdrawalActive
            || this.batchDepositActive || this.organizerActive)
        {
            this.status = "已有一项雇员物品操作正在进行";
            return;
        }
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || this.FindNearbySummoningBell() == null)
        {
            this.status = "跨雇员整理需要登录对应角色并站在传唤铃交互范围内";
            return;
        }

        this.organizerGroupQueue.Clear();
        this.organizerWithdrawalQueue.Clear();
        this.organizerDepositQueue.Clear();
        var currentContentId = PlayerState.ContentId;
        var selected = this.selectedOrganizerSuggestions.Values
            .Where(entry => entry.Character.ContentId == currentContentId)
            .OrderBy(entry => entry.SourceStorage.OwnerId)
            .ThenBy(entry => entry.TargetStorage.OwnerId)
            .ThenBy(entry => entry.SourceItem.Container)
            .ThenBy(entry => entry.SourceItem.Slot)
            .ToList();
        this.organizerSkipped = this.selectedOrganizerSuggestions.Count - selected.Count;
        var valid = new List<OrganizerSuggestion>();
        foreach (var suggestion in selected)
        {
            if (suggestion.Quantity < 1 || suggestion.Quantity > suggestion.SourceItem.Quantity
                || suggestion.TargetItem.Quantity >= suggestion.StackSize
                || suggestion.Quantity > suggestion.StackSize - suggestion.TargetItem.Quantity)
            {
                this.organizerSkipped++;
                continue;
            }
            valid.Add(suggestion);
        }
        foreach (var group in valid.GroupBy(entry => (
                     entry.Character.ContentId,
                     SourceRetainerId: entry.SourceStorage.OwnerId,
                     TargetRetainerId: entry.TargetStorage.OwnerId)))
        {
            this.organizerGroupQueue.Enqueue(new OrganizerTransferGroup(group.ToList()));
        }
        if (this.organizerGroupQueue.Count == 0)
        {
            this.status = "没有可执行的当前角色跨雇员整理建议";
            return;
        }

        this.selectedOrganizerSuggestions.Clear();
        this.organizerActive = true;
        this.organizerCompleted = 0;
        this.StartNextOrganizerGroup();
    }

    private void StartNextOrganizerGroup()
    {
        if (!this.organizerActive)
            return;
        if (!this.organizerGroupQueue.TryDequeue(out var group))
        {
            this.organizerActive = false;
            this.currentOrganizerSuggestion = null;
            this.currentOrganizerGroup = null;
            this.status = $"跨雇员整理完成：成功 {this.organizerCompleted} 条，跳过 {this.organizerSkipped} 条";
            return;
        }

        this.currentOrganizerGroup = group;
        this.organizerWithdrawalQueue.Clear();
        this.organizerDepositQueue.Clear();
        foreach (var suggestion in group.Items)
            this.organizerWithdrawalQueue.Enqueue(suggestion);
        this.StartNextOrganizerWithdrawal();
    }

    private void StartNextOrganizerWithdrawal()
    {
        if (!this.organizerActive || !this.organizerWithdrawalQueue.TryDequeue(out var suggestion))
            return;
        this.currentOrganizerSuggestion = suggestion;
        var source = new SearchResult(suggestion.Character, suggestion.SourceStorage, suggestion.SourceItem);
        this.BeginWithdrawal(source, (int)suggestion.Quantity, true, true);
        if (this.withdrawal == null)
        {
            this.organizerGroupQueue.Clear();
            this.organizerWithdrawalQueue.Clear();
            this.organizerDepositQueue.Clear();
            this.organizerActive = false;
            this.currentOrganizerSuggestion = null;
            this.currentOrganizerGroup = null;
            this.status += "；跨雇员整理已停止";
        }
    }

    private void StartNextOrganizerDeposit()
    {
        if (!this.organizerActive || !this.organizerDepositQueue.TryDequeue(out var entry))
            return;
        var suggestion = entry.Suggestion;
        var received = new StoredItem
        {
            ItemId = suggestion.SourceItem.ItemId,
            Quantity = entry.ReceivedQuantity,
            IsHq = suggestion.SourceItem.IsHq,
            IconId = suggestion.SourceItem.IconId,
            Name = suggestion.SourceItem.Name,
            Container = (uint)entry.ReceivedContainer,
            Slot = entry.ReceivedSlot,
        };
        var result = new StackableResult(
            suggestion.Character,
            received,
            suggestion.TargetStorage,
            suggestion.TargetItem,
            suggestion.StackSize,
            suggestion.Quantity);
        this.BeginDeposit(result, (int)suggestion.Quantity, organizerMode: true);
    }

    private unsafe bool CanStartDeposit(
        StackableResult result,
        int quantity,
        out string reason,
        bool allowRetainerTransition = false)
    {
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || PlayerState.ContentId != result.Character.ContentId)
        {
            reason = "请登录这条记录所属的角色";
            return false;
        }
        if (this.withdrawal != null || this.deposit != null)
        {
            reason = "已有一项雇员物品操作正在进行";
            return false;
        }
        if (quantity < 1 || quantity > result.MaxDeposit)
        {
            reason = $"存入数量必须在 1 到 {result.MaxDeposit} 之间";
            return false;
        }
        if (this.FindNearbySummoningBell() == null)
        {
            reason = "需要站在传唤铃的可交互范围内";
            return false;
        }
        var retainers = RetainerManager.Instance();
        var active = retainers == null ? null : retainers->GetActiveRetainer();
        if (Condition[ConditionFlag.OccupiedSummoningBell]
            && active != null && active->RetainerId != 0 && active->RetainerId != result.TargetStorage.OwnerId
            && !allowRetainerTransition)
        {
            reason = $"请先结束当前雇员对话，再向 {result.TargetStorage.OwnerName} 存入";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void BeginDeposit(
        StackableResult result,
        int quantity,
        bool batchMode = false,
        uint? targetExpectedQuantity = null,
        uint? sourceExpectedQuantity = null,
        bool organizerMode = false)
    {
        var allowRetainerTransition = batchMode || organizerMode;
        if (!this.CanStartDeposit(result, quantity, out var reason, allowRetainerTransition))
        {
            this.status = reason;
            return;
        }

        var now = Environment.TickCount64;
        this.deposit = new DepositRequest
        {
            CharacterContentId = result.Character.ContentId,
            RetainerId = result.TargetStorage.OwnerId,
            RetainerName = result.TargetStorage.OwnerName,
            ItemId = result.SourceItem.ItemId,
            IsHq = result.SourceItem.IsHq,
            SourceExpectedQuantity = sourceExpectedQuantity ?? result.SourceItem.Quantity,
            RequestedQuantity = (uint)quantity,
            SourceContainer = (InventoryType)result.SourceItem.Container,
            SourceSlot = (ushort)result.SourceItem.Slot,
            TargetExpectedQuantity = targetExpectedQuantity ?? result.TargetItem.Quantity,
            TargetContainer = (InventoryType)result.TargetItem.Container,
            TargetSlot = (ushort)result.TargetItem.Slot,
            StackSize = result.StackSize,
            ItemName = result.SourceItem.Name,
            BatchMode = batchMode,
            OrganizerMode = organizerMode,
            Stage = allowRetainerTransition ? DepositStage.WaitForBellReady : DepositStage.OpenBell,
            DeadlineTick = now + 45_000,
            NextActionTick = now,
        };
        this.status = $"准备向雇员 {result.TargetStorage.OwnerName} 存入 {result.SourceItem.Name} ×{quantity}……";
    }

    private void StartBatchDeposit()
    {
        if (this.withdrawal != null || this.deposit != null || this.batchWithdrawalActive || this.batchDepositActive)
        {
            this.status = "已有一项雇员物品操作正在进行";
            return;
        }
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || this.FindNearbySummoningBell() == null)
        {
            this.status = "批量存入需要登录对应角色并站在传唤铃交互范围内";
            return;
        }

        this.batchDepositQueue.Clear();
        var currentContentId = PlayerState.ContentId;
        var selected = this.selectedDeposits.Values
            .Where(entry => entry.Result.Character.ContentId == currentContentId)
            .OrderBy(entry => entry.Result.TargetStorage.OwnerId)
            .ThenBy(entry => entry.Result.TargetItem.Container)
            .ThenBy(entry => entry.Result.TargetItem.Slot)
            .ThenBy(entry => entry.Result.SourceItem.Container)
            .ThenBy(entry => entry.Result.SourceItem.Slot)
            .ToList();
        this.batchDepositSkipped = this.selectedDeposits.Count - selected.Count;
        var expectedTargets = new Dictionary<string, uint>();
        var sourceUsage = new Dictionary<string, uint>();
        foreach (var entry in selected)
        {
            var result = entry.Result;
            var quantity = (uint)Math.Max(entry.Quantity, 0);
            var sourceKey = $"{result.SourceItem.Container}:{result.SourceItem.Slot}";
            sourceUsage.TryGetValue(sourceKey, out var alreadyUsed);
            var targetKey = $"{result.TargetStorage.OwnerId}:{result.TargetItem.Container}:{result.TargetItem.Slot}";
            if (!expectedTargets.TryGetValue(targetKey, out var expectedTarget))
                expectedTarget = result.TargetItem.Quantity;

            if (quantity < 1 || quantity > result.MaxDeposit
                || alreadyUsed + quantity > result.SourceItem.Quantity
                || expectedTarget >= result.StackSize || quantity > result.StackSize - expectedTarget)
            {
                this.batchDepositSkipped++;
                continue;
            }

            this.batchDepositQueue.Enqueue(new BatchDepositItem(
                result,
                entry.Quantity,
                result.SourceItem.Quantity - alreadyUsed,
                expectedTarget));
            sourceUsage[sourceKey] = alreadyUsed + quantity;
            expectedTargets[targetKey] = expectedTarget + quantity;
        }
        if (this.batchDepositQueue.Count == 0)
        {
            this.status = "没有数量有效且属于当前角色的已勾选存入记录";
            return;
        }

        this.selectedDeposits.Clear();
        this.batchDepositActive = true;
        this.batchDepositCompleted = 0;
        this.StartNextBatchDeposit();
    }

    private void StartNextBatchDeposit()
    {
        if (!this.batchDepositActive)
            return;
        if (!this.batchDepositQueue.TryDequeue(out var entry))
        {
            this.batchDepositActive = false;
            this.status = $"批量存入完成：成功 {this.batchDepositCompleted} 条，跳过 {this.batchDepositSkipped} 条";
            return;
        }

        this.BeginDeposit(
            entry.Result,
            entry.Quantity,
            true,
            entry.ExpectedTargetQuantity,
            entry.ExpectedSourceQuantity);
        if (this.deposit == null)
        {
            this.batchDepositQueue.Clear();
            this.batchDepositActive = false;
            this.status += "；批量队列已停止";
        }
    }

    private unsafe void ProcessDeposit(long now)
    {
        var request = this.deposit;
        if (request == null || now < request.NextActionTick)
            return;
        if (this.withdrawal != null)
            return;
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || PlayerState.ContentId != request.CharacterContentId)
        {
            this.FailDeposit("角色状态已变化，已取消存入");
            return;
        }
        if (now > request.DeadlineTick)
        {
            if (request.MoveCompleted)
            {
                if (request.BatchMode)
                    this.FailDeposit("道具已成功存入，但自动关闭雇员窗口超时，请手动关闭");
                else
                    this.CompleteDeposit("道具已成功存入，但自动关闭雇员窗口超时，请手动关闭");
            }
            else
                this.FailDeposit("存入超时，请保持在传唤铃附近后重试");
            return;
        }

        try
        {
            if (this.TryConfirmOperationDialog())
            {
                request.NextActionTick = now + 400;
                return;
            }
            var retainerManager = RetainerManager.Instance();
            var activeRetainer = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            switch (request.Stage)
            {
                case DepositStage.WaitForBellReady:
                    if (Condition[ConditionFlag.OccupiedSummoningBell])
                    {
                        if (activeRetainer != null && activeRetainer->RetainerId == request.RetainerId)
                        {
                            request.Stage = DepositStage.WaitForRetainer;
                            request.NextActionTick = now + 200;
                            return;
                        }
                        request.NextActionTick = now + 200;
                        this.status = "正在等待上一位雇员对话完全结束……";
                        return;
                    }
                    request.Stage = DepositStage.OpenBell;
                    request.NextActionTick = now + 200;
                    return;

                case DepositStage.OpenBell:
                    if (Condition[ConditionFlag.OccupiedSummoningBell]
                        && activeRetainer != null && activeRetainer->RetainerId == request.RetainerId)
                    {
                        request.Stage = DepositStage.WaitForRetainer;
                        request.NextActionTick = now + 300;
                        return;
                    }
                    var bell = this.FindNearbySummoningBell();
                    var targetSystem = TargetSystem.Instance();
                    if (bell == null || targetSystem == null)
                    {
                        this.FailDeposit("已离开传唤铃的可交互范围");
                        return;
                    }
                    targetSystem->InteractWithObject((NativeGameObject*)bell.Address, false);
                    request.Stage = DepositStage.WaitForList;
                    request.NextActionTick = now + 500;
                    this.status = "正在打开雇员列表……";
                    return;

                case DepositStage.WaitForList:
                    var listAddon = GameGui.GetAddonByName<AddonRetainerList>("RetainerList");
                    if (listAddon == null || !listAddon->AtkUnitBase.IsVisible || !listAddon->AtkUnitBase.IsReady
                        || retainerManager == null)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    var sortedIndex = this.FindSortedRetainerIndex(retainerManager, request.RetainerId);
                    if (sortedIndex < 0)
                    {
                        this.FailDeposit($"雇员列表中找不到 {request.RetainerName}");
                        return;
                    }
                    SelectRetainer(listAddon, sortedIndex);
                    request.Stage = DepositStage.WaitForRetainer;
                    request.NextActionTick = now + 500;
                    this.status = $"正在呼叫雇员 {request.RetainerName}……";
                    return;

                case DepositStage.WaitForRetainer:
                    if (!Condition[ConditionFlag.OccupiedSummoningBell]
                        || activeRetainer == null || activeRetainer->RetainerId != request.RetainerId)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    if (this.IsRetainerInventoryVisible())
                    {
                        request.Stage = DepositStage.RequestDeposit;
                        request.NextActionTick = now + 500;
                        return;
                    }
                    var selectString = GameGui.GetAddonByName<AtkUnitBase>("SelectString");
                    if (selectString == null || !selectString->IsVisible)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    selectString->FireCallbackInt(0);
                    request.Stage = DepositStage.WaitForInventory;
                    request.NextActionTick = now + 500;
                    this.status = $"正在打开 {request.RetainerName} 的背包……";
                    return;

                case DepositStage.WaitForInventory:
                    if (!Condition[ConditionFlag.OccupiedSummoningBell]
                        || activeRetainer == null || activeRetainer->RetainerId != request.RetainerId)
                    {
                        this.FailDeposit("当前雇员已经变化，已取消存入");
                        return;
                    }
                    if (!this.IsRetainerInventoryVisible())
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    request.Stage = DepositStage.RequestDeposit;
                    request.NextActionTick = now + 500;
                    return;

                case DepositStage.RequestDeposit:
                    if (!Condition[ConditionFlag.OccupiedSummoningBell]
                        || activeRetainer == null || activeRetainer->RetainerId != request.RetainerId
                        || !this.IsRetainerInventoryVisible())
                    {
                        this.FailDeposit("原生雇员背包界面未就绪，已取消存入");
                        return;
                    }
                    var manager = InventoryManager.Instance();
                    var source = manager == null ? null : manager->GetInventoryContainer(request.SourceContainer);
                    var target = manager == null ? null : manager->GetInventoryContainer(request.TargetContainer);
                    if (source == null || target == null || !source->IsLoaded || !target->IsLoaded
                        || request.SourceSlot >= source->Size || request.TargetSlot >= target->Size)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    var sourceItem = source->GetInventorySlot(request.SourceSlot);
                    var targetItem = target->GetInventorySlot(request.TargetSlot);
                    if (!MatchesItem(sourceItem, request.ItemId, request.IsHq, request.SourceExpectedQuantity)
                        || !MatchesItem(targetItem, request.ItemId, request.IsHq, request.TargetExpectedQuantity))
                    {
                        this.FailDeposit("来源或目标格已经变化，为避免存错已取消");
                        return;
                    }
                    if (targetItem->GetQuantity() >= request.StackSize)
                    {
                        this.FailDeposit("目标格已经达到单格堆叠上限，已取消存入");
                        return;
                    }
                    var remainingCapacity = request.StackSize - targetItem->GetQuantity();
                    if (request.RequestedQuantity > remainingCapacity)
                    {
                        this.FailDeposit($"目标格当前只能再堆叠 {remainingCapacity} 个，已取消存入");
                        return;
                    }
                    var agentModule = AgentModule.Instance();
                    var retainerAgent = agentModule == null
                        ? null
                        : (AgentRetainer*)agentModule->GetAgentByInternalId(AgentId.Retainer);
                    if (retainerAgent == null || !retainerAgent->IsAgentActive())
                    {
                        this.FailDeposit("原生雇员物品代理未就绪，已取消存入");
                        return;
                    }
                    var partial = request.RequestedQuantity < sourceItem->GetQuantity();
                    retainerAgent->HandleCallback(
                        request.SourceSlot,
                        request.SourceContainer,
                        (InventoryContextFlag)0,
                        partial ? 4UL : 1UL);
                    request.Stage = partial ? DepositStage.WaitForQuantity : DepositStage.WaitForMove;
                    request.NextActionTick = now + 300;
                    this.status = $"正在存入 {request.ItemName} ×{request.RequestedQuantity}……";
                    return;

                case DepositStage.WaitForQuantity:
                    var inputNumeric = GameGui.GetAddonByName<AddonInputNumeric>("InputNumeric");
                    if (inputNumeric == null || !inputNumeric->AtkUnitBase.IsVisible
                        || !inputNumeric->AtkUnitBase.IsReady || inputNumeric->AtkUnitBase.AtkValuesCount <= 3)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    var nativeMaximum = inputNumeric->AtkUnitBase.AtkValues[3].UInt;
                    var nativeMinimum = inputNumeric->AtkUnitBase.AtkValues[2].UInt;
                    if (request.RequestedQuantity < nativeMinimum || request.RequestedQuantity > nativeMaximum)
                    {
                        this.FailDeposit($"游戏当前允许的数量是 {nativeMinimum}～{nativeMaximum}，已取消");
                        return;
                    }
                    inputNumeric->AtkUnitBase.FireCallbackInt((int)request.RequestedQuantity);
                    request.Stage = DepositStage.WaitForMove;
                    request.NextActionTick = now + 300;
                    return;

                case DepositStage.WaitForMove:
                    var inventoryManager = InventoryManager.Instance();
                    var currentSource = inventoryManager == null ? null : inventoryManager->GetInventoryContainer(request.SourceContainer);
                    var currentTarget = inventoryManager == null ? null : inventoryManager->GetInventoryContainer(request.TargetContainer);
                    var sourceNow = currentSource == null || !currentSource->IsLoaded ? null : currentSource->GetInventorySlot(request.SourceSlot);
                    var targetNow = currentTarget == null || !currentTarget->IsLoaded ? null : currentTarget->GetInventorySlot(request.TargetSlot);
                    var sourceQuantity = GetMatchingQuantity(sourceNow, request.ItemId, request.IsHq);
                    var targetQuantity = GetMatchingQuantity(targetNow, request.ItemId, request.IsHq);
                    if (sourceQuantity == request.SourceExpectedQuantity && targetQuantity == request.TargetExpectedQuantity)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    if (sourceQuantity != request.SourceExpectedQuantity - request.RequestedQuantity
                        || targetQuantity != request.TargetExpectedQuantity + request.RequestedQuantity)
                    {
                        this.FailDeposit("存入后的格子数量不符合预期，请手动检查背包");
                        return;
                    }
                    request.MoveCompleted = true;
                    if (request.BatchMode)
                    {
                        this.batchDepositCompleted++;
                        if (this.batchDepositQueue.TryPeek(out var next)
                            && next.Result.TargetStorage.OwnerId == request.RetainerId)
                        {
                            this.deposit = null;
                            this.StartNextBatchDeposit();
                            return;
                        }
                    }
                    if (request.OrganizerMode && this.organizerDepositQueue.Count > 0)
                    {
                        this.deposit = null;
                        this.StartNextOrganizerDeposit();
                        return;
                    }
                    request.Stage = DepositStage.CloseInventory;
                    request.NextActionTick = now + 500;
                    this.status = $"已存入 {request.ItemName} ×{request.RequestedQuantity}，正在关闭雇员窗口……";
                    return;

                case DepositStage.CloseInventory:
                    var closeAgentModule = AgentModule.Instance();
                    var closeAgent = closeAgentModule == null
                        ? null
                        : closeAgentModule->GetAgentByInternalId(AgentId.Retainer);
                    if (closeAgent != null && closeAgent->IsAgentActive() && this.IsRetainerInventoryVisible())
                    {
                        closeAgent->Hide();
                        request.Stage = DepositStage.WaitForRetainerMenu;
                        request.NextActionTick = now + 500;
                        return;
                    }
                    request.Stage = DepositStage.WaitForRetainerMenu;
                    request.NextActionTick = now + 200;
                    return;

                case DepositStage.WaitForRetainerMenu:
                    var menu = GameGui.GetAddonByName<AddonSelectString>("SelectString");
                    if (menu == null || !menu->AtkUnitBase.IsVisible || !menu->AtkUnitBase.IsReady)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    var entryCount = menu->PopupMenu.PopupMenu.EntryCount;
                    if (entryCount <= 0)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    // 雇员指令菜单最后一项是原生“结束对话”。
                    menu->AtkUnitBase.FireCallbackInt(entryCount - 1);
                    request.Stage = DepositStage.WaitForListClose;
                    request.NextActionTick = now + 500;
                    return;

                case DepositStage.WaitForListClose:
                    var retainerList = GameGui.GetAddonByName<AddonRetainerList>("RetainerList");
                    if (retainerList == null || !retainerList->AtkUnitBase.IsVisible || !retainerList->AtkUnitBase.IsReady)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    retainerList->AtkUnitBase.FireCallbackInt(-1);
                    request.Stage = DepositStage.WaitForClosed;
                    request.NextActionTick = now + 300;
                    return;

                case DepositStage.WaitForClosed:
                    var remainingList = GameGui.GetAddonByName("RetainerList");
                    if (!remainingList.IsNull && remainingList.IsVisible)
                    {
                        request.NextActionTick = now + 200;
                        return;
                    }
                    this.CompleteDeposit($"已向 {request.RetainerName} 存入 {request.ItemName} ×{request.RequestedQuantity}，并关闭雇员窗口");
                    return;
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Direct retainer deposit failed.");
            this.FailDeposit("存入失败，请查看 Dalamud 日志");
        }
    }

    private void FailDeposit(string message)
    {
        this.deposit = null;
        if (this.batchDepositActive)
        {
            this.batchDepositQueue.Clear();
            this.batchDepositActive = false;
            message += "；批量队列已停止";
        }
        if (this.organizerActive)
        {
            this.organizerGroupQueue.Clear();
            this.organizerWithdrawalQueue.Clear();
            this.organizerDepositQueue.Clear();
            this.organizerActive = false;
            this.currentOrganizerSuggestion = null;
            this.currentOrganizerGroup = null;
            message += "；跨雇员整理已停止";
        }
        this.status = message;
    }

    private void CompleteDeposit(string message)
    {
        var organizerMode = this.deposit?.OrganizerMode == true;
        this.deposit = null;
        this.organizerHint = null;
        this.status = message;
        this.ScanNow(true);
        if (this.batchDepositActive)
            this.StartNextBatchDeposit();
        else if (organizerMode && this.organizerActive)
        {
            this.organizerCompleted += this.currentOrganizerGroup?.Items.Count ?? 0;
            this.currentOrganizerSuggestion = null;
            this.currentOrganizerGroup = null;
            this.StartNextOrganizerGroup();
        }
    }

    private unsafe int FindSortedRetainerIndex(RetainerManager* manager, ulong retainerId)
    {
        for (var index = 0; index < 10; index++)
        {
            var retainer = manager->GetRetainerBySortedIndex((uint)index);
            if (retainer != null && retainer->RetainerId == retainerId)
                return index;
        }
        return -1;
    }

    private static unsafe void SelectRetainer(AddonRetainerList* addon, int sortedIndex)
    {
        var callbackValues = stackalloc AtkValue[4]
        {
            new() { Type = AtkValueType.Int, Int = 2 },
            new() { Type = AtkValueType.UInt, UInt = (uint)sortedIndex },
            new() { Type = 0, Int = 0 },
            new() { Type = 0, Int = 0 },
        };
        addon->AtkUnitBase.FireCallback(4, callbackValues, true);
    }

    private static unsafe bool MatchesItem(InventoryItem* item, uint itemId, bool isHq, uint quantity)
        => item != null && item->ItemId != 0 && item->GetBaseItemId() == itemId
            && item->IsHighQuality() == isHq && item->GetQuantity() == quantity;

    private static unsafe uint GetMatchingQuantity(InventoryItem* item, uint itemId, bool isHq)
        => item != null && item->ItemId != 0 && item->GetBaseItemId() == itemId && item->IsHighQuality() == isHq
            ? item->GetQuantity()
            : 0;

    private bool IsRetainerInventoryVisible()
    {
        var normal = GameGui.GetAddonByName("InventoryRetainer");
        if (!normal.IsNull && normal.IsVisible)
            return true;

        var large = GameGui.GetAddonByName("InventoryRetainerLarge");
        return !large.IsNull && large.IsVisible;
    }

    private IGameObject? FindNearbySummoningBell()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        return ObjectTable
            .Where(gameObject => gameObject.IsTargetable
                && (gameObject.Name.ToString().Contains("传唤铃", StringComparison.OrdinalIgnoreCase)
                    || gameObject.Name.ToString().Contains("Summoning Bell", StringComparison.OrdinalIgnoreCase)))
            .Where(gameObject => Vector3.Distance(player.Position, gameObject.Position)
                <= (gameObject.ObjectKind.ToString() == "HousingEventObject" ? 6.5f : 4.6f))
            .OrderBy(gameObject => Vector3.DistanceSquared(player.Position, gameObject.Position))
            .FirstOrDefault();
    }

    private unsafe bool TryFindEmptyInventorySlot(out InventoryType containerType, out ushort slotIndex)
    {
        var manager = InventoryManager.Instance();
        if (manager != null)
        {
            foreach (var page in InventoryPages)
            {
                var container = manager->GetInventoryContainer(page);
                if (container == null || !container->IsLoaded)
                    continue;
                for (ushort slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item != null && item->ItemId == 0)
                    {
                        containerType = page;
                        slotIndex = slot;
                        return true;
                    }
                }
            }
        }

        containerType = default;
        slotIndex = 0;
        return false;
    }

    private unsafe void ScanNow(bool force)
    {
        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || PlayerState.ContentId == 0)
            return;

        try
        {
            var character = this.GetCurrentCharacter();
            var changed = this.lastContentId != character.ContentId;
            this.lastContentId = character.ContentId;
            character.LastSeenUtc = DateTime.UtcNow;

            var manager = InventoryManager.Instance();
            if (manager != null)
            {
                changed |= this.ScanContainers(character, "inventory", StorageKind.Inventory, "角色背包", 0, InventoryPages, true);
                changed |= this.ScanContainers(character, "saddlebag", StorageKind.Saddlebag, "陆行鸟鞍囊", 0, SaddlebagPages);
                changed |= this.ScanContainers(character, "premium-saddlebag", StorageKind.PremiumSaddlebag, "高级陆行鸟鞍囊", 0, PremiumSaddlebagPages);

                var retainers = RetainerManager.Instance();
                var active = retainers == null ? null : retainers->GetActiveRetainer();
                if (Condition[ConditionFlag.OccupiedSummoningBell]
                    && this.IsRetainerInventoryVisible()
                    && active != null && active->RetainerId != 0)
                {
                    var retainerName = active->NameString;
                    changed |= this.ScanContainers(
                        character,
                        $"retainer:{active->RetainerId}",
                        StorageKind.Retainer,
                        string.IsNullOrWhiteSpace(retainerName) ? "未命名雇员" : retainerName,
                        active->RetainerId,
                        RetainerPages,
                        true);
                }
            }

            changed |= this.ScanGlamourDresser(character);
            changed |= this.ScanArmoire(character);
            if (changed || force)
            {
                this.configuration.Save(PluginInterface);
                this.status = $"本地索引已更新：{DateTime.Now:HH:mm:ss}";
            }
        }
        catch (Exception exception)
        {
            this.status = "刷新失败，请查看 Dalamud 日志";
            Log.Error(exception, "刷新跨角色背包索引失败。");
        }
    }

    private CharacterSnapshot GetCurrentCharacter()
    {
        var contentId = PlayerState.ContentId;
        var name = PlayerState.CharacterName;
        if (string.IsNullOrWhiteSpace(name))
            name = $"角色 {contentId:X}";
        var world = PlayerState.HomeWorld.Value.Name.ToString();
        var character = this.configuration.Characters.FirstOrDefault(entry => entry.ContentId == contentId);
        if (character == null)
        {
            character = new CharacterSnapshot { ContentId = contentId };
            this.configuration.Characters.Add(character);
        }

        character.Name = name;
        character.World = world;
        return character;
    }

    private unsafe bool ScanContainers(
        CharacterSnapshot character,
        string key,
        StorageKind kind,
        string ownerName,
        ulong ownerId,
        IReadOnlyList<InventoryType> pages,
        bool requireAllPages = false)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        var containers = new List<nint>();
        foreach (var page in pages)
        {
            var container = manager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded || container->Size <= 0)
                continue;
            containers.Add((nint)container);
        }
        if (containers.Count == 0 || requireAllPages && containers.Count != pages.Count)
            return false;

        var items = new List<StoredItem>();
        foreach (var address in containers)
        {
            var container = (InventoryContainer*)address;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item == null || item->ItemId == 0)
                    continue;
                var baseId = item->GetBaseItemId();
                if (!this.TryDescribeItem(baseId, out var name, out var icon))
                    continue;
                items.Add(new StoredItem
                {
                    ItemId = baseId,
                    Quantity = item->GetQuantity(),
                    IsHq = item->IsHighQuality(),
                    IconId = icon,
                    Name = name,
                    Container = (uint)container->Type,
                    Slot = slot,
                });
            }
        }

        var loadedPages = containers
            .Select(address => (uint)((InventoryContainer*)address)->Type)
            .ToHashSet();
        return this.MergeStoragePages(character, key, kind, ownerName, ownerId, loadedPages, items);
    }

    private bool MergeStoragePages(
        CharacterSnapshot character,
        string key,
        StorageKind kind,
        string ownerName,
        ulong ownerId,
        HashSet<uint> loadedPages,
        List<StoredItem> loadedItems)
    {
        var storage = character.Storages.FirstOrDefault(entry => entry.Key == key);
        var mergedItems = storage?.Items
            .Where(item => !loadedPages.Contains(item.Container))
            .Concat(loadedItems)
            .OrderBy(item => item.Container)
            .ThenBy(item => item.Slot)
            .ToList() ?? loadedItems
            .OrderBy(item => item.Container)
            .ThenBy(item => item.Slot)
            .ToList();
        return this.ReplaceStorage(character, key, kind, ownerName, ownerId, mergedItems);
    }

    private unsafe bool ScanGlamourDresser(CharacterSnapshot character)
    {
        var manager = MirageManager.Instance();
        if (manager == null || !manager->PrismBoxLoaded)
            return false;

        var items = new List<StoredItem>();
        for (var slot = 0; slot < manager->PrismBoxItemIds.Length; slot++)
        {
            var itemId = manager->PrismBoxItemIds[slot];
            if (itemId == 0 || !this.TryDescribeItem(itemId, out var name, out var icon))
                continue;
            items.Add(new StoredItem
            {
                ItemId = itemId, Quantity = 1, IconId = icon, Name = name, Slot = slot,
            });
        }
        return this.ReplaceStorage(character, "glamour-dresser", StorageKind.GlamourDresser, "幻化台", 0, items);
    }

    private unsafe bool ScanArmoire(CharacterSnapshot character)
    {
        var uiState = UIState.Instance();
        if (uiState == null || !uiState->Cabinet.IsCabinetLoaded())
            return false;

        var items = new List<StoredItem>();
        foreach (var row in DataManager.GetExcelSheet<LuminaCabinet>())
        {
            if (row.RowId == 0 || !uiState->Cabinet.IsItemInCabinet(row.RowId))
                continue;
            var itemId = row.Item.RowId;
            if (itemId == 0 || !this.TryDescribeItem(itemId, out var name, out var icon))
                continue;
            items.Add(new StoredItem
            {
                ItemId = itemId, Quantity = 1, IconId = icon, Name = name, Slot = (int)row.RowId,
            });
        }
        return this.ReplaceStorage(character, "armoire", StorageKind.Armoire, "收藏柜", 0, items);
    }

    private bool TryDescribeItem(uint itemId, out string name, out uint icon)
    {
        name = string.Empty;
        icon = 0;
        if (!DataManager.GetExcelSheet<LuminaItem>().TryGetRow(itemId, out var row))
            return false;
        name = row.Name.ToString();
        icon = row.Icon;
        return !string.IsNullOrWhiteSpace(name);
    }

    private bool ReplaceStorage(
        CharacterSnapshot character,
        string key,
        StorageKind kind,
        string ownerName,
        ulong ownerId,
        List<StoredItem> items)
    {
        var storage = character.Storages.FirstOrDefault(entry => entry.Key == key);
        var signature = string.Join(';', items.Select(item => $"{item.Container}:{item.Slot}:{item.ItemId}:{item.Quantity}:{item.IsHq}"));
        var oldSignature = storage == null
            ? string.Empty
            : string.Join(';', storage.Items.Select(item => $"{item.Container}:{item.Slot}:{item.ItemId}:{item.Quantity}:{item.IsHq}"));
        if (storage != null && signature == oldSignature && storage.OwnerName == ownerName)
            return false;

        storage ??= new StorageSnapshot { Key = key };
        if (!character.Storages.Contains(storage))
            character.Storages.Add(storage);
        storage.Kind = kind;
        storage.OwnerId = ownerId;
        storage.OwnerName = ownerName;
        storage.UpdatedUtc = DateTime.UtcNow;
        storage.Items = items;
        return true;
    }

    private void DrawWindow()
    {
        OldStableNotice.Draw("InventorySearch", ref this.migrationNoticeRequested);
        if (!this.windowOpen)
            return;
        ImGui.SetNextWindowSize(new Vector2(860, 620), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("增强背包搜索###InventorySearch", ref this.windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(380);
        ImGui.InputTextWithHint("###search", "输入道具名称或物品 ID", ref this.search, 128);
        ImGui.SameLine();
        if (ImGui.Button("立即刷新"))
            this.ScanNow(true);
        ImGui.SameLine();
        if (this.withdrawal != null || this.deposit != null)
        {
            if (ImGui.Button("取消操作"))
            {
                if (this.withdrawal != null)
                    this.FailWithdrawal("已取消雇员物品操作");
                if (this.deposit != null)
                    this.FailDeposit("已取消雇员物品操作");
            }
            ImGui.SameLine();
        }
        ImGui.TextDisabled(this.status);

        this.DrawFilters();
        ImGui.Separator();
        this.DrawResults();
        ImGui.End();
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("角色", this.selectedCharacter))
        {
            if (ImGui.Selectable("全部角色", this.selectedCharacter == "全部角色"))
                this.selectedCharacter = "全部角色";
            foreach (var character in this.configuration.Characters.OrderBy(character => character.Name))
            {
                var label = CharacterLabel(character);
                if (ImGui.Selectable(label, this.selectedCharacter == label))
                    this.selectedCharacter = label;
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        var kindNames = new[] { "全部分类", "角色背包", "鸟包", "高级鸟包", "雇员", "幻化台", "收藏柜", "可堆叠物品" };
        ImGui.SetNextItemWidth(170);
        if (ImGui.BeginCombo("分类", kindNames[this.selectedKind]))
        {
            for (var index = 0; index < kindNames.Length; index++)
                if (ImGui.Selectable(kindNames[index], this.selectedKind == index))
                {
                    this.selectedKind = index;
                    this.showOrganizer = false;
                }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("跨雇员堆叠整理"))
            this.showOrganizer = true;
    }

    private void DrawStackableResults()
    {
        var query = this.search.Trim();
        var results = this.GetStackableResults()
            .Where(result => string.IsNullOrEmpty(query)
                || result.SourceItem.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || result.SourceItem.ItemId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(result => this.organizerHint != null
                && result.SourceItem.ItemId == this.organizerHint.ItemId
                && result.SourceItem.IsHq == this.organizerHint.IsHq
                && result.TargetStorage.OwnerId == this.organizerHint.TargetRetainerId)
            .ThenBy(result => result.SourceItem.Name)
            .ThenBy(result => result.TargetStorage.OwnerName)
            .Take(1000)
            .ToList();

        ImGui.TextUnformatted($"已勾选 {this.selectedDeposits.Count} 格 · 按各条目数量存入");
        ImGui.SameLine();
        var canStartBatchDeposit = this.selectedDeposits.Count > 0
            && this.withdrawal == null && this.deposit == null
            && !this.batchWithdrawalActive && !this.batchDepositActive;
        ImGui.BeginDisabled(!canStartBatchDeposit);
        if (ImGui.Button("批量存入"))
            this.StartBatchDeposit();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(this.selectedDeposits.Count == 0 || this.batchDepositActive);
        if (ImGui.Button("清除存入勾选"))
            this.selectedDeposits.Clear();
        ImGui.EndDisabled();

        ImGui.TextDisabled($"找到 {results.Count} 条可并入雇员现有堆叠的背包记录");
        if (!ImGui.BeginChild("stackable-results", Vector2.Zero, true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var result in results)
        {
            var key = $"deposit:{result.Character.ContentId}:{result.SourceItem.Container}:{result.SourceItem.Slot}:{result.TargetStorage.OwnerId}:{result.TargetItem.Container}:{result.TargetItem.Slot}";
            ImGui.PushID(key);
            if (!this.depositQuantities.TryGetValue(key, out var quantity))
                quantity = (int)result.MaxDeposit;
            quantity = Math.Clamp(quantity, 1, (int)result.MaxDeposit);

            var selected = this.selectedDeposits.ContainsKey(key);
            if (ImGui.Checkbox("##batchDepositSelect", ref selected))
            {
                if (selected)
                    this.selectedDeposits[key] = new BatchDepositItem(
                        result, quantity, result.SourceItem.Quantity, result.TargetItem.Quantity);
                else
                    this.selectedDeposits.Remove(key);
            }
            ImGui.SameLine();
            var preferred = this.organizerHint != null
                && result.SourceItem.ItemId == this.organizerHint.ItemId
                && result.SourceItem.IsHq == this.organizerHint.IsHq
                && result.TargetStorage.OwnerId == this.organizerHint.TargetRetainerId;
            var texture = this.GetIcon(result.SourceItem.IconId);
            if (texture.TryGetWrap(out var wrap, out _))
            {
                ImGui.Image(wrap.Handle, new Vector2(34, 34));
                ImGui.SameLine();
            }
            ImGui.BeginGroup();
            ImGui.TextUnformatted($"{result.SourceItem.Name}{(result.SourceItem.IsHq ? " HQ" : string.Empty)}  背包 ×{result.SourceItem.Quantity}");
            ImGui.TextDisabled($"{(preferred ? "[整理目标]  " : string.Empty)}存入 {result.TargetStorage.OwnerName} · 当前 {result.TargetItem.Quantity}/{result.StackSize} · 可存 {result.MaxDeposit}");
            ImGui.EndGroup();

            ImGui.SameLine();
            ImGui.BeginDisabled(quantity <= 1);
            if (ImGui.SmallButton("-")) quantity--;
            ImGui.EndDisabled();
            ImGui.SameLine(0, 3);
            ImGui.SetNextItemWidth(68);
            if (ImGui.InputInt("##depositQuantity", ref quantity, 0, 0))
                quantity = Math.Clamp(quantity, 1, (int)result.MaxDeposit);
            ImGui.SameLine(0, 3);
            ImGui.BeginDisabled(quantity >= result.MaxDeposit);
            if (ImGui.SmallButton("+")) quantity++;
            ImGui.EndDisabled();
            this.depositQuantities[key] = quantity;
            if (this.selectedDeposits.ContainsKey(key))
                this.selectedDeposits[key] = new BatchDepositItem(
                    result, quantity, result.SourceItem.Quantity, result.TargetItem.Quantity);

            var canDeposit = this.CanStartDeposit(result, quantity, out var reason)
                && !this.batchWithdrawalActive && !this.batchDepositActive;
            ImGui.SameLine();
            ImGui.BeginDisabled(!canDeposit);
            if (ImGui.Button("存入"))
                this.BeginDeposit(result, quantity);
            ImGui.EndDisabled();
            if (!canDeposit && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(reason);
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private void DrawOrganizer()
    {
        var query = this.search.Trim();
        var suggestions = this.GetOrganizerSuggestions()
            .Where(result => string.IsNullOrEmpty(query)
                || result.SourceItem.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || result.SourceItem.ItemId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(result => result.SourceItem.Name)
            .Take(1000)
            .ToList();

        ImGui.TextUnformatted($"已勾选 {this.selectedOrganizerSuggestions.Count} 条 · 使用建议数量自动合并");
        ImGui.SameLine();
        var canStartOrganizer = this.selectedOrganizerSuggestions.Count > 0
            && this.withdrawal == null && this.deposit == null
            && !this.batchWithdrawalActive && !this.batchDepositActive && !this.organizerActive;
        ImGui.BeginDisabled(!canStartOrganizer);
        if (ImGui.Button("自动合并勾选项"))
            this.StartOrganizer();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(this.selectedOrganizerSuggestions.Count == 0 || this.organizerActive);
        if (ImGui.Button("清除整理勾选"))
            this.selectedOrganizerSuggestions.Clear();
        ImGui.EndDisabled();

        ImGui.TextDisabled($"找到 {suggestions.Count} 条跨雇员合并建议；勾选后自动完成来源取出与目标存入");
        if (!ImGui.BeginChild("organizer-results", Vector2.Zero, true))
        {
            ImGui.EndChild();
            return;
        }
        foreach (var suggestion in suggestions)
        {
            var suggestionKey = OrganizerKey(suggestion);
            ImGui.PushID(suggestionKey);
            var selected = this.selectedOrganizerSuggestions.ContainsKey(suggestionKey);
            if (ImGui.Checkbox("##organizerSelect", ref selected))
            {
                if (selected)
                    this.selectedOrganizerSuggestions[suggestionKey] = suggestion;
                else
                    this.selectedOrganizerSuggestions.Remove(suggestionKey);
            }
            ImGui.SameLine();
            var texture = this.GetIcon(suggestion.SourceItem.IconId);
            if (texture.TryGetWrap(out var wrap, out _))
            {
                ImGui.Image(wrap.Handle, new Vector2(34, 34));
                ImGui.SameLine();
            }
            ImGui.BeginGroup();
            ImGui.TextUnformatted($"{suggestion.SourceItem.Name}{(suggestion.SourceItem.IsHq ? " HQ" : string.Empty)}  ×{suggestion.Quantity}");
            ImGui.TextDisabled($"{suggestion.SourceStorage.OwnerName} {Location(StorageKind.Retainer, suggestion.SourceItem)}  →  {suggestion.TargetStorage.OwnerName} {suggestion.TargetItem.Quantity}/{suggestion.StackSize}");
            ImGui.EndGroup();
            var sourceResult = new SearchResult(suggestion.Character, suggestion.SourceStorage, suggestion.SourceItem);
            var canStart = this.CanStartWithdrawal(sourceResult, (int)suggestion.Quantity, out var reason)
                && !this.organizerActive && !this.batchWithdrawalActive && !this.batchDepositActive;
            ImGui.SameLine();
            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("自动合并"))
            {
                this.selectedOrganizerSuggestions.Clear();
                this.selectedOrganizerSuggestions[suggestionKey] = suggestion;
                this.StartOrganizer();
            }
            ImGui.EndDisabled();
            if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(reason);
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private List<StackableResult> GetStackableResults()
    {
        var output = new List<StackableResult>();
        foreach (var character in this.configuration.Characters.Where(character
                     => this.selectedCharacter == "全部角色" || CharacterLabel(character) == this.selectedCharacter))
        {
            var inventoryItems = character.Storages
                .Where(storage => storage.Kind == StorageKind.Inventory)
                .SelectMany(storage => storage.Items);
            var retainerEntries = character.Storages
                .Where(storage => storage.Kind == StorageKind.Retainer)
                .SelectMany(storage => storage.Items.Select(item => (Storage: storage, Item: item)))
                .ToList();
            foreach (var sourceItem in inventoryItems)
            {
                var stackSize = this.GetStackSize(sourceItem.ItemId);
                if (stackSize <= 1)
                    continue;
                var targets = retainerEntries.Where(entry => entry.Item.ItemId == sourceItem.ItemId
                        && entry.Item.IsHq == sourceItem.IsHq && entry.Item.Quantity < stackSize)
                    .GroupBy(entry => entry.Storage.OwnerId)
                    .Select(group => group.OrderBy(entry => entry.Item.Container).ThenBy(entry => entry.Item.Slot).First());
                foreach (var target in targets)
                {
                    var maximum = Math.Min(sourceItem.Quantity, stackSize - target.Item.Quantity);
                    if (maximum > 0)
                        output.Add(new StackableResult(character, sourceItem, target.Storage, target.Item, stackSize, maximum));
                }
            }
        }
        return output;
    }

    private List<OrganizerSuggestion> GetOrganizerSuggestions()
    {
        var output = new List<OrganizerSuggestion>();
        foreach (var character in this.configuration.Characters.Where(character
                     => this.selectedCharacter == "全部角色" || CharacterLabel(character) == this.selectedCharacter))
        {
            var entries = character.Storages
                .Where(storage => storage.Kind == StorageKind.Retainer)
                .SelectMany(storage => storage.Items.Select(item => (Storage: storage, Item: item)))
                .GroupBy(entry => (entry.Item.ItemId, entry.Item.IsHq));
            foreach (var group in entries)
            {
                var stackSize = this.GetStackSize(group.Key.ItemId);
                if (stackSize <= 1 || group.Select(entry => entry.Storage.OwnerId).Distinct().Count() < 2)
                    continue;
                var target = group.Where(entry => entry.Item.Quantity < stackSize)
                    .GroupBy(entry => entry.Storage.OwnerId)
                    .Select(ownerGroup => ownerGroup.OrderBy(entry => entry.Item.Container).ThenBy(entry => entry.Item.Slot).First())
                    .OrderByDescending(entry => entry.Item.Quantity)
                    .FirstOrDefault();
                if (target.Storage == null)
                    continue;
                var source = group.Where(entry => entry.Storage.OwnerId != target.Storage.OwnerId)
                    .OrderBy(entry => entry.Item.Quantity)
                    .FirstOrDefault();
                if (source.Storage == null)
                    continue;
                var quantity = Math.Min(source.Item.Quantity, stackSize - target.Item.Quantity);
                if (quantity > 0)
                    output.Add(new OrganizerSuggestion(character, source.Storage, source.Item, target.Storage, target.Item, stackSize, quantity));
            }
        }
        return output;
    }

    private uint GetStackSize(uint itemId)
        => DataManager.GetExcelSheet<LuminaItem>().TryGetRow(itemId, out var row) ? Math.Max(1u, row.StackSize) : 1;

    private void DrawResults()
    {
        if (this.showOrganizer)
        {
            this.DrawOrganizer();
            return;
        }
        if (this.selectedKind == 7)
        {
            this.DrawStackableResults();
            return;
        }

        var query = this.search.Trim();
        var results = this.configuration.Characters
            .Where(character => this.selectedCharacter == "全部角色" || CharacterLabel(character) == this.selectedCharacter)
            .SelectMany(character => character.Storages.SelectMany(storage => storage.Items.Select(item => new SearchResult(character, storage, item))))
            .Where(result => this.KindMatches(result.Storage.Kind))
            .Where(result => string.IsNullOrEmpty(query)
                || result.Item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || result.Item.ItemId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(result => result.Item.Name)
            .ThenBy(result => result.Character.Name)
            .Take(1000)
            .ToList();

        ImGui.TextUnformatted($"已勾选 {this.selectedWithdrawals.Count} 格 · 按各条目数量取出");
        ImGui.SameLine();
        var canStartBatch = this.selectedWithdrawals.Count > 0
            && this.withdrawal == null && this.deposit == null
            && !this.batchWithdrawalActive && !this.batchDepositActive;
        ImGui.BeginDisabled(!canStartBatch);
        if (ImGui.Button("批量取出"))
            this.StartBatchWithdrawal();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(this.selectedWithdrawals.Count == 0 || this.batchWithdrawalActive);
        if (ImGui.Button("清除勾选"))
            this.selectedWithdrawals.Clear();
        ImGui.EndDisabled();

        ImGui.TextDisabled($"找到 {results.Count} 条位置记录（最多显示 1000 条）");
        if (!ImGui.BeginChild("results", Vector2.Zero, true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var result in results)
        {
            var resultKey = ResultKey(result);
            ImGui.PushID(resultKey);
            if (result.Storage.Kind == StorageKind.Retainer)
            {
                var selected = this.selectedWithdrawals.ContainsKey(resultKey);
                if (ImGui.Checkbox("##batchSelect", ref selected))
                {
                    if (selected)
                        this.selectedWithdrawals[resultKey] = new BatchWithdrawalItem(
                            result.Character,
                            result.Storage,
                            result.Item,
                            (int)Math.Min(result.Item.Quantity, int.MaxValue));
                    else
                        this.selectedWithdrawals.Remove(resultKey);
                }
                ImGui.SameLine();
            }
            var texture = this.GetIcon(result.Item.IconId);
            if (texture.TryGetWrap(out var wrap, out _))
            {
                ImGui.Image(wrap.Handle, new Vector2(34, 34));
                ImGui.SameLine();
            }
            ImGui.BeginGroup();
            ImGui.TextUnformatted($"{result.Item.Name}{(result.Item.IsHq ? " HQ" : string.Empty)}  ×{result.Item.Quantity}");
            ImGui.TextDisabled($"{CharacterLabel(result.Character)}  ·  {KindName(result.Storage.Kind)}{OwnerSuffix(result.Storage)}  ·  ID {result.Item.ItemId}  ·  {Location(result.Storage.Kind, result.Item)}");
            ImGui.EndGroup();
            if (result.Storage.Kind == StorageKind.Retainer)
            {
                var quantityKey = $"{result.Character.ContentId}:{result.Storage.Key}:{result.Item.Container}:{result.Item.Slot}";
                if (!this.withdrawalQuantities.TryGetValue(quantityKey, out var quantity))
                    quantity = (int)Math.Min(result.Item.Quantity, int.MaxValue);
                var maxQuantity = (int)Math.Min(result.Item.Quantity, int.MaxValue);
                quantity = Math.Clamp(quantity, 1, maxQuantity);

                ImGui.SameLine();
                ImGui.BeginDisabled(quantity <= 1);
                if (ImGui.SmallButton("-"))
                    quantity--;
                ImGui.EndDisabled();
                ImGui.SameLine(0, 3);
                ImGui.SetNextItemWidth(68);
                if (ImGui.InputInt("##withdrawQuantity", ref quantity, 0, 0))
                    quantity = Math.Clamp(quantity, 1, maxQuantity);
                ImGui.SameLine(0, 3);
                ImGui.BeginDisabled(quantity >= maxQuantity);
                if (ImGui.SmallButton("+"))
                    quantity++;
                ImGui.EndDisabled();
                this.withdrawalQuantities[quantityKey] = quantity;
                if (this.selectedWithdrawals.ContainsKey(resultKey))
                    this.selectedWithdrawals[resultKey] = new BatchWithdrawalItem(
                        result.Character, result.Storage, result.Item, quantity);

                var canWithdraw = this.CanStartWithdrawal(result, quantity, out var disabledReason)
                    && !this.batchWithdrawalActive && !this.batchDepositActive;
                ImGui.SameLine();
                ImGui.BeginDisabled(!canWithdraw);
                if (ImGui.Button("取出"))
                    this.BeginWithdrawal(result, quantity);
                ImGui.EndDisabled();
                if (!canWithdraw && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(disabledReason);
                else if (canWithdraw && ImGui.IsItemHovered())
                    ImGui.SetTooltip($"从这一格取出 {quantity} 个到角色背包");
            }
            ImGui.Separator();
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    private bool KindMatches(StorageKind kind) => this.selectedKind == 0 || (int)kind == this.selectedKind - 1;
    private static string ResultKey(SearchResult result)
        => $"{result.Character.ContentId}:{result.Storage.Key}:{result.Item.Container}:{result.Item.Slot}";
    private static string OrganizerKey(OrganizerSuggestion suggestion)
        => $"organize:{suggestion.Character.ContentId}:{suggestion.SourceStorage.OwnerId}:{suggestion.SourceItem.Container}:{suggestion.SourceItem.Slot}:{suggestion.TargetStorage.OwnerId}:{suggestion.TargetItem.Container}:{suggestion.TargetItem.Slot}";
    private static string CharacterLabel(CharacterSnapshot character)
        => string.IsNullOrWhiteSpace(character.World) ? character.Name : $"{character.Name} @ {character.World}";
    private static string OwnerSuffix(StorageSnapshot storage)
        => storage.Kind == StorageKind.Retainer ? $"：{storage.OwnerName}" : string.Empty;
    private static string Location(StorageKind kind, StoredItem item)
    {
        if (kind == StorageKind.Retainer
            && item.Container >= (uint)InventoryType.RetainerPage1
            && item.Container <= (uint)InventoryType.RetainerPage7)
        {
            var page = item.Container - (uint)InventoryType.RetainerPage1 + 1;
            return $"雇员背包第 {page} 栏 / 第 {item.Slot + 1} 格";
        }

        return kind is StorageKind.GlamourDresser or StorageKind.Armoire
            ? $"索引 {item.Slot + 1}"
            : $"容器 {item.Container} / 格 {item.Slot + 1}";
    }
    private static string KindName(StorageKind kind) => kind switch
    {
        StorageKind.Inventory => "角色背包",
        StorageKind.Saddlebag => "陆行鸟鞍囊",
        StorageKind.PremiumSaddlebag => "高级陆行鸟鞍囊",
        StorageKind.Retainer => "雇员",
        StorageKind.GlamourDresser => "幻化台",
        StorageKind.Armoire => "收藏柜",
        _ => kind.ToString(),
    };

    private ISharedImmediateTexture GetIcon(uint iconId)
    {
        if (this.iconCache.TryGetValue(iconId, out var texture))
            return texture;
        texture = TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        this.iconCache[iconId] = texture;
        return texture;
    }

    private readonly record struct SearchResult(
        CharacterSnapshot Character,
        StorageSnapshot Storage,
        StoredItem Item);

    private readonly record struct StackableResult(
        CharacterSnapshot Character,
        StoredItem SourceItem,
        StorageSnapshot TargetStorage,
        StoredItem TargetItem,
        uint StackSize,
        uint MaxDeposit);

    private readonly record struct OrganizerSuggestion(
        CharacterSnapshot Character,
        StorageSnapshot SourceStorage,
        StoredItem SourceItem,
        StorageSnapshot TargetStorage,
        StoredItem TargetItem,
        uint StackSize,
        uint Quantity);

    private sealed record BatchWithdrawalItem(
        CharacterSnapshot Character,
        StorageSnapshot Storage,
        StoredItem Item,
        int Quantity);

    private sealed record BatchDepositItem(
        StackableResult Result,
        int Quantity,
        uint ExpectedSourceQuantity,
        uint ExpectedTargetQuantity);

    private sealed record OrganizerHint(uint ItemId, bool IsHq, ulong TargetRetainerId);

    private readonly record struct InventorySlotKey(InventoryType Container, ushort Slot);

    private sealed record OrganizerTransferGroup(List<OrganizerSuggestion> Items);

    private sealed record OrganizerDepositItem(
        OrganizerSuggestion Suggestion,
        InventoryType ReceivedContainer,
        ushort ReceivedSlot,
        uint ReceivedQuantity);

    private sealed class WithdrawalRequest
    {
        public ulong CharacterContentId { get; init; }
        public ulong RetainerId { get; init; }
        public string RetainerName { get; init; } = string.Empty;
        public uint ItemId { get; init; }
        public bool IsHq { get; init; }
        public uint ExpectedQuantity { get; init; }
        public uint RequestedQuantity { get; init; }
        public InventoryType SourceContainer { get; init; }
        public ushort SourceSlot { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool CloseAfterMove { get; init; }
        public bool OrganizerMode { get; init; }
        public Dictionary<InventorySlotKey, uint> InventoryBeforeMove { get; init; } = [];
        public InventoryType ReceivedContainer { get; set; }
        public ushort ReceivedSlot { get; set; }
        public uint ReceivedQuantity { get; set; }
        public WithdrawalStage Stage { get; set; }
        public long DeadlineTick { get; init; }
        public long NextActionTick { get; set; }
        public bool MoveCompleted { get; set; }
        public uint QuantityBeforeMove { get; set; }
    }

    private sealed class DepositRequest
    {
        public ulong CharacterContentId { get; init; }
        public ulong RetainerId { get; init; }
        public string RetainerName { get; init; } = string.Empty;
        public uint ItemId { get; init; }
        public bool IsHq { get; init; }
        public uint SourceExpectedQuantity { get; init; }
        public uint RequestedQuantity { get; init; }
        public InventoryType SourceContainer { get; init; }
        public ushort SourceSlot { get; init; }
        public uint TargetExpectedQuantity { get; init; }
        public InventoryType TargetContainer { get; init; }
        public ushort TargetSlot { get; init; }
        public uint StackSize { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public bool BatchMode { get; init; }
        public bool OrganizerMode { get; init; }
        public DepositStage Stage { get; set; }
        public long DeadlineTick { get; init; }
        public long NextActionTick { get; set; }
        public bool MoveCompleted { get; set; }
    }

    private enum WithdrawalStage
    {
        WaitForBellReady,
        OpenBell,
        WaitForList,
        WaitForRetainer,
        WaitForInventory,
        RequestRetrieve,
        WaitForQuantity,
        WaitForMove,
        CloseInventory,
        WaitForRetainerMenu,
        WaitForListClose,
        WaitForClosed,
    }

    private enum DepositStage
    {
        WaitForBellReady,
        OpenBell,
        WaitForList,
        WaitForRetainer,
        WaitForInventory,
        RequestDeposit,
        WaitForQuantity,
        WaitForMove,
        CloseInventory,
        WaitForRetainerMenu,
        WaitForListClose,
        WaitForClosed,
    }
}
