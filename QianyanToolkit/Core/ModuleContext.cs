using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Config;

namespace QToolKit.Core;

internal sealed record ModuleContext(
    IDalamudPluginInterface PluginInterface,
    ICommandManager CommandManager,
    IFramework Framework,
    ICondition Condition,
    IObjectTable ObjectTable,
    IPartyList PartyList,
    INamePlateGui NamePlateGui,
    IContextMenu ContextMenu,
    IClientState ClientState,
    IGameInteropProvider GameInteropProvider,
    IDataManager DataManager,
    ITextureProvider TextureProvider,
    IChatGui ChatGui,
    IGameGui GameGui,
    ISeStringEvaluator SeStringEvaluator,
    IPlayerState PlayerState,
    IGameConfig GameConfig,
    IKeyState KeyState,
    IPluginLog Log);
