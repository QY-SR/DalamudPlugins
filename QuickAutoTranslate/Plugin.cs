using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Text.Payloads;
using Lumina.Excel.Sheets;
using QianyanLegacy;

namespace QuickAutoTranslate;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/qat";
    private const string HistoricalActionLookup =
        "Action[11,14,19,25-26,35,47,50,59-60,63-64,67,71-73,79,81,95,103,115,128,134,145-146," +
        "164-165,168,170-171,174,176-180,182,184,212-214,216-217,219,225-226,231,233-234,236,242-244," +
        "252,270-271,274-279,281,283-287,292-293,301,633-634,637,639,787-788,791-792,794,796-798," +
        "800-801,2256-2257,2865,2867,2875,2879-2881,2885,2888,2891-2892,3487,3544,3546,3548,3553," +
        "3564-3565,3567,3572,3574-3575,3580,3588-3593,3604-3605,3611,3616,3619,3627-3628,3631,3633," +
        "3635,3640,3642,4074-4079,4082-4084,4096-4097,4101,4401-4406,4560,4568-4573,4587,4591," +
        "4605-4606,4615,4645-4646,7398,7417,7423-7425,7443,7448,7450,7494,7500-7502,7508,7522," +
        "7532,7534,7536,7539,7543-7545,7547,7550,7552,7555-7556,7558,7563-7567,7569-7570,7572," +
        "7634,7864-7866,7868,7907-7908,9015,9372,9629,16154,16475,16484,16509,16512-16513,16520-16523," +
        "16791-16803,17055,17216,25779,25870,25876,37036]";

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;

    private readonly Configuration configuration;
    private readonly List<Entry> entries = [];
    private readonly Dictionary<uint, Entry> entriesByRow = [];
    private readonly List<HistoricalAction> historicalActions = [];
    private readonly List<JobOption> jobOptions = [];
    private List<Entry> visibleEntries = [];
    private string search = string.Empty;
    private bool windowOpen;
    private bool migrationNoticeRequested;
    private bool historicalWindowOpen;
    private bool requestSearchFocus;
    private int selectedIndex;
    private string historicalSearch = string.Empty;
    private int historicalJobFilter = -1;
    private uint selectedHistoricalActionId;
    private int selectedHotbar = 1;
    private uint pendingActionId;
    private int pendingHotbarIndex = -1;
    private int pendingSlotIndex = -1;

    public Plugin()
    {
        this.configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.configuration.FavoriteRows ??= [];
        this.configuration.RecentRows ??= [];
        this.configuration.MaximumResults = Math.Clamp(this.configuration.MaximumResults, 20, 300);

        this.LoadEntries();
        this.UpdateVisibleEntries();

        CommandManager.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开定型文快速筛选。使用 /qat hotbar 打开历史技能热键栏。",
            ShowInHelp = true,
        });

        PluginInterface.UiBuilder.Draw += this.DrawWindow;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenWindow;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= this.DrawWindow;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenWindow;
        CommandManager.RemoveHandler(Command);
    }

    private void LoadEntries()
    {
        try
        {
            this.jobOptions.AddRange(DataManager.GetExcelSheet<ClassJob>()
                .Where(job => job.RowId is >= 1 and <= 42 && job.Name.ToString().Trim().Length > 0)
                .Select(job => new JobOption(job.RowId, job.Name.ToString().Trim(), job.Abbreviation.ToString().Trim()))
                .OrderBy(job => job.RowId));

            var rows = DataManager.GetExcelSheet<Completion>().ToList();
            var groupTitles = rows
                .Select(row => (row.Group, Title: row.GroupTitle.ToString().Trim()))
                .Where(static item => item.Group != 0 && item.Title.Length > 0)
                .GroupBy(static item => item.Group)
                .ToDictionary(static group => group.Key, static group => group.First().Title);

            foreach (var row in rows)
            {
                var text = row.Text.ToString().Trim();
                if (row.Group == 0 || row.Key == 0 || string.IsNullOrWhiteSpace(text))
                    continue;

                var category = row.GroupTitle.ToString().Trim();
                if (category.Length == 0)
                    category = groupTitles.GetValueOrDefault(row.Group, string.Empty);
                var lookupTable = row.LookupTable.ToString().Trim();
                var macroKey = lookupTable.Length == 0 ? row.RowId : row.Key;
                var entry = new Entry(
                    row.RowId,
                    row.Group,
                    macroKey,
                    text,
                    category,
                    PinyinInitials.Get(text),
                    PinyinInitials.Normalize(text),
                    PinyinInitials.Get(category),
                    PinyinInitials.Normalize(category));
                this.entries.Add(entry);
                this.entriesByRow[row.RowId] = entry;
            }

            var dynamicCount = 0;
            foreach (var row in rows)
            {
                var lookupTable = row.LookupTable.ToString().Trim();
                if (row.Group == 0 || lookupTable.Length == 0 || lookupTable == "@")
                    continue;

                var category = row.GroupTitle.ToString().Trim();
                if (category.Length == 0)
                    category = groupTitles.GetValueOrDefault(row.Group, string.Empty);

                var bracket = lookupTable.IndexOf('[');
                var sheetName = bracket < 0 ? lookupTable : lookupTable[..bracket];
                var ranges = LookupRanges.Parse(lookupTable);
                dynamicCount += sheetName switch
                {
                    "Action" => this.AddActionEntries(row.Group, category, ranges),
                    "GeneralAction" => this.AddDynamicEntries(
                        row.Group,
                        category,
                        ranges,
                        DataManager.GetExcelSheet<GeneralAction>()
                            .Select(action => (action.RowId, action.Name.ToString()))),
                    "CraftAction" => this.AddDynamicEntries(
                        row.Group,
                        category,
                        ranges,
                        DataManager.GetExcelSheet<CraftAction>()
                            .Select(action => (action.RowId, action.Name.ToString()))),
                    "BuddyAction" => this.AddDynamicEntries(
                        row.Group,
                        category,
                        ranges,
                        DataManager.GetExcelSheet<BuddyAction>()
                            .Select(action => (action.RowId, action.Name.ToString()))),
                    "PetAction" => this.AddDynamicEntries(
                        row.Group,
                        category,
                        ranges,
                        DataManager.GetExcelSheet<PetAction>()
                            .Select(action => (action.RowId, action.Name.ToString()))),
                    _ => 0,
                };
            }

            this.entries.Sort(static (left, right) => string.Compare(left.Text, right.Text, StringComparison.CurrentCulture));
            Log.Information(
                "Loaded {Count} auto-translate entries, including {DynamicCount} dynamically referenced actions.",
                this.entries.Count,
                dynamicCount);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not load Completion sheet.");
        }
    }

    private int AddActionEntries(ushort group, string category, LookupRanges currentRanges)
    {
        var actionRows = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().ToList();
        var actions = actionRows
            .Select(action => (action.RowId, action.Name.ToString()))
            .ToList();
        var count = this.AddDynamicEntries(group, category, currentRanges, actions);

        // Group 56 is the normal Disciple of War & Magic action dictionary.
        // Keep entries that appeared in historical Completion ranges but were
        // removed from the current picker, as long as their Action name still
        // exists in the installed client data.
        if (group == 56)
        {
            var historicalRanges = LookupRanges.Parse(HistoricalActionLookup);
            count += this.AddDynamicEntries(
                group,
                "历史技能定型文（已从原生列表移除）",
                historicalRanges,
                actions.Where(action => !currentRanges.Contains(action.RowId)));

            var descriptions = DataManager.GetExcelSheet<ActionTransient>()
                .ToDictionary(row => row.RowId, row => row.Description.ToString().Trim());
            var categories = DataManager.GetExcelSheet<ClassJobCategory>()
                .ToDictionary(row => row.RowId);
            foreach (var action in actionRows)
            {
                var name = action.Name.ToString().Trim();
                if (name.Length == 0 || currentRanges.Contains(action.RowId) || !historicalRanges.Contains(action.RowId))
                    continue;

                var jobMask = categories.TryGetValue(action.ClassJobCategory.RowId, out var jobCategory)
                    ? GetClassJobMask(jobCategory)
                    : 0UL;
                if (jobMask == 0 && action.ClassJob.RowId is > 0 and < 64)
                    jobMask = 1UL << (int)action.ClassJob.RowId;

                this.historicalActions.Add(new HistoricalAction(
                    action.RowId,
                    name,
                    descriptions.GetValueOrDefault(action.RowId, string.Empty),
                    action.Icon,
                    jobMask,
                    action.ClassJobLevel,
                    PinyinInitials.Get(name),
                    PinyinInitials.Normalize(name)));
            }

            this.historicalActions.Sort(static (left, right) =>
                string.Compare(left.Name, right.Name, StringComparison.CurrentCulture));
        }

        return count;
    }

    private static ulong GetClassJobMask(ClassJobCategory category)
    {
        ulong mask = 0;
        if (category.GLA) mask |= 1UL << 1;
        if (category.PGL) mask |= 1UL << 2;
        if (category.MRD) mask |= 1UL << 3;
        if (category.LNC) mask |= 1UL << 4;
        if (category.ARC) mask |= 1UL << 5;
        if (category.CNJ) mask |= 1UL << 6;
        if (category.THM) mask |= 1UL << 7;
        if (category.CRP) mask |= 1UL << 8;
        if (category.BSM) mask |= 1UL << 9;
        if (category.ARM) mask |= 1UL << 10;
        if (category.GSM) mask |= 1UL << 11;
        if (category.LTW) mask |= 1UL << 12;
        if (category.WVR) mask |= 1UL << 13;
        if (category.ALC) mask |= 1UL << 14;
        if (category.CUL) mask |= 1UL << 15;
        if (category.MIN) mask |= 1UL << 16;
        if (category.BTN) mask |= 1UL << 17;
        if (category.FSH) mask |= 1UL << 18;
        if (category.PLD) mask |= 1UL << 19;
        if (category.MNK) mask |= 1UL << 20;
        if (category.WAR) mask |= 1UL << 21;
        if (category.DRG) mask |= 1UL << 22;
        if (category.BRD) mask |= 1UL << 23;
        if (category.WHM) mask |= 1UL << 24;
        if (category.BLM) mask |= 1UL << 25;
        if (category.ACN) mask |= 1UL << 26;
        if (category.SMN) mask |= 1UL << 27;
        if (category.SCH) mask |= 1UL << 28;
        if (category.ROG) mask |= 1UL << 29;
        if (category.NIN) mask |= 1UL << 30;
        if (category.MCH) mask |= 1UL << 31;
        if (category.DRK) mask |= 1UL << 32;
        if (category.AST) mask |= 1UL << 33;
        if (category.SAM) mask |= 1UL << 34;
        if (category.RDM) mask |= 1UL << 35;
        if (category.BLU) mask |= 1UL << 36;
        if (category.GNB) mask |= 1UL << 37;
        if (category.DNC) mask |= 1UL << 38;
        if (category.RPR) mask |= 1UL << 39;
        if (category.SGE) mask |= 1UL << 40;
        if (category.VPR) mask |= 1UL << 41;
        if (category.PCT) mask |= 1UL << 42;
        return mask;
    }

    private int AddDynamicEntries(
        ushort group,
        string category,
        LookupRanges ranges,
        IEnumerable<(uint RowId, string Name)> rows)
    {
        var count = 0;
        foreach (var (rowId, rawName) in rows)
        {
            var name = rawName.Trim();
            if (name.Length == 0 || !ranges.Contains(rowId))
                continue;

            // Dynamic Completion entries use the referenced sheet's row ID as the
            // Fixed-macro key. Keep a separate synthetic ID for favorites/recents.
            var identity = 0x80000000u | ((uint)group << 24) | (rowId & 0x00FFFFFFu);
            var entry = new Entry(
                identity,
                group,
                rowId,
                name,
                category,
                PinyinInitials.Get(name),
                PinyinInitials.Normalize(name),
                PinyinInitials.Get(category),
                PinyinInitials.Normalize(category));
            this.entries.Add(entry);
            this.entriesByRow[identity] = entry;
            count++;
        }

        return count;
    }

    private sealed class LookupRanges
    {
        private readonly List<(uint Start, uint End)> ranges = [];

        private LookupRanges(bool includeAll)
            => this.IncludeAll = includeAll;

        private bool IncludeAll { get; }

        public bool Contains(uint rowId)
            => this.IncludeAll || this.ranges.Any(range => rowId >= range.Start && rowId <= range.End);

        public static LookupRanges Parse(string lookupTable)
        {
            var open = lookupTable.IndexOf('[');
            var close = lookupTable.LastIndexOf(']');
            if (open < 0 || close <= open)
                return new LookupRanges(true);

            var result = new LookupRanges(false);
            foreach (var rawToken in lookupTable[(open + 1)..close].Split(','))
            {
                var token = rawToken.Trim();
                if (token.Length == 0 || token.StartsWith("col-", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("noun", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dash = token.IndexOf('-');
                if (dash < 0)
                {
                    if (uint.TryParse(token, out var value))
                        result.ranges.Add((value, value));
                    continue;
                }

                if (uint.TryParse(token[..dash].Trim(), out var start)
                    && uint.TryParse(token[(dash + 1)..].Trim(), out var end))
                    result.ranges.Add((Math.Min(start, end), Math.Max(start, end)));
            }

            return result;
        }
    }

    private void OnCommand(string command, string arguments)
    {
        this.migrationNoticeRequested = true;
        if (arguments.Trim().Equals("hotbar", StringComparison.OrdinalIgnoreCase)
            || arguments.Trim().Equals("history", StringComparison.OrdinalIgnoreCase))
        {
            this.historicalWindowOpen = true;
            return;
        }

        this.search = arguments.Trim();
        this.OpenWindow();
    }

    private void OpenWindow()
    {
        this.windowOpen = true;
        this.migrationNoticeRequested = true;
        this.requestSearchFocus = true;
        this.selectedIndex = 0;
        this.UpdateVisibleEntries();
    }

    private unsafe void DrawHistoricalWindow()
    {
        if (!this.historicalWindowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(900f, 640f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("历史技能热键栏###QuickAutoTranslateHistoricalHotbar", ref this.historicalWindowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("这里只列出历史上曾进入原生技能定型文列表、当前已被移除，但客户端仍保留名称的技能。拖动左侧技能到右侧格子，会写入当前角色（或共享）的真实标准热键栏；技能不会因此恢复可用。");
        ImGui.Separator();

        ImGui.SetNextItemWidth(310f);
        ImGui.InputTextWithHint("###historicalSearch", "搜索名称或拼音首字母，例如：ty / 天语", ref this.historicalSearch, 128);
        ImGui.SameLine();

        var currentJobId = PlayerState.IsLoaded ? PlayerState.ClassJob.RowId : 0u;
        var filterLabel = this.historicalJobFilter switch
        {
            -1 when currentJobId != 0 => $"当前职业：{this.GetJobLabel(currentJobId)}",
            -1 => "当前职业（尚未登录）",
            0 => "全部职业",
            _ => this.GetJobLabel((uint)this.historicalJobFilter),
        };
        ImGui.SetNextItemWidth(230f);
        if (ImGui.BeginCombo("职业筛选", filterLabel))
        {
            if (ImGui.Selectable("当前职业", this.historicalJobFilter == -1))
                this.historicalJobFilter = -1;
            if (ImGui.Selectable("全部职业", this.historicalJobFilter == 0))
                this.historicalJobFilter = 0;
            ImGui.Separator();
            foreach (var job in this.jobOptions)
            {
                if (ImGui.Selectable($"{job.Name} ({job.Abbreviation})", this.historicalJobFilter == job.RowId))
                    this.historicalJobFilter = (int)job.RowId;
            }

            ImGui.EndCombo();
        }

        var query = PinyinInitials.Normalize(this.historicalSearch);
        var effectiveJobId = this.historicalJobFilter == -1 ? currentJobId : (uint)this.historicalJobFilter;
        var filtered = this.historicalActions
            .Where(action => query.Length == 0
                || action.NormalizedName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || action.Initials.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(action => effectiveJobId == 0 || action.SupportsJob(effectiveJobId))
            .ToList();

        if (filtered.Count > 0 && filtered.All(action => action.RowId != this.selectedHistoricalActionId))
            this.selectedHistoricalActionId = filtered[0].RowId;

        if (ImGui.BeginChild("historicalActions", new Vector2(330f, -1f), true))
        {
            ImGui.TextDisabled($"{filtered.Count} 个仍保留名称的历史技能");
            ImGui.Separator();
            foreach (var action in filtered)
            {
                ImGui.PushID((int)action.RowId);
                if (ImGui.Selectable($"{action.Name}  [{this.GetJobSummary(action)}]  (ID {action.RowId})", this.selectedHistoricalActionId == action.RowId))
                    this.selectedHistoricalActionId = action.RowId;

                if (ImGui.BeginDragDropSource())
                {
                    ImGui.SetDragDropPayload("QAT_HISTORICAL_ACTION", BitConverter.GetBytes(action.RowId), ImGuiCond.Once);
                    ImGui.Text($"拖动：{action.Name}");
                    ImGui.TextDisabled($"Action ID {action.RowId}");
                    ImGui.EndDragDropSource();
                }

                ImGui.PopID();
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("historicalHotbarTarget", new Vector2(0f, -1f), true))
        {
            var selected = this.historicalActions.FirstOrDefault(action => action.RowId == this.selectedHistoricalActionId);
            if (selected is not null)
            {
                if (this.DrawHistoricalActionIcon(selected))
                    ImGui.SameLine();

                ImGui.BeginGroup();
                ImGui.Text(selected.Name);
                ImGui.TextDisabled($"Action ID {selected.RowId} · Lv.{selected.ClassJobLevel} · {this.GetJobSummary(selected)} · 原图标 {selected.Icon}");
                ImGui.EndGroup();
                ImGui.Spacing();
                ImGui.TextWrapped(selected.Description.Length == 0
                    ? "当前客户端没有保留该技能的描述。"
                    : selected.Description);
            }
            else
            {
                ImGui.TextDisabled("请从左侧选择一个历史技能。");
            }

            ImGui.Separator();
            ImGui.SetNextItemWidth(260f);
            ImGui.SliderInt("标准热键栏", ref this.selectedHotbar, 1, 10, "第 %d 栏");

            var module = RaptureHotbarModule.Instance();
            if (module == null || !module->ModuleReady)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "热键栏模块尚未加载，请先登录角色。");
            }
            else
            {
                var viewingOtherJob = this.historicalJobFilter > 0
                    && (uint)this.historicalJobFilter != currentJobId;
                if (viewingOtherJob)
                    ImGui.TextColored(
                        new Vector4(1f, 0.72f, 0.2f, 1f),
                        $"当前登录职业是 {this.GetJobLabel(currentJobId)}。请切换到 {this.GetJobLabel((uint)this.historicalJobFilter)} 后再写入其职业热键栏。当前仅供查看。 ");

                ImGui.TextDisabled("把左侧技能拖到下面对应格子。红色格子已有内容，松开后会要求确认覆盖。");
                ImGui.BeginDisabled(viewingOtherJob);
                var hotbarIndex = this.selectedHotbar - 1;
                for (var slotIndex = 0; slotIndex < 12; slotIndex++)
                {
                    var slot = module->GetSlotById((uint)hotbarIndex, (uint)slotIndex);
                    var occupied = slot != null && !slot->IsEmpty;
                    if (occupied)
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.45f, 0.14f, 0.14f, 1f));

                    ImGui.Button($"{slotIndex + 1}\n{(occupied ? $"ID {slot->CommandId}" : "空")}##hotbarSlot{slotIndex}", new Vector2(78f, 54f));

                    if (occupied)
                        ImGui.PopStyleColor();

                    if (ImGui.BeginDragDropTarget())
                    {
                        var payload = ImGui.AcceptDragDropPayload("QAT_HISTORICAL_ACTION", ImGuiDragDropFlags.None);
                        if (!payload.IsNull && payload.Delivery && payload.DataSize == sizeof(uint))
                            this.RequestHotbarWrite(*(uint*)payload.Data, hotbarIndex, slotIndex);
                        ImGui.EndDragDropTarget();
                    }

                    if (slotIndex % 6 != 5)
                        ImGui.SameLine();
                }

                if (selected is not null && ImGui.Button($"把“{selected.Name}”放入本栏第一个空槽"))
                {
                    if (module->SetAndSaveFirstAvailableNormalSlot(
                            (uint)(this.selectedHotbar - 1),
                            RaptureHotbarModule.HotbarSlotType.Action,
                            selected.RowId))
                        ChatGui.Print($"[定型文快速筛选] 已把“{selected.Name}”放入第 {this.selectedHotbar} 栏的第一个空槽。");
                    else
                        ChatGui.PrintError($"[定型文快速筛选] 第 {this.selectedHotbar} 栏没有可用空槽。");
                }
                ImGui.EndDisabled();
            }

            this.DrawOverwriteConfirmation();
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private unsafe void RequestHotbarWrite(uint actionId, int hotbarIndex, int slotIndex)
    {
        if (hotbarIndex is < 0 or >= 10 || slotIndex is < 0 or >= 12
            || this.historicalActions.All(action => action.RowId != actionId))
            return;

        var module = RaptureHotbarModule.Instance();
        if (module == null || !module->ModuleReady)
            return;
        var slot = module->GetSlotById((uint)hotbarIndex, (uint)slotIndex);
        if (slot != null && !slot->IsEmpty)
        {
            this.pendingActionId = actionId;
            this.pendingHotbarIndex = hotbarIndex;
            this.pendingSlotIndex = slotIndex;
            ImGui.OpenPopup("确认覆盖热键栏槽位");
            return;
        }

        this.WriteHistoricalActionToSlot(actionId, hotbarIndex, slotIndex);
    }

    private unsafe void DrawOverwriteConfirmation()
    {
        if (!ImGui.BeginPopupModal("确认覆盖热键栏槽位"))
            return;

        var action = this.historicalActions.FirstOrDefault(item => item.RowId == this.pendingActionId);
        ImGui.TextWrapped($"第 {this.pendingHotbarIndex + 1} 栏第 {this.pendingSlotIndex + 1} 格已有内容。确定要用“{action?.Name ?? this.pendingActionId.ToString()}”覆盖吗？");
        if (ImGui.Button("覆盖", new Vector2(100f, 0f)))
        {
            this.WriteHistoricalActionToSlot(this.pendingActionId, this.pendingHotbarIndex, this.pendingSlotIndex);
            this.ClearPendingHotbarWrite();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(100f, 0f)))
        {
            this.ClearPendingHotbarWrite();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private unsafe void WriteHistoricalActionToSlot(uint actionId, int hotbarIndex, int slotIndex)
    {
        var module = RaptureHotbarModule.Instance();
        var action = this.historicalActions.FirstOrDefault(item => item.RowId == actionId);
        if (module == null || !module->ModuleReady || action is null
            || hotbarIndex is < 0 or >= 10 || slotIndex is < 0 or >= 12)
            return;

        module->SetAndSaveSlot(
            (uint)hotbarIndex,
            (uint)slotIndex,
            RaptureHotbarModule.HotbarSlotType.Action,
            actionId);
        Log.Information("Placed historical action {ActionId} in hotbar {HotbarId}, slot {SlotId}.", actionId, hotbarIndex, slotIndex);
        ChatGui.Print($"[定型文快速筛选] 已把“{action.Name}”放入第 {hotbarIndex + 1} 栏第 {slotIndex + 1} 格。技能是否可用仍由游戏判定。");
    }

    private void ClearPendingHotbarWrite()
    {
        this.pendingActionId = 0;
        this.pendingHotbarIndex = -1;
        this.pendingSlotIndex = -1;
    }

    private bool DrawHistoricalActionIcon(HistoricalAction action)
    {
        if (action.Icon != 0 && this.TryDrawGameIcon(action.Icon))
            return true;

        // Icon 405 is used by the empty/invalid Action row in the client data.
        return this.TryDrawGameIcon(405);
    }

    private bool TryDrawGameIcon(uint iconId)
    {
        try
        {
            var lookup = new GameIconLookup(iconId);
            var sharedTexture = TextureProvider.GetFromGameIcon(lookup);
            if (!sharedTexture.TryGetWrap(out var texture, out _))
                return false;

            ImGui.Image(texture.Handle, new Vector2(48f, 48f));
            return true;
        }
        catch (Exception exception)
        {
            Log.Verbose(exception, "Could not load game icon {IconId}.", iconId);
            return false;
        }
    }

    private string GetJobLabel(uint jobId)
    {
        var job = this.jobOptions.FirstOrDefault(option => option.RowId == jobId);
        return job is null ? $"职业 ID {jobId}" : $"{job.Name} ({job.Abbreviation})";
    }

    private string GetJobSummary(HistoricalAction action)
    {
        var abbreviations = this.jobOptions
            .Where(job => action.SupportsJob(job.RowId))
            .Select(job => job.Abbreviation)
            .Where(static abbreviation => abbreviation.Length > 0)
            .ToList();
        if (abbreviations.Count == 0)
            return "职业未知";
        if (abbreviations.Count <= 4)
            return string.Join('/', abbreviations);
        return $"{string.Join('/', abbreviations.Take(3))} 等 {abbreviations.Count} 职业";
    }

    private void DrawWindow()
    {
        this.DrawHistoricalWindow();
        OldStableNotice.Draw("QuickAutoTranslate", ref this.migrationNoticeRequested);

        if (!this.windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(580f, 520f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("定型文快速筛选###QuickAutoTranslate", ref this.windowOpen))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("历史技能热键栏..."))
            this.historicalWindowOpen = true;
        ImGui.SameLine();
        ImGui.TextDisabled("也可使用 /qat hotbar 打开");
        ImGui.Separator();

        if (this.requestSearchFocus)
        {
            ImGui.SetKeyboardFocusHere();
            this.requestSearchFocus = false;
        }

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("###search", "输入中文、拼音首字母或分类，例如：xk / 辛苦 / 战斗", ref this.search, 128))
        {
            this.selectedIndex = 0;
            this.UpdateVisibleEntries();
        }

        this.HandleKeyboard();

        ImGui.Spacing();
        ImGui.TextDisabled(this.search.Length == 0
            ? "空搜索显示收藏和最近使用；单击或 Enter 只会放入聊天输入框"
            : $"显示 {this.visibleEntries.Count} 条结果；↑↓ 选择，Enter 放入聊天框，Esc 关闭");

        ImGui.Separator();
        if (this.entries.Count == 0)
        {
            ImGui.TextWrapped("没有读取到定型文数据。请确认游戏数据已加载后重新载入插件。");
            ImGui.End();
            return;
        }

        if (this.visibleEntries.Count == 0)
        {
            ImGui.TextDisabled("没有匹配结果");
            ImGui.End();
            return;
        }

        if (ImGui.BeginChild("results", new Vector2(0f, -42f), true))
        {
            for (var index = 0; index < this.visibleEntries.Count; index++)
            {
                var entry = this.visibleEntries[index];
                var isFavorite = this.configuration.FavoriteRows.Contains(entry.RowId);
                ImGui.PushID((int)entry.RowId);

                if (ImGui.SmallButton(isFavorite ? "★" : "☆"))
                    this.ToggleFavorite(entry.RowId);

                ImGui.SameLine();
                var label = string.IsNullOrEmpty(entry.Category)
                    ? entry.Text
                    : $"{entry.Text}    [{entry.Category}]";
                if (ImGui.Selectable(label, this.selectedIndex == index))
                {
                    this.selectedIndex = index;
                    this.InsertEntry(entry);
                }

                if (this.selectedIndex == index && ImGui.IsKeyPressed(ImGuiKey.DownArrow, false))
                    ImGui.SetScrollHereY();
                ImGui.PopID();
            }
        }

        ImGui.EndChild();
        ImGui.Separator();
        ImGui.SetNextItemWidth(110f);
        var maximumResults = this.configuration.MaximumResults;
        if (ImGui.InputInt("最大结果数", ref maximumResults, 10, 50))
        {
            this.configuration.MaximumResults = Math.Clamp(maximumResults, 20, 300);
            this.configuration.Save(PluginInterface);
            this.UpdateVisibleEntries();
        }

        ImGui.End();
    }

    private void HandleKeyboard()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            this.windowOpen = false;
            return;
        }

        if (this.visibleEntries.Count == 0)
            return;

        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, false))
            this.selectedIndex = Math.Min(this.selectedIndex + 1, this.visibleEntries.Count - 1);
        else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, false))
            this.selectedIndex = Math.Max(this.selectedIndex - 1, 0);

        if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
            this.InsertEntry(this.visibleEntries[Math.Clamp(this.selectedIndex, 0, this.visibleEntries.Count - 1)]);
    }

    private void UpdateVisibleEntries()
    {
        var query = PinyinInitials.Normalize(this.search);
        if (query.Length == 0)
        {
            var rows = this.configuration.FavoriteRows
                .Concat(this.configuration.RecentRows)
                .Distinct()
                .Select(rowId => this.entriesByRow.GetValueOrDefault(rowId))
                .Where(static entry => entry is not null)
                .Cast<Entry>()
                .ToList();
            this.visibleEntries = rows.Count > 0
                ? rows
                : this.entries.Take(this.configuration.MaximumResults).ToList();
            this.selectedIndex = Math.Clamp(this.selectedIndex, 0, Math.Max(0, this.visibleEntries.Count - 1));
            return;
        }

        this.visibleEntries = this.entries
            .Select(entry => (Entry: entry, Score: Score(entry, query)))
            .Where(static result => result.Score < int.MaxValue)
            .OrderBy(static result => result.Score)
            .ThenBy(static result => result.Entry.Text.Length)
            .ThenBy(static result => result.Entry.Text, StringComparer.CurrentCulture)
            .Take(this.configuration.MaximumResults)
            .Select(static result => result.Entry)
            .ToList();
        this.selectedIndex = Math.Clamp(this.selectedIndex, 0, Math.Max(0, this.visibleEntries.Count - 1));
    }

    private static int Score(Entry entry, string query)
    {
        if (entry.NormalizedText == query)
            return 0;
        if (entry.Initials == query)
            return 1;
        if (entry.NormalizedText.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (entry.Initials.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (entry.NormalizedText.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 4;
        if (entry.Initials.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 5;
        if (entry.NormalizedCategory.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.CategoryInitials.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 6;
        return int.MaxValue;
    }

    private unsafe void InsertEntry(Entry entry)
    {
        try
        {
            var chatLog = GameGui.GetAddonByName<AddonChatLog>("ChatLog");
            if (chatLog == null || chatLog->TextInput == null)
            {
                ChatGui.PrintError("[定型文快速筛选] 聊天输入框当前未载入，请按 Enter 后重试。");
                return;
            }

            var builder = new Lumina.Text.SeStringBuilder();
            builder.BeginMacro(MacroCode.Fixed);
            builder.AppendUIntExpression(entry.Group - 1u);
            builder.AppendUIntExpression(entry.MacroKey);
            builder.EndMacro();

            var current = chatLog->TextInput->RawString.AsSpan();
            var macro = builder.GetViewAsSpan();
            var addSeparator = current.Length > 0 && !char.IsWhiteSpace((char)current[^1]);
            var combined = new byte[current.Length + (addSeparator ? 1 : 0) + macro.Length + 1];
            current.CopyTo(combined);
            var offset = current.Length;
            if (addSeparator)
                combined[offset++] = (byte)' ';
            macro.CopyTo(combined.AsSpan(offset));

            chatLog->TextInput->SetText(combined);
            Log.Information(
                "Placed auto-translate row {RowId} into chat input. RawLength={RawLength}, EvaluatedLength={EvaluatedLength}",
                entry.RowId,
                chatLog->TextInput->RawString.Length,
                chatLog->TextInput->EvaluatedString.Length);

            this.configuration.RecentRows.Remove(entry.RowId);
            this.configuration.RecentRows.Insert(0, entry.RowId);
            if (this.configuration.RecentRows.Count > 30)
                this.configuration.RecentRows.RemoveRange(30, this.configuration.RecentRows.Count - 30);
            this.configuration.Save(PluginInterface);
            this.windowOpen = false;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not insert auto-translate entry {RowId}.", entry.RowId);
            ChatGui.PrintError("[定型文快速筛选] 写入聊天框失败，详情已写入插件日志。");
        }
    }

    private void ToggleFavorite(uint rowId)
    {
        if (!this.configuration.FavoriteRows.Remove(rowId))
            this.configuration.FavoriteRows.Add(rowId);
        this.configuration.Save(PluginInterface);
        if (this.search.Length == 0)
            this.UpdateVisibleEntries();
    }

    private sealed record Entry(
        uint RowId,
        ushort Group,
        uint MacroKey,
        string Text,
        string Category,
        string Initials,
        string NormalizedText,
        string CategoryInitials,
        string NormalizedCategory);

    private sealed record HistoricalAction(
        uint RowId,
        string Name,
        string Description,
        ushort Icon,
        ulong ClassJobMask,
        byte ClassJobLevel,
        string Initials,
        string NormalizedName)
    {
        public bool SupportsJob(uint jobId)
            => jobId < 64 && (this.ClassJobMask & (1UL << (int)jobId)) != 0;
    }

    private sealed record JobOption(uint RowId, string Name, string Abbreviation);
}
