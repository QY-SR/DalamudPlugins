using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace WhiteMageCureRedirect;

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService]
    private static IPlayerState PlayerState { get; set; } = null!;

    [PluginService]
    private static IGameInteropProvider GameInteropProvider { get; set; } = null!;

    private const uint WhiteMageJobId = 24;
    private const uint CureActionId = 120;
    private const uint CureIIActionId = 135;
    private const byte CureIIUnlockLevel = 30;

    private readonly Hook<UseActionDelegate> useActionHook;

    public Plugin()
    {
        this.useActionHook = GameInteropProvider.HookFromAddress<UseActionDelegate>(
            ActionManager.MemberFunctionPointers.UseAction,
            this.UseActionDetour);
        this.useActionHook.Enable();
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
            && PlayerState.IsLoaded
            && PlayerState.ClassJob.RowId == WhiteMageJobId
            && PlayerState.EffectiveLevel < CureIIUnlockLevel)
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
