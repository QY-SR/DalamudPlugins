# 白魔低等级救疗重定向

面向 Dalamud API 15 的轻量插件。

- 仅在当前职业为白魔法师时生效。
- 当前同步等级低于 30（尚未解锁救疗）时，把救疗（动作 ID 135）重定向为治疗（动作 ID 120）。
- 30 级及以上不会修改救疗。
- 只替换玩家主动发出的技能请求，不会自动选取目标或自动施法。

## 构建

```powershell
dotnet build -c Release
```

构建后的开发加载目录为：

`bin\x64\Release\WhiteMageCureRedirect`
