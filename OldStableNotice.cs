using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace QianyanLegacy;

internal static class OldStableNotice
{
    public static void Draw(string popupId, ref bool requested)
    {
        if (!requested)
            return;

        var title = $"功能迁移提示###{popupId}OldStableNotice";
        ImGui.SetNextWindowSize(new Vector2(480f, 0f), ImGuiCond.Appearing);
        var visible = ImGui.Begin(
            title,
            ref requested,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings);

        if (visible)
        {
            ImGui.TextColored(new Vector4(0.96f, 0.22f, 0.26f, 1f), "oldstable");
            ImGui.Spacing();
            ImGui.TextWrapped("此独立插件已进入 oldstable 维护状态。");
            ImGui.TextWrapped("新版功能已迁移至 QToolKit。请在插件安装器中安装 QToolKit，并在完成数据迁移后停用此独立插件。");
            ImGui.Spacing();
            if (ImGui.Button("我知道了", new Vector2(-1f, 0f)))
                requested = false;
        }

        ImGui.End();
    }
}
