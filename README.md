# win-attention

在 **Windows Terminal** 里使用 Claude Code（WSL2 或原生 Windows 均可）时，只要 Claude 需要你处理，Windows 右下角就会弹出一张卡片，任务栏图标也会显示角标并闪烁。卡片没有声音。

| 情况 | 卡片 |
|---|---|
| 任务完成 | ✔ 任务完成 |
| 需要确认权限 | ⚠ 需要你确认 |
| AskUserQuestion 需要你选择 | ? 需要你选择 |
| 计划等待审批 | ◆ 计划待你审批 |
| 压缩完成（手动或自动） | ≡ 对话已压缩 |
| `/btw` 回答完毕 | 💬 /btw 已回答 |

- 左键点击卡片：跳到对应的 Windows Terminal 标签页，并关闭该卡片。
- 右键点击：直接关闭卡片。
- 多张卡片会叠放显示；不处理的话，30 分钟后自动消失。

## 安装

在 Claude Code 中输入：

```
/plugin marketplace add luzhifang/claude-win-attention
/plugin install win-attention@win-attention
```

安装后重启 Claude Code。第一次弹卡时，plugin 会用 Windows 自带的 `csc.exe` 在本机编译一个小 exe，所以会慢几秒。exe 存放在 `%LOCALAPPDATA%\claude-attention\`。

## 要求

- Windows 10/11。无需另装 .NET，系统自带的 .NET Framework 4 即可。
- **WSL2**：WSL 中需要有 `python3`。
- **原生 Windows**（在 cmd 或 PowerShell 中启动 Claude Code）：需要安装 [Git for Windows](https://git-scm.com/download/win)。Claude Code 默认用 Git Bash 执行 hook；没有 Git Bash 时会改用 PowerShell，此时本 plugin 无法运行。
- 点击卡片跳转标签页需要使用 Windows Terminal。使用其他终端时，卡片和角标照常显示，但无法跳转标签页。
- 如果你的 Windows Terminal profile 设置了 `"suppressApplicationTitle": true`，标签页标题不会随之变化，跳转会不准确。请改回 `false`。
- 在 macOS 或纯 Linux 上安装不会报错，只是不做任何事。
- 在 WSL 中使用时，如果找不到对话标题，会临时修改标签页标题来识别标签页。原生 Windows 没有这个后备方法，找不到标题时只会跳到 Windows Terminal 窗口。

## 排错

- Hook 日志：在 `~/.claude/plugins/data/` 中找到本 plugin 对应的文件夹，查看 `win-attention.log`。WSL 和原生 Windows 各有一份，因为两边的 `~` 不同。
- Windows 端日志：`%LOCALAPPDATA%\claude-attention\host.log`。原生 Windows 首次编译 exe 的输出在同一文件夹的 `build.log`。
- 不启动 Claude Code，直接弹一张测试卡片：
  ```
  echo '{"hook_event_name":"Stop","cwd":"/x/demo"}' | python3 scripts/win-attention.py --demo --wait
  ```

## 已知限制

- 判断逻辑有两份：WSL 用 `scripts/win-attention.py`，原生 Windows 用 `scripts/win-attention.cs` 中的 `HookMode`。修改其中一份时，记得同步修改另一份。
- “`/btw` 回答完毕”和“压缩期间不误报”都是根据观察到的 Claude Code 行为实现的，官方文档没有说明。Claude Code 更新后可能需要相应调整。
- 如果 `/btw` 恰好在压缩进行中回答完毕，那一次不会有提示。
- 如果打开了多个 AI 标题相同的标签页，点击卡片只会跳到 Windows Terminal 窗口，无法区分是哪个标签页。
