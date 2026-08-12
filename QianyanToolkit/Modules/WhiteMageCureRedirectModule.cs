using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using QToolKit.Core;

namespace QToolKit.Modules;

internal sealed unsafe class WhiteMageCureRedirectModule : IToolkitModule
{
    private const uint WhiteMageJobId = 24;
    private const uint CureActionId = 120;
    private const uint CureIIActionId = 135;
    private const byte CureIIUnlockLevel = 30;

    private readonly ModuleContext context;
    private readonly Hook<UseActionDelegate> useActionHook;

    public WhiteMageCureRedirectModule(ModuleContext context)
    {
        this.context = context;
        this.useActionHook = context.GameInteropProvider.HookFromAddress<UseActionDelegate>(
            ActionManager.MemberFunctionPointers.UseAction,
            this.UseActionDetour);
    }

    public string Id => "WhiteMageCureRedirect";

    public string DisplayName => "白魔低等级救疗重定向";

    public string Version => "1.0.1.0";

    public string Description => "低等级同步且尚未解锁救疗时，将救疗重定向为治疗。";

    public string CommandHelp => "此模块无需命令。";

    public bool IsRunning => this.useActionHook.IsEnabled;

    public void Start()
    {
        if (!this.useActionHook.IsEnabled)
            this.useActionHook.Enable();
    }

    public void Stop()
    {
        if (this.useActionHook.IsEnabled)
            this.useActionHook.Disable();
    }

    public void DrawSettings()
    {
    }

    public void Dispose()
        => this.useActionHook.Dispose();

    private bool UseActionDetour(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        if (actionType == ActionType.Action
            && actionId == CureIIActionId
            && this.context.PlayerState.IsLoaded
            && this.context.PlayerState.ClassJob.RowId == WhiteMageJobId
            && this.context.PlayerState.EffectiveLevel < CureIIUnlockLevel)
        {
            actionId = CureActionId;
        }

        return this.useActionHook.Original(
            actionManager,
            actionType,
            actionId,
            targetId,
            extraParam,
            mode,
            comboRouteId,
            outOptAreaTargeted);
    }

    private delegate bool UseActionDelegate(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted);
}
