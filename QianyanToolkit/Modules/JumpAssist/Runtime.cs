using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using BGCollisionModule = FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule;
using NativeRaycastHit = FFXIVClientStructs.FFXIV.Common.Component.BGCollision.RaycastHit;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using RaptureAtkModule = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule;
using NativeVector2 = FFXIVClientStructs.FFXIV.Common.Math.Vector2;
using NativeVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using QToolKit.Core;

namespace QToolKit.Modules.JumpAssist;

internal sealed class Runtime : IDisposable
{
    private const string Command = "/jumpassist";
    private const int TriggerVirtualKey = 0x78; // F9
    private const int CancelVirtualKey = 0x23; // End
    private const int MinimumStableSpeedWindowMilliseconds = 90;
    private const int MaximumSpeedWindowMilliseconds = 140;
    private const int RunUpMovementTimeoutMilliseconds = 450;
    private const int MaximumAttemptMilliseconds = 2600;
    private const float MinimumRunUpDisplacement = 0.08f;
    private const float MaximumRunUpLateralError = 0.35f;
    private const float MinimumTargetDistance = 0.8f;
    private const float MaximumTargetDistance = 10f;
    private const float MaximumTargetHeightDifference = 4f;
    private const float MaximumHorizontalSpeed = 6f;
    private const float JumpInitialVerticalSpeed = 8.50f;
    private const float Gravity = 20f;
    // The native Jumping condition reports about 0.835 s on a flat jump. The
    // 8.50/20.0 vertical parabola has the observed ~1.81 yalm apex; this small
    // timing factor aligns its 0.850 s mathematical flight with client frames.
    private const float ObservedFlightTimeScale = 0.9824f;
    private const float ObservedAirSpeedScale = 1.0205f;
    private const float MaximumJumpRise = JumpInitialVerticalSpeed * JumpInitialVerticalSpeed / (2f * Gravity);
    private const float LandingLedgeCaptureAllowance = 0.42f;
    private const float MaximumReachableLandingRise = MaximumJumpRise + LandingLedgeCaptureAllowance;
    private const string UpdatePlayerWalkSpeedSignature = "40 53 48 83 EC 50 80 79 3C 00";
    private const string ReadWalkInputSignature = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";
    private const float TargetAdjustmentSpeed = 0.75f;
    private const float ObserverTargetSpeed = 2.50f;
    private const float PreciseTargetAdjustmentSpeed = 0.20f;
    // GameObject.HitboxRadius is the target/combat hitbox (normally 0.50 for a
    // player), not the radius used by character movement against background
    // geometry.  Using it here made the planner substantially wider than the
    // player controller.  Use a conservative movement-envelope estimate while
    // GetHeight still supplies the race/scale-dependent height.
    private const float MovementCollisionRadius = 0.30f;
    // A hit inside this core is treated as a definite controller collision.
    // Hits which exist only in the larger movement envelope are reported as a
    // clearance risk instead of incorrectly turning the whole route green.
    private const float MovementCollisionCoreRadius = 0.16f;
    // Character-blocking geometry is split across several scene layers.  The
    // 0x4000 material filter below selects solid character terrain; restricting
    // the layer mask to 1 (as the generic convenience raycast does) misses some
    // jump-puzzle models, so query every layer and let the material decide.
    private const int CharacterTerrainCollisionLayer = -1;

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IGameConfig GameConfig { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IGameInteropProvider GameInteropProvider { get; set; } = null!;
    [PluginService] private static IKeyState KeyState { get; set; } = null!;

    private readonly Configuration configuration;
    private bool windowOpen;
    private bool triggerWasDown;
    private bool cancelWasDown;
    private bool candidateValid;
    private bool targetSet;
    private bool adjustmentMode;
    private bool observerMode;
    private bool targetSurfaceSnapped;
    private bool snapWasDown;
    private TargetArrivalMode targetArrivalMode = TargetArrivalMode.ZeroHorizontalSpeed;
    private bool targetArrivalHandled;
    private long lastAdjustmentAt;
    private Vector3 candidate;
    private Vector3 target;
    private Vector3 targetRayOrigin;
    private Vector3 targetRayDirection;
    private float targetHeight;
    private Vector3 planOrigin;
    private float attemptLandingAllowance;
    private readonly List<Vector3> safetyBoundary = [];
    private Vector3 attemptStartPosition;
    private Vector2 targetDirection;
    private AttemptState state;
    private long attemptStartedAt;
    private long jumpTriggeredAt;
    private long airborneStartedAt;
    private readonly Queue<SpeedSample> speedSamples = [];
    private double currentSpeedWindowMilliseconds;
    private float forwardSpeed;
    private float requiredTakeoffSpeed;
    private float appliedTakeoffSpeed;
    private float attemptTargetDistance;
    private float takeoffForwardProgress;
    private float movementInputScale;
    private float gamepadDeadZone;
    private uint previousGamepadMode;
    private bool restoreGamepadMode;
    private float lastCameraReferenceDegrees;
    private ViGEmClient? virtualGamepadClient;
    private IXbox360Controller? virtualGamepad;
    private bool virtualGamepadConnected;
    private Hook<UpdatePlayerWalkSpeedDelegate>? updatePlayerWalkSpeedHook;
    private Hook<ReadWalkInputDelegate>? readWalkInputHook;
    private bool nativeMovementActive;
    private bool nativeDirectionDiagnosticPending;
    private bool nativeDirectionDiagnosticLogged;
    private bool lastInjectedLegacyMode;
    private float lastInjectedWorldDegrees;
    private float lastInjectedReferenceDegrees;
    private float lastInjectedRelativeDegrees;
    private float lastInjectedLeft;
    private float lastInjectedForward;
    private Hook<GetCameraPositionDelegate>? getCameraPositionHook;
    private bool speedOverrideActive;
    private volatile bool speedOverrideApplied;
    private float desiredSpeedOverride;
    private float appliedBaseMovementSpeed;
    private nint walkControllerAddress;
    private int plannedForwardMilliseconds;
    private string status = "将鼠标指向落点，执行 /jumpassist go。";

    public Runtime(ModuleContext context, Configuration configuration)
    {
        PluginInterface = context.PluginInterface;
        CommandManager = context.CommandManager;
        Framework = context.Framework;
        ObjectTable = context.ObjectTable;
        ClientState = context.ClientState;
        Condition = context.Condition;
        ChatGui = context.ChatGui;
        GameConfig = context.GameConfig;
        Log = context.Log;
        GameInteropProvider = context.GameInteropProvider;
        KeyState = context.KeyState;
        this.configuration = configuration;
        CommandManager.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "observe：自由三维选点；snap：可选贴合；mode fall/zero：到点方式；go [fall|zero]：跳跃；debug on/off：调试输出。",
            ShowInHelp = false,
        });
        Framework.Update += this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += this.Draw;
    }

    public void Dispose()
    {
        this.StopAttempt("插件已停止。");
        this.DisableObserverCameraLock();
        this.getCameraPositionHook?.Dispose();
        this.getCameraPositionHook = null;
        this.updatePlayerWalkSpeedHook?.Dispose();
        this.updatePlayerWalkSpeedHook = null;
        this.readWalkInputHook?.Dispose();
        this.readWalkInputHook = null;
        this.DisposeVirtualGamepad();
        PluginInterface.UiBuilder.Draw -= this.Draw;
        Framework.Update -= this.OnFrameworkUpdate;
        CommandManager.RemoveHandler(Command);
    }

    private void OnCommand(string _, string arguments)
    {
        var normalizedArguments = arguments.Trim().ToLowerInvariant();
        if (normalizedArguments.StartsWith("height ", StringComparison.Ordinal))
        {
            this.ChangeTargetHeight(normalizedArguments[7..]);
            return;
        }

        switch (normalizedArguments)
        {
            case "set":
                if (this.state != AttemptState.Idle)
                {
                    this.ReportStatus("跳跃执行中，不能修改固定目标。", true);
                    return;
                }
                var planningPlayer = ObjectTable.LocalPlayer;
                if (planningPlayer == null)
                {
                    this.ReportStatus("当前角色不可用，无法固定规划起点。", true);
                    return;
                }
                if (!TryGetScreenRay(ImGui.GetMousePos(), out this.targetRayOrigin, out this.targetRayDirection))
                {
                    this.ReportStatus("无法取得鼠标射线，目标未修改。", true);
                    return;
                }
                this.planOrigin = planningPlayer.Position;
                if (TryRaycastAllLayers(this.targetRayOrigin, this.targetRayDirection, 200f,
                        out var visualSurface, out var surfaceNormal)
                    && Vector3.Distance(this.targetRayOrigin, visualSurface) > 0.5f)
                {
                    this.target = surfaceNormal.Y < 0.45f
                        && TryFindTopSurface(visualSurface, out var topSurface)
                            ? topSurface
                            : visualSurface;
                    this.targetHeight = this.target.Y;
                }
                else
                {
                    this.targetHeight = planningPlayer.Position.Y;
                    if (!this.UpdateTargetFromRay())
                    {
                        this.ReportStatus("鼠标射线无法与当前高度平面相交；请调整视角后重试。", true);
                        return;
                    }
                }
                this.targetSet = true;
                this.observerMode = false;
                this.targetSurfaceSnapped = true;
                this.SetAdjustmentMode(false, false);
                this.ReportStatus($"已固定规划起点 X{this.planOrigin.X:F2} Y{this.planOrigin.Y:F2} Z{this.planOrigin.Z:F2}，目标 X{this.target.X:F2} Y{this.target.Y:F2} Z{this.target.Z:F2}；可执行 /jumpassist adjust 进入键盘微调，或直接执行 /jumpassist go。", true);
                break;
            case "go fall":
                this.targetArrivalMode = TargetArrivalMode.NaturalFall;
                goto case "go";
            case "go zero":
                this.targetArrivalMode = TargetArrivalMode.ZeroHorizontalSpeed;
                goto case "go";
            case "go":
                if (this.state != AttemptState.Idle)
                {
                    this.ReportStatus("已有一次跳跃正在执行；请先使用 /jumpassist stop。", true);
                    return;
                }
                this.SetAdjustmentMode(false, false);
                this.BeginAttempt(true);
                break;
            case "clear":
                if (this.state != AttemptState.Idle)
                {
                    this.ReportStatus("跳跃执行中，不能清除固定目标。", true);
                    return;
                }
                this.targetSet = false;
                this.observerMode = false;
                this.targetSurfaceSnapped = false;
                this.SetAdjustmentMode(false, false);
                this.ReportStatus("已清除固定目标。", true);
                break;
            case "adjust":
                this.SetAdjustmentMode(true, true);
                break;
            case "adjust on":
                this.SetAdjustmentMode(true, true);
                break;
            case "adjust off":
                this.SetAdjustmentMode(false, true);
                break;
            case "done":
                this.SetAdjustmentMode(false, true);
                break;
            case "observe":
            case "observer":
                this.BeginObserverSelection();
                break;
            case "snap":
                this.TrySnapTargetToSurfaceBelow(true);
                break;
            case "mode fall":
            case "mode natural":
                this.targetArrivalMode = TargetArrivalMode.NaturalFall;
                this.ReportStatus("到点方式已设为 fall：经过目标位置后释放移动控制，自然下落。", true);
                break;
            case "mode zero":
            case "mode stop":
                this.targetArrivalMode = TargetArrivalMode.ZeroHorizontalSpeed;
                this.ReportStatus("到点方式已设为 zero：经过目标位置后停止水平移动，垂直方向继续自然下落。", true);
                break;
            case "boundary":
            case "boundary add":
                this.AddBoundaryPoint();
                break;
            case "boundary undo":
                if (this.state != AttemptState.Idle)
                {
                    this.ReportStatus("跳跃执行中，不能修改安全边界。", true);
                    return;
                }
                if (this.safetyBoundary.Count > 0)
                    this.safetyBoundary.RemoveAt(this.safetyBoundary.Count - 1);
                this.ReportStatus($"已撤销边界点；当前 {this.safetyBoundary.Count} 个点。", true);
                break;
            case "boundary clear":
                if (this.state != AttemptState.Idle)
                {
                    this.ReportStatus("跳跃执行中，不能修改安全边界。", true);
                    return;
                }
                this.safetyBoundary.Clear();
                this.ReportStatus("已清除安全边界。", true);
                break;
            case "stop":
                this.StopAttempt("已手动中止。");
                this.ReportStatus(this.status, true);
                break;
            case "status":
                this.ReportStatus(this.status, true);
                break;
            case "debug":
            case "debug on":
                this.configuration.DebugMode = true;
                this.Save();
                this.ReportStatus("调试模式已开启，详细状态将显示在 /e 并写入日志。", true);
                break;
            case "debug off":
                this.configuration.DebugMode = false;
                this.Save();
                this.status = "调试模式已关闭。";
                break;
            default:
                this.windowOpen = true;
                break;
        }
    }

    public void OpenWindow() => this.windowOpen = true;

    private void OnFrameworkUpdate(IFramework _)
    {
        this.UpdateTargetAdjustment();
        var triggerDown = IsPhysicalKeyDown(TriggerVirtualKey);
        var cancelDown = IsPhysicalKeyDown(CancelVirtualKey);
        if (cancelDown && !this.cancelWasDown)
        {
            this.StopAttempt("已用 End 中止。");
            this.ReportStatus(this.status, true);
        }
        if (triggerDown && !this.triggerWasDown && this.state == AttemptState.Idle)
        {
            this.SetAdjustmentMode(false, false);
            this.BeginAttempt(true);
        }
        this.triggerWasDown = triggerDown;
        this.cancelWasDown = cancelDown;

        if (this.state != AttemptState.Idle)
            this.UpdateAttempt();
    }

    private void BeginAttempt(bool notifyChat)
    {
        var player = ObjectTable.LocalPlayer;
        if (!this.targetSet || player == null)
        {
            this.ReportStatus("尚未固定目标；请先将鼠标指向落点并执行 /jumpassist set。", notifyChat);
            return;
        }
        if (!ClientState.IsLoggedIn)
        {
            this.ReportStatus("当前角色尚未登录，不能执行跳跃。", notifyChat);
            return;
        }
        if (ClientState.IsPvP)
        {
            this.ReportStatus("PvP 区域内不能执行跳跃。", notifyChat);
            return;
        }
        if (ClientState.IsGPosing)
        {
            this.ReportStatus("集体动作模式中不能执行跳跃。", notifyChat);
            return;
        }
        if (!ClientState.IsClientIdle(out var blockingFlag))
        {
            this.ReportStatus($"当前角色状态不允许执行：{blockingFlag}。", notifyChat);
            return;
        }
        if (Condition[ConditionFlag.Jumping] || Condition[ConditionFlag.InFlight] || Condition[ConditionFlag.Swimming])
        {
            this.ReportStatus("请在地面静止后再执行。", notifyChat);
            return;
        }

        var originOffset = player.Position - this.planOrigin;
        var horizontalOriginOffset = new Vector2(originOffset.X, originOffset.Z).Length();
        if (horizontalOriginOffset > 0.15f || MathF.Abs(originOffset.Y) > 0.20f)
        {
            this.ReportStatus($"角色已离开固定规划起点 {horizontalOriginOffset:F2} yalm；请在当前站位重新执行 /jumpassist set。", notifyChat);
            return;
        }

        var delta = this.target - player.Position;
        var horizontalDistance = new Vector2(delta.X, delta.Z).Length();
        if (horizontalDistance < MinimumTargetDistance || horizontalDistance > MaximumTargetDistance)
        {
            this.ReportStatus($"水平距离 {horizontalDistance:F2} yalms，超出允许范围。", notifyChat);
            return;
        }
        if (MathF.Abs(delta.Y) > MaximumTargetHeightDifference)
        {
            this.ReportStatus($"高度差 {delta.Y:+0.00;-0.00;0.00} yalms，超出允许范围。", notifyChat);
            return;
        }

        if (this.safetyBoundary.Count >= 3
            && !IsInsideBoundary(new Vector2(player.Position.X, player.Position.Z), this.safetyBoundary))
        {
            this.ReportStatus("当前站位位于安全边界之外；拒绝执行原地起跳。", notifyChat);
            return;
        }
        var (characterHeight, collisionRadius) = GetPlayerCollisionDimensions(player.Address);
        var allowLedgeCapture = !this.observerMode || this.targetSurfaceSnapped;
        var executionPrediction = PredictTrajectory(
            player.Position, this.target, characterHeight, collisionRadius, this.safetyBoundary, allowLedgeCapture);
        if (!executionPrediction.HeadClear)
        {
            this.ReportStatus($"规划轨迹存在顶头风险：碰撞点 X{executionPrediction.CollisionPoint.X:F2} Y{executionPrediction.CollisionPoint.Y:F2} Z{executionPrediction.CollisionPoint.Z:F2}；已拒绝执行。", notifyChat);
            return;
        }
        if (!executionPrediction.PathClear)
        {
            var collisionPart = executionPrediction.CollisionKind == TrajectoryCollisionKind.Feet ? "脚部" : "身体";
            this.ReportStatus($"规划轨迹的{collisionPart}碰撞体会撞到角色地形层：碰撞点 X{executionPrediction.CollisionPoint.X:F2} Y{executionPrediction.CollisionPoint.Y:F2} Z{executionPrediction.CollisionPoint.Z:F2}，法线 Y {executionPrediction.CollisionNormal.Y:F2}，移动半径 {executionPrediction.CollisionRadius:F2}；目标 X{this.target.X:F2} Y{this.target.Y:F2} Z{this.target.Z:F2}，已拒绝执行。", notifyChat);
            return;
        }
        if (executionPrediction.ClearanceWarning)
        {
            var warningPart = executionPrediction.WarningCollisionKind switch
            {
                TrajectoryCollisionKind.Feet => "脚部",
                TrajectoryCollisionKind.Head => "头部上沿",
                _ => "身体",
            };
            var warningReason = executionPrediction.WarningCollisionKind == TrajectoryCollisionKind.Head
                ? "视觉模型头部上沿命中，但下层移动核心未命中"
                : $"仅安全外包络或备用材质命中，{MovementCollisionCoreRadius:F2} 核心未确认阻挡";
            this.ReportStatus($"规划轨迹存在{warningPart}风险：X{executionPrediction.WarningCollisionPoint.X:F2} Y{executionPrediction.WarningCollisionPoint.Y:F2} Z{executionPrediction.WarningCollisionPoint.Z:F2}；{warningReason}；允许执行。", notifyChat);
        }
        this.attemptStartPosition = player.Position;
        this.targetDirection = Vector2.Normalize(new Vector2(delta.X, delta.Z));
        this.requiredTakeoffSpeed = executionPrediction.TakeoffSpeed;
        this.attemptLandingAllowance = executionPrediction.LandingAllowance;
        if (!float.IsFinite(this.requiredTakeoffSpeed) || this.requiredTakeoffSpeed > MaximumHorizontalSpeed)
        {
            var takeoffHeightDifference = delta.Y;
            var maximumReachableRise = executionPrediction.UsesLedgeCapture
                ? MaximumReachableLandingRise
                : MaximumJumpRise;
            var reason = takeoffHeightDifference > maximumReachableRise
                ? $"目标高于起跳点 {takeoffHeightDifference:F2} yalm，超过当前模式弹道上限 {maximumReachableRise:F2} yalm"
                : $"所需水平速度超过 {MaximumHorizontalSpeed:F2} yalm/s";
            this.ReportStatus($"轨迹模型判定当前落点不可达：{reason}。", notifyChat);
            return;
        }
        this.plannedForwardMilliseconds = (int)MathF.Round(executionPrediction.FlightTime * 1000f);
        this.forwardSpeed = 0f;
        this.currentSpeedWindowMilliseconds = 0d;
        this.speedSamples.Clear();
        this.attemptStartedAt = Stopwatch.GetTimestamp();
        this.speedSamples.Enqueue(new SpeedSample(this.attemptStartedAt, 0f));
        this.jumpTriggeredAt = 0;
        this.airborneStartedAt = 0;
        this.attemptTargetDistance = horizontalDistance;
        this.takeoffForwardProgress = 0f;
        this.appliedTakeoffSpeed = 0f;
        this.targetArrivalHandled = false;
        this.movementInputScale = Math.Clamp(
            this.requiredTakeoffSpeed / MaximumHorizontalSpeed,
            0.05f,
            1f);
        // Prepare the planned direction before starting the jump so the
        // character leaves the original point without an extra run-up step.
        this.desiredSpeedOverride = 0f;
        this.speedOverrideApplied = false;
        this.gamepadDeadZone = GameConfig.TryGet(SystemConfigOption.DeadArea, out float configuredDeadZone)
            ? Math.Clamp(configuredDeadZone, 0f, 0.95f)
            : 0f;
        if (!this.StartMovementOverride(out var movementError))
        {
            this.ReportStatus(movementError, notifyChat);
            return;
        }
        this.state = AttemptState.RunUp;
        var ledgeCaptureText = executionPrediction.UsesLedgeCapture && delta.Y > 0f
            ? $"；已自动选择 {executionPrediction.LandingAllowance:F2} yalm 落台捕获弹道"
            : string.Empty;
        var arrivalModeText = this.targetArrivalMode == TargetArrivalMode.ZeroHorizontalSpeed
            ? "到点后水平零速"
            : "到点后释放并自然下落";
        this.ReportStatus(
            $"跳跃控制已启动；目标 X{this.target.X:F2} Y{this.target.Y:F2} Z{this.target.Z:F2}；距离 {horizontalDistance:F2}，高度差 {delta.Y:+0.00;-0.00;0.00}；起跳速度 {this.requiredTakeoffSpeed:F2} yalm/s{ledgeCaptureText}；{arrivalModeText}；相机基准 {this.lastCameraReferenceDegrees:F1}°{(this.restoreGamepadMode ? "；已临时启用游戏手柄" : string.Empty)}。",
            notifyChat);
    }

    private void UpdateAttempt()
    {
        this.FlushNativeDirectionDiagnostic();
        var player = ObjectTable.LocalPlayer;
        if (player == null || !ClientState.IsLoggedIn)
        {
            this.StopAttempt("角色不可用，已中止。");
            return;
        }

        if (!this.targetArrivalHandled && !this.TryApplyMovementDirection(1f))
        {
            this.StopAttempt("方向输入提交失败，已立即停止移动。");
            this.ReportStatus(this.status, true);
            return;
        }

        var elapsedMs = ElapsedMilliseconds(this.attemptStartedAt);
        var effectiveTimeout = Math.Max(
            MaximumAttemptMilliseconds,
            RunUpMovementTimeoutMilliseconds + this.plannedForwardMilliseconds + 1000);
        if (elapsedMs >= effectiveTimeout)
        {
            this.StopAttempt("本次尝试超时，已停止移动。");
            this.ReportStatus(this.status, true);
            return;
        }

        var delta = this.target - player.Position;
        var runUpDelta = player.Position - this.attemptStartPosition;
        var runUpVector = new Vector2(runUpDelta.X, runUpDelta.Z);
        var forwardProgress = Vector2.Dot(runUpVector, this.targetDirection);
        this.UpdateMovementSpeed(forwardProgress);
        switch (this.state)
        {
            case AttemptState.RunUp:
                if (this.safetyBoundary.Count >= 3
                    && !IsInsideBoundary(new Vector2(player.Position.X, player.Position.Z), this.safetyBoundary))
                {
                    this.StopAttempt("角色已越过安全边界，立即中止跳跃。");
                    this.ReportStatus(this.status, true);
                    break;
                }
                if (!this.speedOverrideApplied)
                {
                    if (elapsedMs >= 250d)
                    {
                        this.StopAttempt("移动控制未能及时启动，已安全中止。");
                        this.ReportStatus(this.status, true);
                    }
                    break;
                }
                var remainingDistance = MathF.Max(0.05f, this.attemptTargetDistance - forwardProgress);
                var remainingHeight = this.target.Y - player.Position.Y;
                var correctedSpeed = CalculateRequiredTakeoffSpeedForAllowance(
                        remainingDistance, remainingHeight, this.attemptLandingAllowance)
                    / ObservedAirSpeedScale;
                if (!float.IsFinite(correctedSpeed)
                    || correctedSpeed > this.appliedBaseMovementSpeed
                    || !this.TryWriteCurrentSpeed(correctedSpeed))
                {
                    this.StopAttempt("无法应用起跳速度，已安全中止。");
                    this.ReportStatus(this.status, true);
                    return;
                }
                this.desiredSpeedOverride = correctedSpeed;
                this.appliedTakeoffSpeed = correctedSpeed;
                if (!this.TriggerJump())
                {
                    this.StopAttempt("游戏未接受跳跃动作，已停止移动控制。");
                    this.ReportStatus(this.status, true);
                    return;
                }
                this.jumpTriggeredAt = Stopwatch.GetTimestamp();
                this.takeoffForwardProgress = forwardProgress;
                this.state = AttemptState.JumpTriggered;
                this.ReportStatus(
                    $"已按规划方向起跳：规划速度 {this.requiredTakeoffSpeed:F2}，实际起跳速度 {this.desiredSpeedOverride:F2}，当前速度上限 {this.appliedBaseMovementSpeed:F2} yalm/s；起跳前位移 {forwardProgress:F3} yalm。",
                    true);
                break;

            case AttemptState.JumpTriggered:
                if (Condition[ConditionFlag.Jumping])
                {
                    this.airborneStartedAt = Stopwatch.GetTimestamp();
                    this.state = AttemptState.Airborne;
                    var arrivalText = this.targetArrivalMode == TargetArrivalMode.ZeroHorizontalSpeed
                        ? "经过三维目标后水平零速下落"
                        : "经过三维目标后释放控制并自然下落";
                    this.status = $"已离地；保持水平速度 {this.desiredSpeedOverride:F2} yalm/s，随后{arrivalText}。";
                }
                else if (ElapsedMilliseconds(this.jumpTriggeredAt) >= 350)
                {
                    this.StopAttempt("未检测到角色起跳，已停止移动。");
                    this.ReportStatus(this.status, true);
                }
                break;

            case AttemptState.Airborne:
                if (!this.targetArrivalHandled && forwardProgress >= this.attemptTargetDistance)
                    this.HandleTargetArrival(player.Position, forwardProgress);
                if (!Condition[ConditionFlag.Jumping])
                {
                    var takeoffSpeed = this.appliedTakeoffSpeed;
                    var landingSpeedCleared = this.TryWriteCurrentSpeed(0f);
                    this.NeutralizeVirtualGamepad();
                    var error = new Vector2(delta.X, delta.Z).Length();
                    var landingDelta = player.Position - this.attemptStartPosition;
                    var landingVector = new Vector2(landingDelta.X, landingDelta.Z);
                    var landingForward = Vector2.Dot(landingVector, this.targetDirection);
                    var signedForwardError = landingForward - this.attemptTargetDistance;
                    var landingLateral = Cross(landingVector, this.targetDirection);
                    var flightMilliseconds = ElapsedMilliseconds(this.airborneStartedAt);
                    var airForwardDistance = landingForward - this.takeoffForwardProgress;
                    var observedAirSpeed = flightMilliseconds > 1d
                        ? airForwardDistance / (float)(flightMilliseconds / 1000d)
                        : 0f;
                    this.StopAttempt($"已落地并{(landingSpeedCleared ? "停止水平移动" : "归零方向输入")}；距落点 {error:F2} yalm；纵向误差 {signedForwardError:+0.00;-0.00;0.00}（正数=跳过），侧偏 {landingLateral:+0.00;-0.00;0.00}；起跳前位移 {this.takeoffForwardProgress:F3} yalm，目标速度 {takeoffSpeed:F2} yalm/s，空中均速 {observedAirSpeed:F2} yalm/s，滞空 {flightMilliseconds:F0}ms。");
                    this.ReportStatus(this.status, true);
                }
                break;
        }
    }

    private void PlanJump(float horizontalDistance, float heightDifference, bool allowLedgeCapture)
    {
        this.plannedForwardMilliseconds = (int)MathF.Round(EstimateFlightTime(heightDifference, allowLedgeCapture) * 1000f);
    }

    private static float CalculateRequiredTakeoffSpeed(
        float horizontalDistance,
        float heightDifference,
        bool allowLedgeCapture = true)
        => CalculateRequiredTakeoffSpeedForAllowance(
            horizontalDistance,
            heightDifference,
            allowLedgeCapture ? LandingLedgeCaptureAllowance : 0f);

    private static float CalculateRequiredTakeoffSpeedForAllowance(
        float horizontalDistance,
        float heightDifference,
        float landingAllowance)
    {
        var flightTime = EstimateFlightTimeForAllowance(heightDifference, landingAllowance);
        return float.IsFinite(flightTime) && flightTime > 0f
            ? Math.Max(0.10f, horizontalDistance / flightTime)
            : float.PositiveInfinity;
    }

    private static float EstimateFlightTime(float heightDifference, bool allowLedgeCapture = true)
        => EstimateFlightTimeForAllowance(
            heightDifference,
            allowLedgeCapture ? LandingLedgeCaptureAllowance : 0f);

    private static float EstimateFlightTimeForAllowance(float heightDifference, float landingAllowance)
    {
        // On an upward landing the client's collision controller can capture a
        // ledge while the feet are still slightly below its top. Model the
        // ballistic contact height instead of demanding that the foot arc itself
        // reaches the platform's exact Y coordinate.
        var ballisticHeightDifference = heightDifference > 0f
            ? MathF.Max(0f, heightDifference - Math.Clamp(landingAllowance, 0f, LandingLedgeCaptureAllowance))
            : heightDifference;
        var discriminant = (JumpInitialVerticalSpeed * JumpInitialVerticalSpeed)
            - (2f * Gravity * ballisticHeightDifference);
        return discriminant >= 0f
            ? ((JumpInitialVerticalSpeed + MathF.Sqrt(discriminant)) / Gravity) * ObservedFlightTimeScale
            : float.PositiveInfinity;
    }

    private unsafe bool StartMovementOverride(out string error)
    {
        try
        {
            this.readWalkInputHook ??=
                GameInteropProvider.HookFromSignature<ReadWalkInputDelegate>(
                    ReadWalkInputSignature,
                    this.ReadWalkInputDetour);
            this.updatePlayerWalkSpeedHook ??=
                GameInteropProvider.HookFromSignature<UpdatePlayerWalkSpeedDelegate>(
                    UpdatePlayerWalkSpeedSignature,
                    this.UpdatePlayerWalkSpeedDetour);
            this.nativeMovementActive = true;
            this.nativeDirectionDiagnosticPending = false;
            this.nativeDirectionDiagnosticLogged = false;
            this.speedOverrideApplied = false;
            this.speedOverrideActive = true;
            this.readWalkInputHook.Enable();
            this.updatePlayerWalkSpeedHook.Enable();
        }
        catch (Exception exception)
        {
            this.nativeMovementActive = false;
            this.readWalkInputHook?.Disable();
            this.speedOverrideActive = false;
            this.updatePlayerWalkSpeedHook?.Disable();
            if (!this.StartVirtualGamepadFallback(out error))
            {
                error = $"原生移动方向 Hook 初始化失败（{exception.GetType().Name}），且旧移动方案不可用：{error}";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private unsafe void ReadWalkInputDetour(
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte* haveBackwardOrStrafe,
        byte* unknown,
        byte additiveInput)
    {
        this.readWalkInputHook!.Original(
            self,
            sumLeft,
            sumForward,
            sumTurnLeft,
            haveBackwardOrStrafe,
            unknown,
            additiveInput);

        if (!this.nativeMovementActive
            || additiveInput != 0
            || sumLeft == null
            || sumForward == null)
            return;

        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return;

        var desiredWorldAngle = MathF.Atan2(this.targetDirection.X, this.targetDirection.Y);
        var legacyMode = GameConfig.TryGet(UiControlOption.MoveMode, out uint moveMode) && moveMode == 1;
        float referenceAngle;
        if (legacyMode)
        {
            var cameraManager = GameCameraManager.Instance();
            var camera = cameraManager == null ? null : cameraManager->GetActiveCamera();
            if (camera == null)
                return;
            referenceAngle = camera->DirH + MathF.PI;
        }
        else
        {
            referenceAngle = player.Rotation;
        }

        var relativeDirection = desiredWorldAngle - referenceAngle;
        this.lastCameraReferenceDegrees = NormalizeDegrees(referenceAngle * (180f / MathF.PI));
        *sumLeft = MathF.Sin(relativeDirection);
        *sumForward = MathF.Cos(relativeDirection);
        if (!this.nativeDirectionDiagnosticLogged)
        {
            this.lastInjectedLegacyMode = legacyMode;
            this.lastInjectedWorldDegrees = NormalizeDegrees(desiredWorldAngle * (180f / MathF.PI));
            this.lastInjectedReferenceDegrees = this.lastCameraReferenceDegrees;
            this.lastInjectedRelativeDegrees = NormalizeSignedDegrees(relativeDirection * (180f / MathF.PI));
            this.lastInjectedLeft = *sumLeft;
            this.lastInjectedForward = *sumForward;
            this.nativeDirectionDiagnosticPending = true;
        }
    }

    private void FlushNativeDirectionDiagnostic()
    {
        if (!this.nativeDirectionDiagnosticPending || this.nativeDirectionDiagnosticLogged)
            return;
        this.nativeDirectionDiagnosticPending = false;
        this.nativeDirectionDiagnosticLogged = true;
        if (!this.configuration.DebugMode)
            return;
        Log.Information(
            "[跳跳乐助手] 原生方向输入：模式 {Mode}；世界角 {World:F2}°；基准角 {Reference:F2}°；相对角 {Relative:+0.00;-0.00;0.00}°；Left {Left:+0.000;-0.000;0.000}；Forward {Forward:+0.000;-0.000;0.000}",
            this.lastInjectedLegacyMode ? "传统" : "标准",
            this.lastInjectedWorldDegrees,
            this.lastInjectedReferenceDegrees,
            this.lastInjectedRelativeDegrees,
            this.lastInjectedLeft,
            this.lastInjectedForward);
    }

    private unsafe bool StartVirtualGamepadFallback(out string error)
    {
        if (!this.TryTemporarilyEnableGamepad(out error))
            return false;
        if (!this.TryInitializeVirtualGamepad(out error))
        {
            this.RestoreGamepadMode();
            return false;
        }
        try
        {
            this.updatePlayerWalkSpeedHook ??=
                GameInteropProvider.HookFromSignature<UpdatePlayerWalkSpeedDelegate>(
                    UpdatePlayerWalkSpeedSignature,
                    this.UpdatePlayerWalkSpeedDetour);
            this.speedOverrideApplied = false;
            this.speedOverrideActive = true;
            this.updatePlayerWalkSpeedHook.Enable();
        }
        catch (Exception exception)
        {
            this.speedOverrideActive = false;
            this.DisposeVirtualGamepad();
            this.RestoreGamepadMode();
            error = $"移动控制初始化失败：{exception.GetType().Name}；未执行跳跃。";
            return false;
        }
        if (!this.TryApplyVirtualMovement(1f))
        {
            this.speedOverrideActive = false;
            this.updatePlayerWalkSpeedHook.Disable();
            this.DisposeVirtualGamepad();
            this.RestoreGamepadMode();
            error = "无法提交备用虚拟 Xbox 左摇杆输入。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private bool TryApplyMovementDirection(float effectiveInputScale)
        => this.nativeMovementActive || this.TryApplyVirtualMovement(effectiveInputScale);

    private unsafe void UpdatePlayerWalkSpeedDetour(PlayerMoveControllerWalk* controller)
    {
        if (this.speedOverrideActive && controller != null)
        {
            this.walkControllerAddress = (nint)controller;
            this.appliedBaseMovementSpeed = controller->BaseMovementSpeed;
            controller->CurrentSpeed = Math.Clamp(
                this.desiredSpeedOverride,
                0f,
                MathF.Max(0f, controller->BaseMovementSpeed));
        }

        this.updatePlayerWalkSpeedHook!.Original(controller);

        if (this.speedOverrideActive && controller != null)
        {
            this.walkControllerAddress = (nint)controller;
            this.appliedBaseMovementSpeed = controller->BaseMovementSpeed;
            controller->CurrentSpeed = Math.Clamp(
                this.desiredSpeedOverride,
                0f,
                MathF.Max(0f, controller->BaseMovementSpeed));
            this.speedOverrideApplied = true;
        }
    }

    private unsafe bool TryWriteCurrentSpeed(float speed)
    {
        if (this.walkControllerAddress == 0)
            return false;
        var controller = (PlayerMoveControllerWalk*)this.walkControllerAddress;
        var baseSpeed = controller->BaseMovementSpeed;
        if (!float.IsFinite(baseSpeed) || baseSpeed <= 0f || speed < 0f || speed > baseSpeed)
            return false;
        this.appliedBaseMovementSpeed = baseSpeed;
        this.desiredSpeedOverride = speed;
        controller->CurrentSpeed = speed;
        return true;
    }

    private void HandleTargetArrival(Vector3 playerPosition, float forwardProgress)
    {
        this.targetArrivalHandled = true;
        this.nativeMovementActive = false;
        this.DisposeVirtualGamepad();
        var forwardError = forwardProgress - this.attemptTargetDistance;
        var heightError = playerPosition.Y - this.target.Y;
        if (this.targetArrivalMode == TargetArrivalMode.ZeroHorizontalSpeed)
        {
            var cleared = this.TryWriteCurrentSpeed(0f);
            this.status = $"已经过目标并{(cleared ? "停止水平移动" : "归零方向输入")}；前向误差 {forwardError:+0.000;-0.000;0.000}，高度误差 {heightError:+0.000;-0.000;0.000}；继续自然下落。";
        }
        else
        {
            this.speedOverrideActive = false;
            this.speedOverrideApplied = false;
            this.updatePlayerWalkSpeedHook?.Disable();
            this.walkControllerAddress = 0;
            this.RestoreGamepadMode();
            this.status = $"已经过探针目标并释放速度与方向控制；前向误差 {forwardError:+0.000;-0.000;0.000}，高度误差 {heightError:+0.000;-0.000;0.000}；后续由游戏自然下落。";
        }
        this.ReportStatus(this.status, true);
    }

    private bool TryTemporarilyEnableGamepad(out string error)
    {
        if (!GameConfig.TryGet(SystemConfigOption.PadMode, out uint gamepadMode))
        {
            error = "无法读取游戏的 PadMode 配置，未发送移动输入。";
            return false;
        }

        this.previousGamepadMode = gamepadMode;
        this.restoreGamepadMode = gamepadMode == 0;
        if (this.restoreGamepadMode)
            GameConfig.Set(SystemConfigOption.PadMode, 1u);
        error = string.Empty;
        return true;
    }

    private void RestoreGamepadMode()
    {
        if (!this.restoreGamepadMode)
            return;
        this.restoreGamepadMode = false;
        try
        {
            GameConfig.Set(SystemConfigOption.PadMode, this.previousGamepadMode);
        }
        catch (Exception)
        {
            // The game may already be shutting down; leaving gamepad enabled is harmless.
        }
    }

    private bool TryInitializeVirtualGamepad(out string error)
    {
        if (this.virtualGamepadConnected && this.virtualGamepad != null)
        {
            error = string.Empty;
            return true;
        }

        try
        {
            this.DisposeVirtualGamepad();
            this.virtualGamepadClient = new ViGEmClient();
            this.virtualGamepad = this.virtualGamepadClient.CreateXbox360Controller();
            this.virtualGamepad.AutoSubmitReport = false;
            this.virtualGamepad.Connect();
            this.virtualGamepad.ResetReport();
            this.virtualGamepad.SubmitReport();
            this.virtualGamepadConnected = true;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            this.DisposeVirtualGamepad();
            error = $"虚拟手柄初始化失败：{exception.GetType().Name}。请确认 ViGEmBus 已安装。";
            return false;
        }
    }

    private unsafe bool TryApplyVirtualMovement(float? effectiveInputScale = null)
    {
        if (!this.virtualGamepadConnected || this.virtualGamepad == null)
            return false;

        try
        {
            var cameraManager = GameCameraManager.Instance();
            var camera = cameraManager == null ? null : cameraManager->GetActiveCamera();
            if (camera == null)
                return false;

            var physicalStickScale = this.GetPhysicalStickScale(effectiveInputScale ?? this.movementInputScale);
            var desiredWorldAngle = MathF.Atan2(this.targetDirection.X, this.targetDirection.Y);
            var cameraReferenceAngle = camera->DirH + MathF.PI;
            var relativeDirection = desiredWorldAngle - cameraReferenceAngle;
            this.lastCameraReferenceDegrees = NormalizeDegrees(cameraReferenceAngle * (180f / MathF.PI));

            // The game's legacy input is Left=sin(relative), Forward=cos(relative).
            // XInput's positive X is right, hence the sign inversion for Left.
            var stickX = -MathF.Sin(relativeDirection) * physicalStickScale;
            var stickY = MathF.Cos(relativeDirection) * physicalStickScale;
            this.virtualGamepad.SetAxisValue(Xbox360Axis.LeftThumbX, ToThumbAxis(stickX));
            this.virtualGamepad.SetAxisValue(Xbox360Axis.LeftThumbY, ToThumbAxis(stickY));
            this.virtualGamepad.SubmitReport();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void NeutralizeVirtualGamepad()
    {
        if (!this.virtualGamepadConnected || this.virtualGamepad == null)
            return;
        try
        {
            this.virtualGamepad.ResetReport();
            this.virtualGamepad.SubmitReport();
        }
        catch (Exception)
        {
            this.virtualGamepadConnected = false;
        }
    }

    private void DisposeVirtualGamepad()
    {
        this.NeutralizeVirtualGamepad();
        if (this.virtualGamepad != null)
        {
            try
            {
                this.virtualGamepad.Disconnect();
            }
            catch (Exception)
            {
                // The driver or device may already be gone during shutdown.
            }
        }
        this.virtualGamepadConnected = false;
        this.virtualGamepad = null;
        this.virtualGamepadClient?.Dispose();
        this.virtualGamepadClient = null;
    }

    private static short ToThumbAxis(float value)
        => (short)Math.Clamp((int)MathF.Round(value * short.MaxValue), -short.MaxValue, short.MaxValue);

    private float GetPhysicalStickScale()
        => this.GetPhysicalStickScale(this.movementInputScale);

    private float GetPhysicalStickScale(float effectiveInputScale)
        => this.gamepadDeadZone + (effectiveInputScale * (1f - this.gamepadDeadZone));

    private static float NormalizeDegrees(float value)
    {
        value %= 360f;
        return value < 0f ? value + 360f : value;
    }

    private static float NormalizeSignedDegrees(float value)
    {
        value = NormalizeDegrees(value);
        return value > 180f ? value - 360f : value;
    }

    private unsafe bool TriggerJump()
    {
        var actionManager = ActionManager.Instance();
        return actionManager != null && actionManager->UseAction(ActionType.GeneralAction, 2);
    }

    private void StopAttempt(string message)
    {
        this.adjustmentMode = false;
        this.DisableObserverCameraLock();
        if (this.speedOverrideActive)
            this.TryWriteCurrentSpeed(0f);
        this.speedOverrideActive = false;
        this.nativeMovementActive = false;
        this.nativeDirectionDiagnosticPending = false;
        this.nativeDirectionDiagnosticLogged = false;
        this.speedOverrideApplied = false;
        this.walkControllerAddress = 0;
        this.targetArrivalHandled = false;
        this.attemptLandingAllowance = 0f;
        this.updatePlayerWalkSpeedHook?.Disable();
        this.readWalkInputHook?.Disable();
        this.DisposeVirtualGamepad();
        this.RestoreGamepadMode();
        this.state = AttemptState.Idle;
        this.status = message;
    }

    private void Draw()
    {
        this.UpdateCandidatePreview();
        this.DrawOverlay();
        if (!this.windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(520f, 340f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("跳跳乐助手###JumpAssist", ref this.windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("普通选点：鼠标指向平台后 /jumpassist set。观察者选点：/jumpassist observe 后用 WASD/Space/Ctrl 自由选择三维位置，Shift 精调；R 贴合平台只是可选操作，未贴合的空中坐标也能直接执行。");
        ImGui.Separator();
        ImGui.TextUnformatted($"状态：{this.status}");
        if (ObjectTable.LocalPlayer is { } player && this.candidateValid)
        {
            var delta = this.candidate - player.Position;
            ImGui.TextUnformatted($"脚下：X {player.Position.X:F2}  Y {player.Position.Y:F2}  Z {player.Position.Z:F2}");
            ImGui.TextUnformatted($"指针：X {this.candidate.X:F2}  Y {this.candidate.Y:F2}  Z {this.candidate.Z:F2}");
            ImGui.TextUnformatted($"水平距离：{new Vector2(delta.X, delta.Z).Length():F2} yalms    高度差：{delta.Y:+0.00;-0.00;0.00}");
        }
        else
            ImGui.TextDisabled("当前鼠标位置没有检测到场景表面。");
        if (this.targetSet)
        {
            ImGui.TextUnformatted($"规划起点：X {this.planOrigin.X:F2}  Y {this.planOrigin.Y:F2}  Z {this.planOrigin.Z:F2}");
            ImGui.TextUnformatted($"固定目标：X {this.target.X:F2}  Y {this.target.Y:F2}  Z {this.target.Z:F2}");
        }
        else
            ImGui.TextDisabled("尚未固定目标：请执行 /jumpassist set。");
        if (ImGui.Button("进入观察者选点"))
            this.BeginObserverSelection();
        if (this.observerMode)
        {
            ImGui.SameLine();
            if (ImGui.Button("向下贴合平台（R）"))
                this.TrySnapTargetToSurfaceBelow(true);
            ImGui.SameLine();
            ImGui.TextColored(
                this.targetSurfaceSnapped ? new Vector4(0.2f, 1f, 0.4f, 1f) : new Vector4(1f, 0.75f, 0.2f, 1f),
                this.targetSurfaceSnapped ? "已贴合顶面" : "自由三维目标：可直接执行");
        }
        var zeroAtTarget = this.targetArrivalMode == TargetArrivalMode.ZeroHorizontalSpeed;
        if (ImGui.RadioButton("到点水平零速", zeroAtTarget))
            this.targetArrivalMode = TargetArrivalMode.ZeroHorizontalSpeed;
        ImGui.SameLine();
        if (ImGui.RadioButton("到点后自然下落", !zeroAtTarget))
            this.targetArrivalMode = TargetArrivalMode.NaturalFall;
        if (this.targetSet)
        {
            var adjustmentMode = this.adjustmentMode;
            if (ImGui.Checkbox("落点键盘微调（WASD / Space / Ctrl）", ref adjustmentMode))
                this.SetAdjustmentMode(adjustmentMode, true);
            ImGui.SameLine();
            ImGui.TextColored(
                this.adjustmentMode ? new Vector4(0.2f, 1f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f),
                this.adjustmentMode ? "按键已拦截，角色不会移动" : "已关闭");
        }
        ImGui.TextUnformatted($"安全边界：{this.safetyBoundary.Count} 个点{(this.safetyBoundary.Count >= 3 ? "（已闭合）" : "")}");

        ImGui.Spacing();
        var showMeasurement = this.configuration.ShowCursorMeasurement;
        if (ImGui.Checkbox("显示实时测距、判定点和预测路径", ref showMeasurement))
        {
            this.configuration.ShowCursorMeasurement = showMeasurement;
            this.Save();
        }
        var debugMode = this.configuration.DebugMode;
        if (ImGui.Checkbox("调试模式（在 /e 显示详细状态）", ref debugMode))
        {
            this.configuration.DebugMode = debugMode;
            this.Save();
        }
        ImGui.TextDisabled("/go 会按规划方向和速度执行跳跃，过程中不会改变摄像头方向。");
        ImGui.TextColored(new Vector4(1f, 0.25f, 0.25f, 1f), "红色：可通过");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.65f, 0.15f, 1f), "橙色：存在风险，可自行执行");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.25f, 1f, 0.35f, 1f), "绿色：不可通过");
        if (this.state != AttemptState.Idle && ImGui.Button("立即中止"))
            this.StopAttempt("已从设置窗口中止。");
        ImGui.End();
    }

    private void UpdateCandidatePreview()
    {
        if (!ClientState.IsLoggedIn)
        {
            this.candidateValid = false;
            return;
        }
        if (!ImGui.GetIO().WantTextInput)
            this.CaptureCursorPoint();
    }

    private void CaptureCursorPoint()
        => this.candidateValid = TryScreenToWorld(ImGui.GetMousePos(), out this.candidate);

    private void AddBoundaryPoint()
    {
        if (this.state != AttemptState.Idle)
        {
            this.ReportStatus("跳跃执行中，不能修改安全边界。", true);
            return;
        }
        this.CaptureCursorPoint();
        if (!this.candidateValid)
        {
            this.ReportStatus("无法取得鼠标所指的场景坐标，边界点未添加。", true);
            return;
        }
        this.safetyBoundary.Add(this.candidate);
        var suffix = this.safetyBoundary.Count >= 3 ? "，安全区域已闭合" : "，至少需要 3 个点";
        this.ReportStatus($"已添加边界点 {this.safetyBoundary.Count}{suffix}。", true);
    }

    private unsafe void BeginObserverSelection()
    {
        if (this.state != AttemptState.Idle || ObjectTable.LocalPlayer is not { } player)
        {
            this.ReportStatus("当前无法进入观察者选点；请先停止跳跃并确保角色可用。", true);
            return;
        }
        var horizontalForward = TryGetCameraHorizontalAxes(out var cameraForward, out _)
            ? cameraForward
            : Vector2.UnitY;
        var forward = new Vector3(horizontalForward.X, 0f, horizontalForward.Y);
        this.planOrigin = player.Position;
        this.target = player.Position + (forward * 0.80f) + new Vector3(0f, 0.20f, 0f);
        this.targetHeight = this.target.Y;
        this.targetSet = true;
        this.observerMode = true;
        this.targetSurfaceSnapped = false;
        this.snapWasDown = false;
        if (!this.SetAdjustmentMode(true, false))
            return;
        this.ReportStatus(
            "已进入观察者选点：WASD 飞行，Space/Ctrl 升降，Shift 精调，鼠标旋转视角；圆圈所在三维坐标可直接执行。R 仅用于可选的向下贴合；go fall 到点后自然下落，go zero 到点后水平零速。角色不会移动。",
            true);
    }

    private bool TrySnapTargetToSurfaceBelow(bool notifyChat)
    {
        if (!this.targetSet || this.state != AttemptState.Idle)
        {
            if (notifyChat)
                this.ReportStatus("当前没有可贴合的自由落点。", true);
            return false;
        }
        var rayOrigin = this.target + new Vector3(0f, 0.60f, 0f);
        if (!TryRaycastAllLayers(rayOrigin, -Vector3.UnitY, 20f, out var surface, out var normal)
            || (normal.LengthSquared() > 0.01f && normal.Y < 0.45f))
        {
            if (notifyChat)
                this.ReportStatus("圆圈下方 20 yalm 内没有检测到朝上的可站立表面；请飞到平台正上方再按 R。", true);
            return false;
        }
        this.target = surface;
        this.targetHeight = surface.Y;
        this.targetSurfaceSnapped = true;
        if (notifyChat)
            this.ReportStatus($"观察者探针已向下贴合顶面 X{surface.X:F2} Y{surface.Y:F2} Z{surface.Z:F2}；可继续微调或执行 /jumpassist go。", true);
        return true;
    }

    private bool SetAdjustmentMode(bool enabled, bool notifyChat)
    {
        if (enabled && (!this.targetSet || this.state != AttemptState.Idle))
        {
            if (notifyChat)
                this.ReportStatus("只能在已固定目标且未执行跳跃时开启落点微调。", true);
            return false;
        }

        if (enabled && this.observerMode && !this.TryEnableObserverCameraLock(out var cameraError))
        {
            this.adjustmentMode = false;
            this.ReportStatus(cameraError, true);
            return false;
        }
        if (!enabled)
            this.DisableObserverCameraLock();

        this.adjustmentMode = enabled;
        this.lastAdjustmentAt = Stopwatch.GetTimestamp();
        if (notifyChat)
        {
            this.ReportStatus(enabled
                ? "已开启落点微调；WASD 按摄像机水平朝向移动，Space 上升、Ctrl 下降，Shift 精调；这些按键不会传给角色移动。"
                : "已确认落点并关闭键盘微调；角色移动按键已恢复。", true);
        }
        return true;
    }

    private unsafe void UpdateTargetAdjustment()
    {
        if (!this.adjustmentMode || !this.targetSet || this.state != AttemptState.Idle)
            return;
        var raptureAtkModule = RaptureAtkModule.Instance();
        if (ImGui.GetIO().WantTextInput
            || (raptureAtkModule != null && raptureAtkModule->IsTextInputActive()))
        {
            this.lastAdjustmentAt = Stopwatch.GetTimestamp();
            this.ApplyAdjustmentCamera();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = this.lastAdjustmentAt == 0
            ? 0f
            : Math.Clamp((float)((now - this.lastAdjustmentAt) / (double)Stopwatch.Frequency), 0f, 0.05f);
        this.lastAdjustmentAt = now;

        var forwardInput = (IsPhysicalKeyDown((int)VirtualKey.W) ? 1f : 0f)
            - (IsPhysicalKeyDown((int)VirtualKey.S) ? 1f : 0f);
        var rightInput = (IsPhysicalKeyDown((int)VirtualKey.D) ? 1f : 0f)
            - (IsPhysicalKeyDown((int)VirtualKey.A) ? 1f : 0f);
        var verticalInput = (IsPhysicalKeyDown((int)VirtualKey.SPACE) ? 1f : 0f)
            - (IsPhysicalKeyDown((int)VirtualKey.CONTROL) ? 1f : 0f);
        var precise = IsPhysicalKeyDown((int)VirtualKey.SHIFT);
        var snapDown = IsPhysicalKeyDown((int)VirtualKey.R);

        // Clear the game's key-state buffer every adjustment frame. Physical
        // state is read above from Windows, so the marker still responds while
        // the character never receives movement or jump/crouch input.
        KeyState[VirtualKey.W] = false;
        KeyState[VirtualKey.A] = false;
        KeyState[VirtualKey.S] = false;
        KeyState[VirtualKey.D] = false;
        KeyState[VirtualKey.SPACE] = false;
        KeyState[VirtualKey.CONTROL] = false;
        KeyState[VirtualKey.R] = false;

        if (this.observerMode && snapDown && !this.snapWasDown)
            this.TrySnapTargetToSurfaceBelow(true);
        this.snapWasDown = snapDown;

        if (seconds <= 0f || (forwardInput == 0f && rightInput == 0f && verticalInput == 0f))
        {
            this.ApplyAdjustmentCamera();
            return;
        }

        if (!TryGetCameraHorizontalAxes(out var forward, out var right))
            return;
        var horizontal = (forward * forwardInput) + (right * rightInput);
        if (horizontal.LengthSquared() > 1f)
            horizontal = Vector2.Normalize(horizontal);
        var speed = precise
            ? PreciseTargetAdjustmentSpeed
            : this.observerMode ? ObserverTargetSpeed : TargetAdjustmentSpeed;
        this.target += new Vector3(horizontal.X * speed * seconds, verticalInput * speed * seconds, horizontal.Y * speed * seconds);
        this.targetHeight = this.target.Y;
        if (this.observerMode)
            this.targetSurfaceSnapped = false;
        this.status = $"正在微调固定落点：X{this.target.X:F2} Y{this.target.Y:F2} Z{this.target.Z:F2}；/jumpassist go 自动确认。";
        this.ApplyAdjustmentCamera();
    }

    private unsafe void ApplyAdjustmentCamera()
    {
        var player = ObjectTable.LocalPlayer;
        if (!this.adjustmentMode || !this.targetSet || player == null)
            return;
        // Observer mode is applied inside the game's own camera-target position
        // calculation. Let the native camera finish rotation, zoom and collision
        // around the probe instead of translating the render matrix afterward.
        if (this.observerMode)
            return;
        var cameraManager = GameCameraManager.Instance();
        var camera = cameraManager == null ? null : cameraManager->GetActiveCamera();
        if (camera == null)
            return;

        var offset = this.target - player.Position;
        var sceneCamera = &camera->SceneCamera;
        var basePosition = (Vector3)camera->LastPosition;
        var baseLookAt = (Vector3)camera->LastLookAtVector;
        if (!IsFinite(basePosition) || !IsFinite(baseLookAt)
            || Vector3.DistanceSquared(basePosition, baseLookAt) < 0.01f)
        {
            basePosition = sceneCamera->Position;
            baseLookAt = sceneCamera->LookAtVector;
        }
        var translatedPosition = basePosition + offset;
        var translatedLookAt = baseLookAt + offset;
        var viewMatrix = Matrix4x4.CreateLookAt(translatedPosition, translatedLookAt, Vector3.UnitY);
        sceneCamera->Position = translatedPosition;
        sceneCamera->LookAtVector = translatedLookAt;
        sceneCamera->ViewMatrix = viewMatrix;
        if (sceneCamera->RenderCamera != null)
        {
            sceneCamera->RenderCamera->Origin = translatedPosition;
            sceneCamera->RenderCamera->ViewMatrix = viewMatrix;
        }
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private unsafe bool TryEnableObserverCameraLock(out string error)
    {
        try
        {
            var cameraManager = GameCameraManager.Instance();
            var camera = cameraManager == null ? null : cameraManager->GetActiveCamera();
            if (camera == null)
            {
                error = "当前没有可用的游戏相机，无法把观察中心锁定到落点。";
                return false;
            }

            if (this.getCameraPositionHook == null)
            {
                var virtualTable = *(nint**)camera;
                var getCameraPositionAddress = virtualTable[16];
                if (getCameraPositionAddress == 0)
                {
                    error = "无法取得游戏相机主体位置入口，观察者模式未启动。";
                    return false;
                }
                this.getCameraPositionHook = GameInteropProvider.HookFromAddress<GetCameraPositionDelegate>(
                    getCameraPositionAddress,
                    this.GetCameraPositionDetour);
            }
            this.getCameraPositionHook.Enable();
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            this.getCameraPositionHook?.Disable();
            error = $"观察者相机主体锁定失败：{exception.GetType().Name}。";
            return false;
        }
    }

    private void DisableObserverCameraLock()
    {
        this.getCameraPositionHook?.Disable();
    }

    private unsafe void GetCameraPositionDetour(
        GameCamera* camera,
        NativeGameObject* cameraTarget,
        NativeVector3* cameraTargetPosition,
        byte swapPerson)
    {
        this.getCameraPositionHook!.Original(camera, cameraTarget, cameraTargetPosition, swapPerson);
        if (!this.observerMode || !this.adjustmentMode || !this.targetSet || cameraTargetPosition == null)
            return;

        // Preserve the native character camera's height offset and all camera
        // rules, but move its world-space subject from the planning character to
        // the probe. The rest of the native update now truly orbits the probe.
        var offset = this.target - this.planOrigin;
        cameraTargetPosition->X += offset.X;
        cameraTargetPosition->Y += offset.Y;
        cameraTargetPosition->Z += offset.Z;
    }

    private static unsafe bool TryGetCameraHorizontalAxes(out Vector2 forward, out Vector2 right)
    {
        forward = default;
        right = default;
        var cameraManager = GameCameraManager.Instance();
        var camera = cameraManager == null ? null : cameraManager->GetActiveCamera();
        if (camera == null)
            return false;

        // Use the actual rendered view direction instead of DirH. DirH follows
        // the game's character-camera convention and its sign/PI offset does not
        // consistently match screen-space WASD after the observer translation.
        var cameraPosition = (Vector3)camera->LastPosition;
        var lookAt = (Vector3)camera->LastLookAtVector;
        if (!IsFinite(cameraPosition) || !IsFinite(lookAt)
            || Vector3.DistanceSquared(cameraPosition, lookAt) < 0.01f)
        {
            cameraPosition = camera->SceneCamera.Position;
            lookAt = camera->SceneCamera.LookAtVector;
        }

        var view = lookAt - cameraPosition;
        var horizontal = new Vector2(view.X, view.Z);
        if (!float.IsFinite(horizontal.X) || !float.IsFinite(horizontal.Y)
            || horizontal.LengthSquared() < 0.0001f)
            return false;

        forward = Vector2.Normalize(horizontal);
        right = new Vector2(-forward.Y, forward.X);
        return true;
    }

    private void ChangeTargetHeight(string valueText)
    {
        if (!this.targetSet || this.state != AttemptState.Idle)
        {
            this.ReportStatus("请先固定目标，且只能在未执行跳跃时调整高度。", true);
            return;
        }
        if (!float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            this.ReportStatus("高度格式无效。示例：/jumpassist height +0.1 或 /jumpassist height 38.5", true);
            return;
        }
        this.targetHeight = valueText.StartsWith('+') || valueText.StartsWith('-')
            ? this.targetHeight + value
            : value;
        this.target.Y = this.targetHeight;
        if (this.observerMode)
            this.targetSurfaceSnapped = false;
        this.ReportStatus($"目标高度已调整为 Y{this.target.Y:F2}，目标 X{this.target.X:F2} Z{this.target.Z:F2}。", true);
    }

    private bool UpdateTargetFromRay()
    {
        if (MathF.Abs(this.targetRayDirection.Y) < 0.0001f)
            return false;
        var distance = (this.targetHeight - this.targetRayOrigin.Y) / this.targetRayDirection.Y;
        if (distance <= 0f)
            return false;
        this.target = this.targetRayOrigin + (this.targetRayDirection * distance);
        this.target.Y = this.targetHeight;
        return true;
    }

    private void UpdateMovementSpeed(float forwardProgress)
    {
        var now = Stopwatch.GetTimestamp();
        this.speedSamples.Enqueue(new SpeedSample(now, forwardProgress));
        while (this.speedSamples.Count > 2
               && (now - this.speedSamples.ElementAt(1).Timestamp) * 1000d / Stopwatch.Frequency
               >= MaximumSpeedWindowMilliseconds)
            this.speedSamples.Dequeue();

        var oldest = this.speedSamples.Peek();
        var sampleSeconds = (now - oldest.Timestamp) / (double)Stopwatch.Frequency;
        this.currentSpeedWindowMilliseconds = sampleSeconds * 1000d;
        this.forwardSpeed = this.currentSpeedWindowMilliseconds >= MinimumStableSpeedWindowMilliseconds
            ? Math.Clamp((float)((forwardProgress - oldest.ForwardProgress) / sampleSeconds),
                -MaximumHorizontalSpeed, MaximumHorizontalSpeed * 1.15f)
            : 0f;
    }

    private static float Cross(Vector2 left, Vector2 right)
        => (left.X * right.Y) - (left.Y * right.X);

    private static TrajectoryPrediction PredictTrajectory(
        Vector3 start,
        Vector3 destination,
        float characterHeight,
        float collisionRadius,
        IReadOnlyList<Vector3>? safetyBoundary = null,
        bool allowLedgeCapture = true)
    {
        var delta = destination - start;
        var horizontalDistance = new Vector2(delta.X, delta.Z).Length();
        if (horizontalDistance < 0.001f)
            return new TrajectoryPrediction(false, true, false, true, true, false, false, 0f, 0f, 0f, 0f, start, start, [start], [start], TrajectoryCollisionKind.None, default, 0f, false, default, TrajectoryCollisionKind.None, default, 0f);

        var direction = Vector2.Normalize(new Vector2(delta.X, delta.Z));
        var takeoffPoint = start + new Vector3(0f, 0.04f, 0f);
        var hasConfirmedLandingSurface = TryConfirmLandingSurface(
            destination, out var landingSurface, out var landingNormal);
        var landingAllowances = allowLedgeCapture || hasConfirmedLandingSurface
            ? new[] { 0f, 0.10f, 0.20f, 0.30f, LandingLedgeCaptureAllowance }
            : new[] { 0f };
        TrajectoryPrediction bestPrediction = default;
        foreach (var landingAllowance in landingAllowances)
        {
            var prediction = PredictTrajectoryWithAllowance(
                start, destination, characterHeight, collisionRadius, safetyBoundary,
                direction, takeoffPoint, horizontalDistance, landingAllowance,
                hasConfirmedLandingSurface, landingSurface, landingNormal);
            bestPrediction = prediction;
            if (prediction.Reachable)
                return prediction;
        }
        return bestPrediction;
    }

    private static TrajectoryPrediction PredictTrajectoryWithAllowance(
        Vector3 start,
        Vector3 destination,
        float characterHeight,
        float collisionRadius,
        IReadOnlyList<Vector3>? safetyBoundary,
        Vector2 direction,
        Vector3 takeoffPoint,
        float horizontalDistance,
        float landingAllowance,
        bool hasConfirmedLandingSurface,
        Vector3 landingSurface,
        Vector3 landingNormal)
    {
        var flightTime = EstimateFlightTimeForAllowance(destination.Y - start.Y, landingAllowance);
        var plannedSpeed = CalculateRequiredTakeoffSpeedForAllowance(
            horizontalDistance, destination.Y - start.Y, landingAllowance);
        var physicsReachable = float.IsFinite(flightTime)
            && plannedSpeed <= MaximumHorizontalSpeed
            && horizontalDistance is >= MinimumTargetDistance and <= MaximumTargetDistance
            && MathF.Abs(destination.Y - start.Y) <= MaximumTargetHeightDifference;

        var simulatedSpeed = float.IsFinite(plannedSpeed)
            ? Math.Min(plannedSpeed, MaximumHorizontalSpeed)
            : MaximumHorizontalSpeed;
        if (!float.IsFinite(flightTime))
            flightTime = (2f * JumpInitialVerticalSpeed) / Gravity;
        var groundPoints = new List<Vector3> { takeoffPoint };
        const bool terrainSafe = true;
        var airPoints = new List<Vector3>();
        var airSegments = Math.Clamp((int)MathF.Ceiling(flightTime * 48f), 24, 64);
        for (var index = 0; index <= airSegments; index++)
        {
            var time = flightTime * index / airSegments;
            var horizontal = simulatedSpeed * time;
            var vertical = (JumpInitialVerticalSpeed * time) - (0.5f * Gravity * time * time);
            airPoints.Add(takeoffPoint + new Vector3(direction.X * horizontal, vertical, direction.Y * horizontal));
        }

        var clearance = CheckTrajectoryClearance(
            airPoints, destination, characterHeight, collisionRadius,
            hasConfirmedLandingSurface, landingSurface, landingNormal);

        var boundarySafe = safetyBoundary == null
            || safetyBoundary.Count < 3
            || IsInsideBoundary(new Vector2(start.X, start.Z), safetyBoundary);
        return new TrajectoryPrediction(
            physicsReachable, boundarySafe, clearance.LandingSurfaceConfirmed, clearance.PathClear, clearance.HeadClear,
            physicsReachable && boundarySafe && terrainSafe && clearance.PathClear && clearance.HeadClear,
            landingAllowance > 0f, landingAllowance, plannedSpeed, 0f, flightTime,
            takeoffPoint, clearance.CollisionPoint, groundPoints, airPoints,
            clearance.CollisionKind, clearance.CollisionNormal, clearance.CollisionRadius,
            clearance.ClearanceWarning, clearance.WarningCollisionPoint, clearance.WarningCollisionKind,
            clearance.WarningCollisionNormal, clearance.WarningCollisionRadius);
    }

    private static TrajectoryClearance CheckTrajectoryClearance(
        IReadOnlyList<Vector3> airPoints,
        Vector3 destination,
        float characterHeight,
        float collisionRadius,
        bool hasConfirmedLandingSurface,
        Vector3 landingSurface,
        Vector3 landingNormal)
    {
        var height = Math.Clamp(characterHeight, 0.75f, 2.60f);
        var envelopeRadius = Math.Min(Math.Clamp(collisionRadius, MovementCollisionCoreRadius, 0.50f), height * 0.45f);
        var coreRadius = Math.Min(MovementCollisionCoreRadius, envelopeRadius);

        var core = SweepTrajectoryCapsule(
            airPoints, destination, height, coreRadius,
            hasConfirmedLandingSurface, landingSurface, landingNormal, false);
        if (!core.PathClear)
            return core with { LandingSurfaceConfirmed = hasConfirmedLandingSurface };
        var warning = core.ClearanceWarning ? core : default(TrajectoryClearance?);

        // Some jump-puzzle collision models use one of the other default
        // collision material bits (0x1000/0x2000) instead of the conventional
        // walkable 0x4000 bit.  Do not call those a definite block until runtime
        // calibration confirms their semantics, but never label the route clean.
        var alternateMaterialCore = SweepTrajectoryCapsule(
            airPoints, destination, height, coreRadius,
            hasConfirmedLandingSurface, landingSurface, landingNormal, true);
        if (!alternateMaterialCore.PathClear)
        {
            warning ??= core with
            {
                LandingSurfaceConfirmed = hasConfirmedLandingSurface,
                ClearanceWarning = true,
                WarningCollisionPoint = alternateMaterialCore.CollisionPoint,
                WarningCollisionKind = alternateMaterialCore.CollisionKind,
                WarningCollisionNormal = alternateMaterialCore.CollisionNormal,
                WarningCollisionRadius = alternateMaterialCore.CollisionRadius,
            };
        }
        else if (alternateMaterialCore.ClearanceWarning)
        {
            warning ??= alternateMaterialCore;
        }

        var envelope = SweepTrajectoryCapsule(
            airPoints, destination, height, envelopeRadius,
            hasConfirmedLandingSurface, landingSurface, landingNormal, false);
        if (!envelope.PathClear)
        {
            warning ??= core with
            {
                LandingSurfaceConfirmed = hasConfirmedLandingSurface,
                ClearanceWarning = true,
                WarningCollisionPoint = envelope.CollisionPoint,
                WarningCollisionKind = envelope.CollisionKind,
                WarningCollisionNormal = envelope.CollisionNormal,
                WarningCollisionRadius = envelope.CollisionRadius,
            };
        }
        else if (envelope.ClearanceWarning)
        {
            warning ??= envelope;
        }

        return (warning ?? core) with { LandingSurfaceConfirmed = hasConfirmedLandingSurface };
    }

    private static TrajectoryClearance SweepTrajectoryCapsule(
        IReadOnlyList<Vector3> airPoints,
        Vector3 destination,
        float height,
        float radius,
        bool hasConfirmedLandingSurface,
        Vector3 landingSurface,
        Vector3 landingNormal,
        bool includeAlternateCollisionMaterials)
    {
        var bottomCenter = radius;
        var topCenter = Math.Max(bottomCenter, height - radius);
        var capsuleLength = topCenter - bottomCenter;
        var sphereCount = Math.Max(1, (int)MathF.Ceiling(capsuleLength / Math.Max(0.12f, radius * 1.25f)));
        var probeLevels = new List<(float Height, TrajectoryCollisionKind Kind)>();
        var hasHeadWarning = false;
        var headWarningPoint = default(Vector3);
        var headWarningNormal = default(Vector3);
        for (var index = 0; index <= sphereCount; index++)
        {
            var centerHeight = sphereCount == 0
                ? bottomCenter
                : bottomCenter + (capsuleLength * index / sphereCount);
            var kind = index == 0
                ? TrajectoryCollisionKind.Feet
                : centerHeight + radius >= height * 0.88f
                    ? TrajectoryCollisionKind.Head
                    : TrajectoryCollisionKind.Body;
            probeLevels.Add((centerHeight, kind));
        }

        for (var segmentIndex = 0; segmentIndex < airPoints.Count - 1; segmentIndex++)
        {
            foreach (var probe in probeLevels)
            {
                var verticalOffset = new Vector3(0f, probe.Height, 0f);
                var from = airPoints[segmentIndex] + verticalOffset;
                var to = airPoints[segmentIndex + 1] + verticalOffset;
                var segment = to - from;
                var length = segment.Length();
                if (length <= 0.02f)
                    continue;

                // Stop just short of the mathematical endpoint so the target
                // platform itself remains a valid landing surface. Every other
                // part of the path is checked with the full character radius.
                var checkedLength = segmentIndex == airPoints.Count - 2
                    ? Math.Max(0.01f, length - 0.035f)
                    : Math.Max(0.01f, length - 0.01f);
                if (TrySweepSphereAllLayers(
                        from,
                        segment / length,
                        checkedLength,
                        Math.Max(0.05f, radius - 0.01f),
                        includeAlternateCollisionMaterials,
                        out var hit,
                        out var hitNormal))
                {
                    if (probe.Kind == TrajectoryCollisionKind.Feet
                        && IsIntendedLandingContact(
                            hit,
                            hitNormal,
                            destination,
                            radius,
                            hasConfirmedLandingSurface,
                            landingSurface,
                            landingNormal))
                        continue;
                    // GetHeight describes the visual character model. Its top
                    // follows animation and is not a reliable hard boundary of
                    // the native movement controller. Keep checking lower body
                    // probes, but report a top-only hit as clearance risk.
                    if (probe.Kind == TrajectoryCollisionKind.Head)
                    {
                        if (!hasHeadWarning)
                        {
                            hasHeadWarning = true;
                            headWarningPoint = hit;
                            headWarningNormal = hitNormal;
                        }
                        continue;
                    }
                    var headClear = probe.Kind != TrajectoryCollisionKind.Head;
                    return new TrajectoryClearance(false, headClear, hit, probe.Kind, hitNormal, radius, false, false, default, TrajectoryCollisionKind.None, default, 0f);
                }
            }
        }
        return hasHeadWarning
            ? new TrajectoryClearance(true, true, default, TrajectoryCollisionKind.None, default, radius, false, true, headWarningPoint, TrajectoryCollisionKind.Head, headWarningNormal, radius)
            : new TrajectoryClearance(true, true, default, TrajectoryCollisionKind.None, default, radius, false, false, default, TrajectoryCollisionKind.None, default, 0f);
    }

    private static bool IsIntendedLandingContact(
        Vector3 hit,
        Vector3 normal,
        Vector3 destination,
        float radius,
        bool hasConfirmedLandingSurface,
        Vector3 landingSurface,
        Vector3 landingNormal)
    {
        // A foot sphere necessarily touches the destination platform before its
        // center reaches the mathematical endpoint. Some collider types leave
        // RaycastHit.Normal empty, so verify the selected platform with a second
        // downward ray instead of rejecting a legitimate landing solely because
        // that optional field is absent.
        if (!hasConfirmedLandingSurface
            || (landingNormal.LengthSquared() > 0.01f && landingNormal.Y < 0.45f))
            return false;
        var horizontalDistance = new Vector2(hit.X - destination.X, hit.Z - destination.Z).Length();
        var surfaceError = MathF.Abs(hit.Y - landingSurface.Y);
        var hitLooksUpward = normal.LengthSquared() <= 0.01f || normal.Y >= 0.45f;
        return horizontalDistance <= Math.Max(0.18f, radius * 1.35f)
            && surfaceError <= 0.14f
            && hitLooksUpward;
    }

    private static unsafe (float Height, float Radius) GetPlayerCollisionDimensions(nint address)
    {
        var nativePlayer = (NativeGameObject*)address;
        var height = nativePlayer == null ? 1.70f : nativePlayer->GetHeight();
        if (!float.IsFinite(height) || height < 0.5f)
            height = 1.70f;
        return (height, MovementCollisionRadius);
    }

    private static unsafe bool TryRaycastAllLayers(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out Vector3 hitPoint)
        => TryRaycastAllLayers(origin, direction, maxDistance, out hitPoint, out _);

    private static unsafe bool TryRaycastAllLayers(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out Vector3 hitPoint,
        out Vector3 hitNormal,
        bool includeAlternateCollisionMaterials = false)
    {
        hitPoint = default;
        hitNormal = default;
        var framework = GameFramework.Instance();
        var collision = framework == null ? null : framework->BGCollisionModule;
        if (collision == null)
            return false;

        var nativeOrigin = origin;
        var normalized = Vector3.Normalize(direction);
        var nativeDirection = normalized;
        var flags = stackalloc int[]
        {
            includeAlternateCollisionMaterials ? 0x7000 : 0x4000,
            0,
            includeAlternateCollisionMaterials ? 0 : 0x4000,
            0,
        };
        var hit = new NativeRaycastHit();
        if (!collision->RaycastMaterialFilter(
                &hit, &nativeOrigin, &nativeDirection, maxDistance, CharacterTerrainCollisionLayer, flags))
            return false;

        hitPoint = new Vector3(hit.Point.X, hit.Point.Y, hit.Point.Z);
        hitNormal = GetHitNormal(hit, normalized);
        return true;
    }

    private static bool TryConfirmLandingSurface(
        Vector3 destination,
        out Vector3 landingSurface,
        out Vector3 landingNormal)
    {
        var origin = destination + new Vector3(0f, 0.55f, 0f);
        const float maxDistance = 1.10f;
        if (TryRaycastAllLayers(origin, -Vector3.UnitY, maxDistance,
                out landingSurface, out landingNormal)
            && IsMatchingLandingSurface(destination, landingSurface, landingNormal))
            return true;
        if (TryRaycastAllLayers(origin, -Vector3.UnitY, maxDistance,
                out landingSurface, out landingNormal, true)
            && IsMatchingLandingSurface(destination, landingSurface, landingNormal))
            return true;

        landingSurface = default;
        landingNormal = default;
        return false;
    }

    private static bool IsMatchingLandingSurface(
        Vector3 destination,
        Vector3 landingSurface,
        Vector3 landingNormal)
        => MathF.Abs(landingSurface.Y - destination.Y) <= 0.24f
            && (landingNormal.LengthSquared() <= 0.01f || landingNormal.Y >= 0.45f);

    private static unsafe bool TrySweepSphereAllLayers(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        float radius,
        bool includeAlternateCollisionMaterials,
        out Vector3 hitPoint,
        out Vector3 hitNormal)
    {
        hitPoint = default;
        hitNormal = default;
        var framework = GameFramework.Instance();
        var collision = framework == null ? null : framework->BGCollisionModule;
        if (collision == null || radius <= 0f)
            return false;

        // SweepSphereMaterialFilter reads the fourth component of its origin as
        // the sphere radius even though the public signature is Vector3*.
        var sweptOrigin = new Vector4(origin, radius);
        var normalized = Vector3.Normalize(direction);
        // RaycastMaterialFilter is two ulongs: mask then value.  Value 0 means
        // "any masked bit"; 0x7000 is the default collider material family.
        int* flags = stackalloc int[4];
        flags[0] = includeAlternateCollisionMaterials ? 0x7000 : 0x4000;
        flags[1] = 0;
        flags[2] = includeAlternateCollisionMaterials ? 0 : 0x4000;
        flags[3] = 0;
        var hit = new NativeRaycastHit();
        if (!collision->SweepSphereMaterialFilter(
                &hit,
                (Vector3*)&sweptOrigin,
                &normalized,
                maxDistance,
                CharacterTerrainCollisionLayer,
                flags))
            return false;

        hitPoint = new Vector3(hit.Point.X, hit.Point.Y, hit.Point.Z);
        hitNormal = GetHitNormal(hit, normalized);
        return true;
    }

    private static unsafe Vector3 GetHitNormal(NativeRaycastHit hit, Vector3 travelDirection)
    {
        var normal = new Vector3(hit.Normal.X, hit.Normal.Y, hit.Normal.Z);
        if (normal.LengthSquared() <= 0.01f)
        {
            var v1 = new Vector3(hit.V1.X, hit.V1.Y, hit.V1.Z);
            var v2 = new Vector3(hit.V2.X, hit.V2.Y, hit.V2.Z);
            var v3 = new Vector3(hit.V3.X, hit.V3.Y, hit.V3.Z);
            normal = Vector3.Cross(v2 - v1, v3 - v1);
        }

        if (!float.IsFinite(normal.X)
            || !float.IsFinite(normal.Y)
            || !float.IsFinite(normal.Z)
            || normal.LengthSquared() <= 0.01f)
            return Vector3.Zero;

        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, travelDirection) > 0f ? -normal : normal;
    }

    private static bool TryFindTopSurface(Vector3 sideHit, out Vector3 topSurface)
    {
        var origin = sideHit + new Vector3(0f, 1.60f, 0f);
        if (TryRaycastAllLayers(origin, -Vector3.UnitY, 1.75f, out var candidate, out var normal)
            && normal.Y >= 0.45f
            && candidate.Y >= sideHit.Y - 0.05f)
        {
            topSurface = candidate;
            return true;
        }
        topSurface = default;
        return false;
    }

    private static bool IsInsideBoundary(Vector2 point, IReadOnlyList<Vector3> polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = new Vector2(polygon[previous].X, polygon[previous].Z);
            var b = new Vector2(polygon[current].X, polygon[current].Z);
            if (DistanceToSegment(point, a, b) <= 0.03f)
                return true;
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared < 0.000001f)
            return Vector2.Distance(point, start);
        var amount = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, start + (segment * amount));
    }

    private void ReportStatus(string message, bool notifyChat)
    {
        this.status = message;
        if (!this.configuration.DebugMode)
            return;
        Log.Information("[跳跳乐助手] {Message}", message);
        if (!notifyChat)
            return;
        ChatGui.Print(new XivChatEntry
        {
            Type = XivChatType.Echo,
            Message = new SeStringBuilder().AddText($"[跳跳乐助手] {message}").Build(),
            Silent = true,
        });
    }

    private void DrawOverlay()
    {
        var player = ObjectTable.LocalPlayer;
        if (!this.configuration.ShowCursorMeasurement || player == null || this.state != AttemptState.Idle)
            return;
        if (!this.targetSet && !this.candidateValid)
            return;

        const uint reachableColor = 0xFF0000FF; // User-requested red = reachable.
        const uint unreachableColor = 0xFF00FF00; // User-requested green = unreachable.
        const uint clearanceRiskColor = 0xFF00A5FF;
        const uint groundColor = 0xFF00D7FF;
        const uint airColor = 0xFF00A5FF;
        const uint takeoffColor = 0xFF00FFFF;
        const uint collisionColor = unreachableColor;

        var destination = this.targetSet ? this.target : this.candidate;
        var predictionOrigin = this.targetSet ? this.planOrigin : player.Position;
        var delta = destination - predictionOrigin;
        var (characterHeight, collisionRadius) = GetPlayerCollisionDimensions(player.Address);
        var allowLedgeCapture = !this.targetSet || !this.observerMode || this.targetSurfaceSnapped;
        var prediction = PredictTrajectory(
            predictionOrigin, destination, characterHeight, collisionRadius, this.safetyBoundary, allowLedgeCapture);
        var resultColor = prediction.Reachable
            ? prediction.ClearanceWarning ? clearanceRiskColor : reachableColor
            : unreachableColor;
        var resultText = prediction.Reachable
            ? prediction.ClearanceWarning
                ? "存在风险（可执行）"
                : prediction.TerrainSafe ? "可落台" : "轨迹可经过（未确认落台）"
            : "不可通过";
        var targetKind = this.targetSet ? "固定目标" : "鼠标预览";
        var boundaryText = prediction.BoundarySafe ? string.Empty : "  起点越界";
        var terrainText = prediction.TerrainSafe ? string.Empty : "  目标处无已确认顶面";
        var collisionText = !prediction.HeadClear
            ? "  顶头风险"
            : !prediction.PathClear
                ? prediction.CollisionKind == TrajectoryCollisionKind.Feet ? "  脚部碰撞" : "  身体碰撞"
                : string.Empty;
        var clearanceText = prediction.ClearanceWarning
            ? prediction.WarningCollisionKind == TrajectoryCollisionKind.Head
                ? "  头部上沿风险"
                : "  外包络/备用材质风险"
            : string.Empty;
        var takeoffHeightDifference = destination.Y - prediction.TakeoffPoint.Y;
        var maximumReachableRise = prediction.UsesLedgeCapture
            ? MaximumReachableLandingRise
            : MaximumJumpRise;
        var heightReason = takeoffHeightDifference > maximumReachableRise
            ? $"  超过弹道上限{maximumReachableRise:F2}y"
            : string.Empty;
        var speedText = float.IsFinite(prediction.TakeoffSpeed) ? $"{prediction.TakeoffSpeed:F2}" : "—";
        var ledgeText = prediction.UsesLedgeCapture && takeoffHeightDifference > 0f
            ? $"  自动落台捕获 {prediction.LandingAllowance:F2}y"
            : this.targetSet && this.observerMode && !this.targetSurfaceSnapped ? "  精确三维点" : string.Empty;
        var text = $"{targetKind}  {resultText}{boundaryText}{terrainText}{collisionText}{clearanceText}{heightReason}  {new Vector2(delta.X, delta.Z).Length():F2}y  起跳Y {prediction.TakeoffPoint.Y:F2}  落点ΔY {takeoffHeightDifference:+0.00;-0.00;0.00}{ledgeText}  起跳速度 {speedText}";
        var mouse = ImGui.GetMousePos() + new Vector2(18f, 18f);
        var size = ImGui.CalcTextSize(text);
        var drawList = ImGui.GetForegroundDrawList();
        drawList.AddRectFilled(mouse - new Vector2(5f, 3f), mouse + size + new Vector2(5f, 3f), 0xC0000000, 4f);
        drawList.AddText(mouse, resultColor, text);
        if (!this.targetSet)
            drawList.AddCircle(ImGui.GetMousePos(), 10f, resultColor, 20, 3f);

        DrawSafetyBoundary(drawList, this.safetyBoundary);
        if (this.targetSet)
        {
            this.DrawTargetHeightHandle(drawList);
        }
        DrawWorldPath(drawList, prediction.GroundPoints, groundColor, 2f, false);
        foreach (var groundPoint in prediction.GroundPoints)
            DrawGroundRing(drawList, groundPoint, 0.035f, groundColor, 2f);
        DrawWorldPath(drawList, prediction.AirPoints, airColor, 2f, true);
        if ((!prediction.PathClear || !prediction.HeadClear)
            && TryWorldToScreen(prediction.CollisionPoint, out var collisionScreen))
        {
            drawList.AddCircleFilled(collisionScreen, 7f, collisionColor, 16);
            drawList.AddCircle(collisionScreen, 13f, collisionColor, 20, 3f);
            drawList.AddText(collisionScreen + new Vector2(12f, -24f), collisionColor,
                !prediction.HeadClear
                    ? "顶头碰撞"
                    : prediction.CollisionKind == TrajectoryCollisionKind.Feet ? "脚部碰撞" : "身体碰撞");
        }
        else if (prediction.ClearanceWarning
                 && TryWorldToScreen(prediction.WarningCollisionPoint, out var warningScreen))
        {
            drawList.AddCircle(warningScreen, 10f, clearanceRiskColor, 20, 3f);
            drawList.AddText(
                warningScreen + new Vector2(12f, -24f),
                clearanceRiskColor,
                prediction.WarningCollisionKind == TrajectoryCollisionKind.Head
                    ? "头部上沿风险（可执行）"
                    : "擦边/材质风险（可执行）");
        }
        if (TryWorldToScreen(prediction.TakeoffPoint, out var takeoffScreen))
        {
            DrawGroundRing(drawList, prediction.TakeoffPoint, 0.09f, takeoffColor, 3f);
            drawList.AddText(takeoffScreen + new Vector2(8f, -18f), takeoffColor,
                $"原地起跳  Y{prediction.TakeoffPoint.Y:F2}");
        }
        if (TryWorldToScreen(destination + new Vector3(0f, 0.08f, 0f), out var targetScreen))
        {
            DrawGroundRing(drawList, destination + new Vector3(0f, 0.04f, 0f), 0.16f, resultColor, 3f);
            drawList.AddCircleFilled(targetScreen, 3f, resultColor, 12);
        }
    }

    private static void DrawSafetyBoundary(ImDrawListPtr drawList, IReadOnlyList<Vector3> boundary)
    {
        if (boundary.Count == 0)
            return;
        const uint boundaryColor = 0xFFFF66CC;
        var screens = new List<Vector2>(boundary.Count);
        foreach (var point in boundary)
        {
            if (!TryWorldToScreen(point + new Vector3(0f, 0.06f, 0f), out var screen))
                continue;
            screens.Add(screen);
            drawList.AddCircleFilled(screen, 5f, boundaryColor, 12);
        }
        for (var index = 1; index < screens.Count; index++)
            drawList.AddLine(screens[index - 1], screens[index], boundaryColor, 3f);
        if (boundary.Count >= 3 && screens.Count == boundary.Count)
            drawList.AddLine(screens[^1], screens[0], boundaryColor, 3f);
    }

    private void DrawTargetHeightHandle(ImDrawListPtr drawList)
    {
        var handleWorld = this.target + new Vector3(0f, 0.45f, 0f);
        if (!TryWorldToScreen(this.target, out var targetScreen)
            || !TryWorldToScreen(handleWorld, out var handleScreen))
            return;

        const uint color = 0xFFFFFF00;
        drawList.AddLine(targetScreen, handleScreen, color, 3f);
        drawList.AddCircleFilled(handleScreen, 7f, color, 16);
        drawList.AddText(handleScreen + new Vector2(10f, -18f), color, $"目标高度 Y{this.targetHeight:F2}（上下拖动）");

        ImGui.SetNextWindowPos(handleScreen - new Vector2(16f));
        ImGui.SetNextWindowSize(new Vector2(32f));
        ImGui.SetNextWindowBgAlpha(0f);
        var flags = ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoBackground
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoNav
                    | ImGuiWindowFlags.NoFocusOnAppearing;
        ImGui.Begin("###JumpAssistTargetHeightHandle", flags);
        ImGui.InvisibleButton("###JumpAssistTargetHeightDrag", new Vector2(32f));
        if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            this.targetHeight -= ImGui.GetIO().MouseDelta.Y * 0.01f;
            this.target.Y = this.targetHeight;
            if (this.observerMode)
                this.targetSurfaceSnapped = false;
        }
        if (ImGui.IsItemHovered() && MathF.Abs(ImGui.GetIO().MouseWheel) > 0f)
        {
            this.targetHeight += ImGui.GetIO().MouseWheel * 0.05f;
            this.target.Y = this.targetHeight;
            if (this.observerMode)
                this.targetSurfaceSnapped = false;
        }
        ImGui.End();
    }

    private static unsafe bool TryWorldToScreen(Vector3 world, out Vector2 screen)
    {
        screen = default;
        var manager = SceneCameraManager.Instance();
        var camera = manager == null ? null : manager->CurrentCamera;
        if (camera == null)
            return false;

        var nativeWorld = new NativeVector3 { X = world.X, Y = world.Y, Z = world.Z };
        if (!camera->WorldToScreen(nativeWorld, out var nativeScreen))
            return false;

        var viewport = ImGui.GetMainViewport();
        var framebufferScale = ImGui.GetIO().DisplayFramebufferScale;
        var scaleX = framebufferScale.X > 0f ? framebufferScale.X : 1f;
        var scaleY = framebufferScale.Y > 0f ? framebufferScale.Y : 1f;
        screen = viewport.Pos + new Vector2(nativeScreen.X / scaleX, nativeScreen.Y / scaleY);
        return true;
    }

    private static unsafe bool TryScreenToWorld(Vector2 screen, out Vector3 world)
    {
        world = default;
        var manager = SceneCameraManager.Instance();
        var camera = manager == null ? null : manager->CurrentCamera;
        if (camera == null)
            return false;

        var viewport = ImGui.GetMainViewport();
        var framebufferScale = ImGui.GetIO().DisplayFramebufferScale;
        var scaleX = framebufferScale.X > 0f ? framebufferScale.X : 1f;
        var scaleY = framebufferScale.Y > 0f ? framebufferScale.Y : 1f;
        var local = screen - viewport.Pos;
        var nativeScreen = new NativeVector2 { X = local.X * scaleX, Y = local.Y * scaleY };
        if (!camera->ScreenToWorld(nativeScreen, out var nativeWorld))
            return false;
        world = new Vector3(nativeWorld.X, nativeWorld.Y, nativeWorld.Z);
        return true;
    }

    private static unsafe bool TryGetScreenRay(Vector2 screen, out Vector3 origin, out Vector3 direction)
    {
        origin = default;
        direction = default;
        var manager = SceneCameraManager.Instance();
        var camera = manager == null ? null : manager->CurrentCamera;
        if (camera == null)
            return false;

        var viewport = ImGui.GetMainViewport();
        var framebufferScale = ImGui.GetIO().DisplayFramebufferScale;
        var scaleX = framebufferScale.X > 0f ? framebufferScale.X : 1f;
        var scaleY = framebufferScale.Y > 0f ? framebufferScale.Y : 1f;
        var local = screen - viewport.Pos;
        var ray = camera->ScreenPointToRay(new NativeVector2 { X = local.X * scaleX, Y = local.Y * scaleY });
        origin = new Vector3(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
        direction = Vector3.Normalize(new Vector3(ray.Direction.X, ray.Direction.Y, ray.Direction.Z));
        return direction.LengthSquared() > 0.9f;
    }

    private static void DrawGroundRing(
        ImDrawListPtr drawList,
        Vector3 center,
        float radius,
        uint color,
        float thickness)
    {
        const int segments = 20;
        Vector2? first = null;
        Vector2? previous = null;
        for (var index = 0; index <= segments; index++)
        {
            var angle = MathF.Tau * index / segments;
            var world = center + new Vector3(MathF.Cos(angle) * radius, 0.02f, MathF.Sin(angle) * radius);
            if (!TryWorldToScreen(world, out var screen))
            {
                previous = null;
                continue;
            }
            first ??= screen;
            if (previous is { } previousScreen)
                drawList.AddLine(previousScreen, screen, color, thickness);
            previous = screen;
        }
        if (first is { } firstScreen && previous is { } lastScreen)
            drawList.AddLine(lastScreen, firstScreen, color, thickness);
    }

    private static void DrawWorldPath(
        ImDrawListPtr drawList,
        IReadOnlyList<Vector3> points,
        uint color,
        float thickness,
        bool drawPoints)
    {
        Vector2? previous = null;
        foreach (var point in points)
        {
            if (!TryWorldToScreen(point, out var screen))
            {
                previous = null;
                continue;
            }
            if (previous is { } previousScreen)
                drawList.AddLine(previousScreen, screen, color, thickness);
            if (drawPoints)
                drawList.AddCircleFilled(screen, 3f, color, 10);
            previous = screen;
        }
    }

    private void Save() => this.configuration.SaveAction();

    private static double ElapsedMilliseconds(long start)
        => (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;

    private static bool IsPhysicalKeyDown(int virtualKey)
        => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private unsafe delegate void UpdatePlayerWalkSpeedDelegate(PlayerMoveControllerWalk* controller);
    private unsafe delegate void ReadWalkInputDelegate(
        void* self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte* haveBackwardOrStrafe,
        byte* unknown,
        byte additiveInput);
    private unsafe delegate void GetCameraPositionDelegate(
        GameCamera* camera,
        NativeGameObject* cameraTarget,
        NativeVector3* cameraTargetPosition,
        byte swapPerson);

    [StructLayout(LayoutKind.Explicit, Size = 0x140)]
    private struct PlayerMoveControllerWalk
    {
        [FieldOffset(0x44)] public float CurrentSpeed;
        [FieldOffset(0x58)] public float BaseMovementSpeed;
    }

    private enum AttemptState
    {
        Idle,
        RunUp,
        JumpTriggered,
        Airborne,
    }

    private enum TargetArrivalMode
    {
        NaturalFall,
        ZeroHorizontalSpeed,
    }

    private readonly record struct TrajectoryPrediction(
        bool PhysicsReachable,
        bool BoundarySafe,
        bool TerrainSafe,
        bool PathClear,
        bool HeadClear,
        bool Reachable,
        bool UsesLedgeCapture,
        float LandingAllowance,
        float TakeoffSpeed,
        float RunUpDistance,
        float FlightTime,
        Vector3 TakeoffPoint,
        Vector3 CollisionPoint,
        IReadOnlyList<Vector3> GroundPoints,
        IReadOnlyList<Vector3> AirPoints,
        TrajectoryCollisionKind CollisionKind,
        Vector3 CollisionNormal,
        float CollisionRadius,
        bool ClearanceWarning,
        Vector3 WarningCollisionPoint,
        TrajectoryCollisionKind WarningCollisionKind,
        Vector3 WarningCollisionNormal,
        float WarningCollisionRadius);

    private readonly record struct TrajectoryClearance(
        bool PathClear,
        bool HeadClear,
        Vector3 CollisionPoint,
        TrajectoryCollisionKind CollisionKind,
        Vector3 CollisionNormal,
        float CollisionRadius,
        bool LandingSurfaceConfirmed,
        bool ClearanceWarning,
        Vector3 WarningCollisionPoint,
        TrajectoryCollisionKind WarningCollisionKind,
        Vector3 WarningCollisionNormal,
        float WarningCollisionRadius);

    private enum TrajectoryCollisionKind
    {
        None,
        Feet,
        Body,
        Head,
    }

    private readonly record struct SpeedSample(long Timestamp, float ForwardProgress);
}
