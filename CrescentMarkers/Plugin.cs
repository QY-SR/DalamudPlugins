using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace CrescentMarkers;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/ocmark";
    private const uint ChestColor = 0xFF48C8FF;
    private const uint CarrotColor = 0xFF42A5FF;
    private const uint TextColor = 0xFFFFFFFF;
    private const uint ShadowColor = 0xE0000000;
    private const float ChestRemovalDistance = 50f;
    private const uint LocalMapMarkerIconId = 60563;
    private const uint ChewedCarrotBaseId = 2010139;
    private static readonly TimeSpan ChestMissingGracePeriod = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan AreaLoadGracePeriod = TimeSpan.FromSeconds(5);

    private static readonly string[] ChestKeywords =
    [
        "宝箱", "宝物箱", "treasure coffer", "treasure chest",
        "schatztruhe", "coffre au trésor",
    ];

    private static readonly string[] CarrotKeywords =
    [
        "胡萝卜", "萝卜", "carrot", "karotte", "carotte",
    ];


    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static ISeStringEvaluator SeStringEvaluator { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IGameInteropProvider GameInteropProvider { get; set; } = null!;

    private readonly List<DetectedObject> detectedObjects = [];
    private readonly HashSet<ulong> announcedObjects = [];
    private readonly HashSet<TrackedChestRecord> observedChestRecords = [];
    private readonly Dictionary<TrackedChestRecord, DateTime> missingChestSince = [];
    private Configuration configuration;
    private bool windowOpen;
    private uint lastTerritoryId;
    private uint lastMapId;
    private DateTime areaChangedAt = DateTime.UtcNow;
    private bool mapMarkersInjected;
    private byte nativeMapMarkerCount;
    private byte injectedMapMarkerCount;
    private int requestedMapMarkerSignature = int.MinValue;
    private readonly Hook<CreateMapMarkersDelegate> createMapMarkersHook;

    private unsafe delegate void CreateMapMarkersDelegate(AgentMap* agentMap, bool omitAetherytes);

    public Plugin()
    {
        this.configuration =
            PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        CommandManager.AddHandler(
            Command,
            new CommandInfo(this.OnCommand)
            {
                HelpMessage = "打开新月岛宝藏标记窗口。",
                ShowInHelp = true,
            });

        PluginInterface.UiBuilder.Draw += this.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenWindow;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenWindow;

        unsafe
        {
            this.createMapMarkersHook =
                GameInteropProvider.HookFromAddress<CreateMapMarkersDelegate>(
                    AgentMap.Addresses.CreateMapMarkers.Value,
                    this.CreateMapMarkersDetour);
        }

        this.createMapMarkersHook.Enable();
    }

    public unsafe void Dispose()
    {
        this.createMapMarkersHook.Disable();
        var agentMap = AgentMap.Instance();
        if (agentMap != null)
            this.RemoveLocalMapMarkers(agentMap);
        this.createMapMarkersHook.Dispose();
        PluginInterface.UiBuilder.Draw -= this.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenWindow;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenWindow;
        CommandManager.RemoveHandler(Command);
    }

    private void OnCommand(string command, string arguments)
        => this.windowOpen = true;

    private void OpenWindow()
        => this.windowOpen = true;

    private void Draw()
    {
        this.ResetTransientStateAfterAreaChange();
        this.RefreshDetectedObjects();
        this.UpdateTrackedChestRecords();
        this.UpdateLocalMapMarkers();

        if (this.configuration.Enabled)
            this.AnnounceNewObjects();

        if (this.configuration.Enabled && !GameGui.GameUiHidden)
            this.DrawWorldMarkers();

        this.DrawWindow();
    }

    private void ResetTransientStateAfterAreaChange()
    {
        var territoryId = ClientState.TerritoryType;
        var mapId = ClientState.MapId;
        if (territoryId == this.lastTerritoryId && mapId == this.lastMapId)
            return;

        this.lastTerritoryId = territoryId;
        this.lastMapId = mapId;
        this.areaChangedAt = DateTime.UtcNow;
        this.detectedObjects.Clear();
        this.announcedObjects.Clear();
        this.observedChestRecords.Clear();
        this.missingChestSince.Clear();
        this.mapMarkersInjected = false;
        this.requestedMapMarkerSignature = int.MinValue;
    }

    private void RefreshDetectedObjects()
    {
        this.detectedObjects.Clear();
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        foreach (var gameObject in ObjectTable)
        {
            if (!gameObject.IsValid() || gameObject.Address == localPlayer.Address)
                continue;

            var distance = Vector3.Distance(localPlayer.Position, gameObject.Position);
            if (distance > this.configuration.MaxDistance)
                continue;

            var markerKind = this.Classify(gameObject);
            if (markerKind == MarkerKind.None)
                continue;

            // Opened chests can remain in the object table briefly. Stop drawing them as
            // soon as the game marks them non-targetable instead of waiting for despawn.
            if (markerKind == MarkerKind.Chest && !gameObject.IsTargetable)
                continue;

            if (markerKind == MarkerKind.Chest && !this.configuration.ShowChests)
                continue;

            if (markerKind == MarkerKind.Carrot && !this.configuration.ShowCarrots)
                continue;

            this.detectedObjects.Add(new DetectedObject(gameObject, markerKind, distance));
        }

        this.detectedObjects.Sort((left, right) => left.Distance.CompareTo(right.Distance));
    }

    private void UpdateTrackedChestRecords()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        var territoryId = ClientState.TerritoryType;
        var mapId = ClientState.MapId;
        var visibleChests = ObjectTable
            .Where(gameObject =>
                gameObject.IsValid()
                && gameObject.IsTargetable
                && this.Classify(gameObject) == MarkerKind.Chest)
            .ToArray();
        var changed = false;

        foreach (var chest in visibleChests)
        {
            var existingRecord = this.configuration.TrackedChests.FirstOrDefault(record =>
                record.TerritoryId == territoryId
                && record.MapId == mapId
                && Vector3.DistanceSquared(this.GetPosition(record), chest.Position) < 2.25f);
            if (existingRecord != null)
            {
                this.observedChestRecords.Add(existingRecord);
                this.missingChestSince.Remove(existingRecord);
                continue;
            }

            var mapCoordinates = chest.GetMapCoordinates();
            var newRecord = new TrackedChestRecord
            {
                    TerritoryId = territoryId,
                    MapId = mapId,
                    BaseId = chest.BaseId,
                    X = chest.Position.X,
                    Y = chest.Position.Y,
                    Z = chest.Position.Z,
                    MapX = mapCoordinates.X,
                MapY = mapCoordinates.Y,
            };
            this.configuration.TrackedChests.Add(newRecord);
            this.observedChestRecords.Add(newRecord);
            this.missingChestSince.Remove(newRecord);
            changed = true;
        }

        var now = DateTime.UtcNow;
        for (var index = this.configuration.TrackedChests.Count - 1; index >= 0; index--)
        {
            var record = this.configuration.TrackedChests[index];
            if (record.TerritoryId != territoryId || record.MapId != mapId)
                continue;

            var position = this.GetPosition(record);
            var stillVisible = visibleChests.Any(chest =>
                Vector3.DistanceSquared(chest.Position, position) < 2.25f);
            if (stillVisible)
            {
                this.missingChestSince.Remove(record);
                continue;
            }

            if (Vector3.Distance(localPlayer.Position, position) > ChestRemovalDistance
                || now - this.areaChangedAt < AreaLoadGracePeriod)
            {
                this.missingChestSince.Remove(record);
                continue;
            }

            if (!this.missingChestSince.TryGetValue(record, out var missingSince))
            {
                this.missingChestSince[record] = now;
                continue;
            }

            if (now - missingSince < ChestMissingGracePeriod)
                continue;

            this.configuration.TrackedChests.RemoveAt(index);
            this.observedChestRecords.Remove(record);
            this.missingChestSince.Remove(record);
            changed = true;
        }

        if (changed)
            this.Save();
    }

    private Vector3 GetPosition(TrackedChestRecord record)
        => new(record.X, record.Y, record.Z);
    private MarkerKind Classify(IGameObject gameObject)
    {
        // Player names may contain words such as 宝箱 or 萝卜, but players are never markers.
        if (gameObject is IPlayerCharacter)
            return MarkerKind.None;

        // The carrot itself is an anonymous EventObj and must remain detectable even
        // if the game assigns ownership metadata while it is being interacted with.
        if (gameObject.BaseId == ChewedCarrotBaseId
            || this.configuration.CarrotBaseIds.Contains(gameObject.BaseId))
            return MarkerKind.Carrot;

        // Reward coffers spawned by another player's carrot/rabbit dig have an owner.
        // Natural island treasure chests do not. Exclude owned objects before either
        // learned BaseId rules or localized name matching can classify them as chests.
        if (gameObject.OwnerId is not 0 and not 0xE0000000)
            return MarkerKind.None;

        if (this.configuration.ChestBaseIds.Contains(gameObject.BaseId))
            return MarkerKind.Chest;

        var name = this.GetObjectName(gameObject);
        if (name.Length == 0)
            return MarkerKind.None;

        if (ChestKeywords.Any(keyword =>
                name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return MarkerKind.Chest;
        }

        if (CarrotKeywords.Any(keyword =>
                name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return MarkerKind.Carrot;
        }

        return MarkerKind.None;
    }

    private string GetObjectName(IGameObject gameObject)
    {
        var name = gameObject.Name.TextValue.Trim();
        if (name.Length > 0)
            return name;

        try
        {
            return SeStringEvaluator
                .EvaluateObjStr(gameObject.ObjectKind, gameObject.BaseId)
                .Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private unsafe void UpdateLocalMapMarkers()
    {
        var agentMap = AgentMap.Instance();
        if (agentMap == null)
            return;

        var mapVisible = agentMap->IsAddonShown() && !agentMap->IsAddonHidden();
        if (!mapVisible)
            return;

        var signature = this.BuildMapMarkerSignature();
        if (signature == this.requestedMapMarkerSignature)
            return;

        this.requestedMapMarkerSignature = signature;
        agentMap->UpdateFlags |= 2;
    }

    private unsafe void CreateMapMarkersDetour(AgentMap* agentMap, bool omitAetherytes)
    {
        try
        {
            this.InjectLocalMapMarkers(agentMap);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "向地图重建流程注入宝箱标记失败。");
        }

        this.createMapMarkersHook.Original(agentMap, omitAetherytes);
    }

    private unsafe void InjectLocalMapMarkers(AgentMap* agentMap)
    {
        if (this.mapMarkersInjected && agentMap->MapMarkerCount == this.injectedMapMarkerCount)
            agentMap->MapMarkerCount = this.nativeMapMarkerCount;

        this.mapMarkersInjected = false;

        var territoryId = ClientState.TerritoryType;
        var mapId = ClientState.MapId;
        var displayedTerritoryId = agentMap->SelectedTerritoryId != 0
            ? agentMap->SelectedTerritoryId
            : agentMap->CurrentTerritoryId;
        var displayedMapId = agentMap->SelectedMapId != 0
            ? agentMap->SelectedMapId
            : agentMap->CurrentMapId;
        if (!this.configuration.Enabled
            || !this.configuration.ShowChests
            || displayedTerritoryId != territoryId
            || displayedMapId != mapId)
        {
            return;
        }

        if (!DataManager.GetExcelSheet<Map>().TryGetRow(mapId, out var map))
            return;

        var records = this.GetCurrentMapChestRecords();
        this.nativeMapMarkerCount = agentMap->MapMarkerCount;
        foreach (var record in records)
        {
            var mapPosition = this.GetPosition(record);
            mapPosition.X += map.OffsetX;
            mapPosition.Z += map.OffsetY;
            if (!agentMap->AddMapMarker(
                    mapPosition,
                    LocalMapMarkerIconId,
                    scale: 0,
                    text: null,
                    textPosition: 3))
            {
                break;
            }
        }

        this.injectedMapMarkerCount = agentMap->MapMarkerCount;
        this.mapMarkersInjected = this.injectedMapMarkerCount > this.nativeMapMarkerCount;
    }

    private TrackedChestRecord[] GetCurrentMapChestRecords()
        => this.configuration.TrackedChests
            .Where(record =>
                record.TerritoryId == ClientState.TerritoryType
                && record.MapId == ClientState.MapId)
            .OrderBy(record => record.X)
            .ThenBy(record => record.Z)
            .ToArray();

    private int BuildMapMarkerSignature()
        => this.BuildMapMarkerSignature(this.GetCurrentMapChestRecords());

    private int BuildMapMarkerSignature(IEnumerable<TrackedChestRecord> records)
    {
        var signatureBuilder = new HashCode();
        signatureBuilder.Add(ClientState.TerritoryType);
        signatureBuilder.Add(ClientState.MapId);
        signatureBuilder.Add(this.configuration.Enabled);
        signatureBuilder.Add(this.configuration.ShowChests);
        foreach (var record in records)
        {
            signatureBuilder.Add(BitConverter.SingleToInt32Bits(record.X));
            signatureBuilder.Add(BitConverter.SingleToInt32Bits(record.Z));
        }

        return signatureBuilder.ToHashCode();
    }
    private unsafe void RemoveLocalMapMarkers(AgentMap* agentMap)
    {
        if (!this.mapMarkersInjected)
            return;

        if (agentMap->MapMarkerCount == this.injectedMapMarkerCount)
        {
            agentMap->MapMarkerCount = this.nativeMapMarkerCount;
            agentMap->UpdateFlags |= 2;
        }

        this.mapMarkersInjected = false;
    }
    private void DrawWorldMarkers()
    {
        var drawList = ImGui.GetForegroundDrawList();

        foreach (var detected in this.detectedObjects)
        {
            var label = detected.Kind == MarkerKind.Chest ? "◆ 宝箱" : "● 萝卜";
            this.DrawVirtualMarker(
                drawList,
                detected.GameObject.Position,
                detected.Kind,
                detected.Distance,
                label);
        }

        var localPlayer = ObjectTable.LocalPlayer;
        if (!this.configuration.ShowChests || localPlayer == null)
            return;

        var territoryId = ClientState.TerritoryType;
        var mapId = ClientState.MapId;
        foreach (var record in this.configuration.TrackedChests.Where(record =>
                     record.TerritoryId == territoryId && record.MapId == mapId))
        {
            var position = this.GetPosition(record);
            if (this.detectedObjects.Any(detected =>
                    detected.Kind == MarkerKind.Chest
                    && Vector3.DistanceSquared(detected.GameObject.Position, position) < 2.25f))
            {
                continue;
            }

            var distance = Vector3.Distance(localPlayer.Position, position);
            if (distance <= this.configuration.MaxDistance)
                this.DrawVirtualMarker(drawList, position, MarkerKind.Chest, distance, "◆ 宝箱（记录）");
        }
    }

    private void DrawVirtualMarker(
        ImDrawListPtr drawList,
        Vector3 groundPosition,
        MarkerKind kind,
        float distance,
        string label)
    {
        var topPosition = groundPosition + new Vector3(0f, 3f, 0f);
        if (!GameGui.WorldToScreen(groundPosition, out var screenPosition)
            || !GameGui.WorldToScreen(topPosition, out var topScreenPosition))
        {
            return;
        }

        var color = kind == MarkerKind.Chest ? ChestColor : CarrotColor;
        if (this.configuration.ShowDistance)
            label += $"  {distance:F0}m";

        var textSize = ImGui.CalcTextSize(label);
        var textPosition = topScreenPosition - new Vector2(textSize.X / 2f, 24f);

        this.DrawGroundRing(drawList, groundPosition, color);
        drawList.AddLine(screenPosition, topScreenPosition, color, 3f);
        drawList.AddCircleFilled(screenPosition, 7f, color);
        drawList.AddCircle(topScreenPosition, 12f, color, 0, 3f);
        drawList.AddText(textPosition + Vector2.One, ShadowColor, label);
        drawList.AddText(textPosition, TextColor, label);
    }
    private void DrawGroundRing(ImDrawListPtr drawList, Vector3 center, uint color)
    {
        const int segmentCount = 32;
        const float radius = 1.4f;
        Vector2? firstPoint = null;
        Vector2? previousPoint = null;

        for (var index = 0; index <= segmentCount; index++)
        {
            var angle = MathF.Tau * index / segmentCount;
            var worldPoint = center
                             + new Vector3(MathF.Cos(angle) * radius, 0.05f, MathF.Sin(angle) * radius);
            if (!GameGui.WorldToScreen(worldPoint, out var screenPoint))
            {
                previousPoint = null;
                continue;
            }

            firstPoint ??= screenPoint;
            if (previousPoint.HasValue)
                drawList.AddLine(previousPoint.Value, screenPoint, color, 3f);
            previousPoint = screenPoint;
        }

        if (firstPoint.HasValue && previousPoint.HasValue)
            drawList.AddLine(previousPoint.Value, firstPoint.Value, color, 3f);
    }
    private void DrawWindow()
    {
        if (!this.windowOpen)
            return;

        ImGui.SetNextWindowSizeConstraints(new Vector2(460f, 260f), new Vector2(900f, 800f));
        if (!ImGui.Begin("新月岛宝藏标记###CrescentMarkers", ref this.windowOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = this.configuration.Enabled;
        if (ImGui.Checkbox("启用屏幕标记", ref enabled))
        {
            this.configuration.Enabled = enabled;
            this.Save();
        }

        var showChests = this.configuration.ShowChests;
        if (ImGui.Checkbox("标记宝箱", ref showChests))
        {
            this.configuration.ShowChests = showChests;
            this.Save();
        }

        ImGui.SameLine();
        var showCarrots = this.configuration.ShowCarrots;
        if (ImGui.Checkbox("标记萝卜", ref showCarrots))
        {
            this.configuration.ShowCarrots = showCarrots;
            this.Save();
        }

        var showDistance = this.configuration.ShowDistance;
        if (ImGui.Checkbox("显示距离", ref showDistance))
        {
            this.configuration.ShowDistance = showDistance;
            this.Save();
        }

        var echoNewDetections = this.configuration.EchoNewDetections;
        if (ImGui.Checkbox("发现新目标时在默语频道回显坐标", ref echoNewDetections))
        {
            this.configuration.EchoNewDetections = echoNewDetections;
            this.Save();
        }

        var maxDistance = this.configuration.MaxDistance;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderFloat("最大标记距离", ref maxDistance, 20f, 300f, "%.0f 米"))
        {
            this.configuration.MaxDistance = maxDistance;
            this.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"已发现：{this.detectedObjects.Count}");

        if (ImGui.BeginTable("DetectedObjects", 4))
        {
            ImGui.TableSetupColumn("类型");
            ImGui.TableSetupColumn("名称");
            ImGui.TableSetupColumn("距离");
            ImGui.TableSetupColumn("操作");
            ImGui.TableHeadersRow();

            foreach (var detected in this.detectedObjects)
            {
                ImGui.PushID($"{detected.GameObject.GameObjectId}-{detected.GameObject.ObjectIndex}");
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(detected.Kind == MarkerKind.Chest ? "宝箱" : "萝卜");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(this.GetObjectName(detected.GameObject));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{detected.Distance:F1}m");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("回显坐标"))
                    this.EchoCoordinates(detected);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        this.DrawTrackedChestRecords();

        ImGui.Separator();
        var showScanner = this.configuration.ShowScanner;
        if (ImGui.Checkbox("显示附近对象扫描器（用于适配新对象）", ref showScanner))
        {
            this.configuration.ShowScanner = showScanner;
            this.Save();
        }

        if (this.configuration.ShowScanner)
            this.DrawScanner();

        ImGui.TextWrapped("点击“回显坐标”可在自己的默语频道再次输出可点击坐标。");
        ImGui.TextUnformatted("命令：/ocmark");
        ImGui.End();
    }

    private void DrawTrackedChestRecords()
    {
        var territoryId = ClientState.TerritoryType;
        var mapId = ClientState.MapId;
        var records = this.configuration.TrackedChests
            .Where(record => record.TerritoryId == territoryId && record.MapId == mapId)
            .ToArray();

        ImGui.Separator();
        ImGui.TextUnformatted($"当前区域未开启宝箱记录：{records.Length}");
        ImGui.SameLine();
        if (records.Length > 0 && ImGui.SmallButton("清空宝箱记录"))
        {
            foreach (var record in records)
            {
                this.configuration.TrackedChests.Remove(record);
                this.observedChestRecords.Remove(record);
                this.missingChestSince.Remove(record);
            }
            this.Save();
            return;
        }

        var localPlayer = ObjectTable.LocalPlayer;
        if (records.Length == 0 || localPlayer == null || !ImGui.BeginTable("TrackedChests", 3))
            return;

        ImGui.TableSetupColumn("地图坐标");
        ImGui.TableSetupColumn("距离");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();

        foreach (var record in records)
        {
            var distance = Vector3.Distance(localPlayer.Position, this.GetPosition(record));
            ImGui.PushID($"record-{record.TerritoryId}-{record.MapId}-{record.X}-{record.Z}");
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"X:{record.MapX:F1} Y:{record.MapY:F1}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{distance:F1}m");
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("回显坐标"))
                this.EchoCoordinates(record, distance);
            ImGui.SameLine();
            if (ImGui.SmallButton("删除"))
            {
                this.configuration.TrackedChests.Remove(record);
                this.observedChestRecords.Remove(record);
                this.missingChestSince.Remove(record);
                this.Save();
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
    }
    private void DrawScanner()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        ImGui.TextWrapped("无名对象也会显示。请站在胡萝卜旁，选择距离最近且标为「无名称」的对象，再点「设为萝卜」。");
        ImGui.BeginChild("ObjectScanner", new Vector2(0f, 220f), true);
        foreach (var gameObject in ObjectTable
                     .Where(gameObject =>
                         gameObject.IsValid()
                         && gameObject.Address != localPlayer.Address
                         && gameObject is not IPlayerCharacter)
                     .OrderBy(gameObject =>
                         Vector3.Distance(localPlayer.Position, gameObject.Position))
                     .Take(150))
        {
            var name = this.GetObjectName(gameObject);
            var displayName = name.Length == 0 ? "【无名称】" : name;
            var distance = Vector3.Distance(localPlayer.Position, gameObject.Position);
            ImGui.PushID($"scan-{gameObject.GameObjectId}-{gameObject.ObjectIndex}");
            ImGui.TextUnformatted(
                $"{distance,5:F1}m  {displayName}  BaseId:{gameObject.BaseId}  实体:{gameObject.EntityId:X8}  Owner:{gameObject.OwnerId:X8}  索引:{gameObject.ObjectIndex}  {gameObject.ObjectKind}  可选:{gameObject.IsTargetable}");
            ImGui.SameLine();
            if (ImGui.SmallButton("设为宝箱"))
                this.AddCustomObject(gameObject, MarkerKind.Chest);
            ImGui.SameLine();
            if (ImGui.SmallButton("设为萝卜"))
                this.AddCustomObject(gameObject, MarkerKind.Carrot);
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private void AddCustomObject(IGameObject gameObject, MarkerKind kind)
    {
        if (gameObject.BaseId == 0)
        {
            ChatGui.PrintError("该对象的 BaseId 为 0，暂时无法作为同类对象持久识别。请截图这一行的完整信息。", "新月标记");
            return;
        }

        var target = kind == MarkerKind.Chest
            ? this.configuration.ChestBaseIds
            : this.configuration.CarrotBaseIds;
        var other = kind == MarkerKind.Chest
            ? this.configuration.CarrotBaseIds
            : this.configuration.ChestBaseIds;

        other.Remove(gameObject.BaseId);
        if (!target.Contains(gameObject.BaseId))
            target.Add(gameObject.BaseId);

        this.Save();
        var objectName = this.GetObjectName(gameObject);
        ChatGui.Print(
            $"已将{(objectName.Length == 0 ? "无名对象" : objectName)}（BaseId:{gameObject.BaseId}）设为{(kind == MarkerKind.Chest ? "宝箱" : "萝卜")}。",
            "新月标记");
    }

    private void AnnounceNewObjects()
    {
        if (!this.configuration.EchoNewDetections)
            return;

        foreach (var detected in this.detectedObjects)
        {
            if (this.announcedObjects.Add(detected.GameObject.GameObjectId))
                this.EchoCoordinates(detected);
        }
    }

    private void EchoCoordinates(DetectedObject detected)
    {
        try
        {
            var mapCoordinates = detected.GameObject.GetMapCoordinates();
            var label = detected.Kind == MarkerKind.Chest ? "宝箱" : "萝卜";
            this.PrintCoordinates(
                label,
                ClientState.TerritoryType,
                ClientState.MapId,
                mapCoordinates.X,
                mapCoordinates.Y,
                detected.Distance);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "输出新月岛宝藏坐标失败。");
            ChatGui.PrintError("无法输出该对象的地图坐标。", "新月标记");
        }
    }

    private void EchoCoordinates(TrackedChestRecord record, float distance)
        => this.PrintCoordinates(
            "宝箱（记录）",
            record.TerritoryId,
            record.MapId,
            record.MapX,
            record.MapY,
            distance);

    private void PrintCoordinates(
        string label,
        uint territoryId,
        uint mapId,
        float mapX,
        float mapY,
        float distance)
    {
        var message = new SeStringBuilder()
            .AddText($"[新月标记] {label}  距离 {distance:F1}m  （")
            .Build();
        var coordinateLink = SeString.CreateMapLink(territoryId, mapId, mapX, mapY);
        var closingParenthesis = new SeStringBuilder()
            .AddText("）")
            .Build();

        message.Append(coordinateLink);
        message.Append(closingParenthesis);

        ChatGui.Print(
            new XivChatEntry
            {
                Type = XivChatType.Echo,
                Message = message,
                Silent = true,
            });
    }
    private void Save()
        => this.configuration.Save(PluginInterface);

    private enum MarkerKind
    {
        None,
        Chest,
        Carrot,
    }

    private readonly record struct DetectedObject(
        IGameObject GameObject,
        MarkerKind Kind,
        float Distance);
}
