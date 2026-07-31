# QY-SR Dalamud 自定义插件仓库

此仓库同时发布以下三个 Dalamud API 15 插件：

- 战斗模型屏蔽（CombatModelBlocker）
- 新月岛宝藏标记（CrescentMarkers）
- 白魔低等级救疗重定向（WhiteMageCureRedirect）

## 仓库地址

在 Dalamud 设置的“实验性功能”中，将以下地址添加到自定义插件仓库：

```text
https://raw.githubusercontent.com/QY-SR/DalamudPlugins/main/pluginmaster.json
```

保存设置并刷新插件列表后，即可搜索、安装和更新以上插件。

## 更新规则

Dalamud 根据 pluginmaster.json 中的 AssemblyVersion 判断更新。发布新版本时，需要更新对应 ZIP、版本号以及 LastUpdate。

本仓库不包含任何 ACT 版本或 ACT 开发工具。