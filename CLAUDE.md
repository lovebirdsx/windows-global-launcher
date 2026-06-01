# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

回答与代码注释统一使用中文（与现有代码风格一致）。

## 项目概览

Windows 平台桌面工具，基于 **.NET 8 WPF**（`net8.0-windows`，同时启用 `UseWPF` 和 `UseWindowsForms`）。包含两个相互独立的功能：

1. **命令启动器**（`MainWindow`）：全局热键（默认 `Ctrl+Shift+I`）弹出的搜索式命令面板，从 JSON 配置读取命令并通过 `Process.Start` 执行。
2. **Alt+Tab 窗口切换器**（`SwitcherWindow`）：接管系统 Alt+Tab，竖向列出当前窗口（图标 + 标题）供切换。

两者由 `App.OnStartup` 同时创建、常驻后台（通过系统托盘 `NotifyIcon` 管理），平时隐藏，靠热键/钩子唤出。

## 常用命令

```bash
dotnet build WindowsGlobalLauncher.sln        # 编译
dotnet test                                    # 运行全部单元测试 (xUnit)
dotnet test --filter "FullyQualifiedName~CommandNameWithHotKeyConverter"  # 运行单个测试
```

发布与本地运行用 PowerShell 脚本（`scripts/publish.ps1`）：会先 `Stop-Process` 掉正在运行的实例，`dotnet publish` 出单文件 exe（`-r win-x64 --self-contained false`，依赖已装 .NET 8 运行时）到 `dist/`，再启动它。

**运行需要管理员权限**：`app.manifest` 声明 `requireAdministrator`（见下文「键盘钩子与权限」），因此直接 `dotnet run` 会触发 UAC。

## 关键架构与约定

**命名空间陷阱**：所有代码使用 `namespace CommandLauncher`，但程序集名是 `WindowsGlobalLauncher`、`RootNamespace` 是 `windows_global_launcher`。新增文件请沿用 `CommandLauncher`。

**纯代码构建 UI，无 XAML**：所有窗口、样式、`DataTemplate`、`Style`/`Trigger` 都在 C# 中用 `FrameworkElementFactory` 等手工构造（见 `MainWindow.InitializeComponent` / `SwitcherWindow.InitializeComponent`）。少数复杂模板（如切换器的扁平滚动条）用 `XamlReader.Parse` 解析内联 XAML 字符串。

**单例 + 文件持久化**：
- `AppConfig.Instance` — 命令配置；`AppState.Instance` — 每条命令的执行次数/最后执行时间（用于列表排序与统计显示）。
- 数据目录：`%USERPROFILE%\.windows-global-launcher\`（`App.BaseDir`）。
- 配置路径有一层间接：`ConfigPath.txt` 若存在则其内容指向真正的配置文件，否则用默认 `WindowsCommandLauncher.json`。
- `AppConfig` 用 `FileSystemWatcher` 监听配置文件，变更后重新加载并触发 `ConfigUpdated` 事件（热键会据此重新注册）。订阅该事件做 UI 操作时**务必 `Dispatcher.Invoke` 切回 UI 线程**。

**`AppConfig.SaveConfig` 是手写 JSON 字符串**（为了带注释），不是 `JsonSerializer` 输出。修改 `Config`/`ConfigCommand` 结构时必须同步更新这段手写拼接逻辑，否则保存的文件会与模型不一致。

**内置命令**：`config` / `setconfig` / `logs` / `exit` 在 `MainWindow.RefreshCommandList` 中动态注入命令列表，由 `ExecuteAppCommand` 特殊处理，而非走 `Process.Start`。

**两套热键机制（不要混淆）**：
- 命令启动器用 `HotKeyListener`（`user32.dll` 的 `RegisterHotKey` + 隐藏消息窗口接收 `WM_HOTKEY`）。注意系统占用的组合键（如 `Alt+Tab`）无法用 `RegisterHotKey` 注册。
- 切换器用 `KeyboardHook`（`WH_KEYBOARD_LL` 低级键盘钩子），才能拦截并「吞掉」(`return (IntPtr)1`) 系统原生 Alt+Tab。

## Alt+Tab 切换器实现要点

涉及文件：`KeyboardHook.cs`、`WindowEnumerator.cs`、`SwitcherWindow.cs`、`WindowInfo.cs`。

- **钩子回调运行在 UI 线程**（在 UI 线程安装），且系统对回调有超时（`LowLevelHooksTimeout`）。回调本体只做按键判定，所有重活（枚举窗口、显示、激活）通过 `Dispatcher.BeginInvoke` 异步执行。委托实例必须用字段强引用，防止被 GC 回收导致钩子失效。
- **激活态状态机**：`SwitcherWindow._isActive` 是唯一真相源（仅在 UI 线程读写）。钩子通过 `IsSwitcherActive` 委托读取它，决定是否吞掉 Esc / 导航键 / 触发 Commit。交互：Alt+Tab 显示并向后移动，Shift+Tab 向前，激活态下 `↑/↓` 与 `Ctrl+P/Ctrl+N` 也可移动，松开 Alt = Commit（激活选中窗口），Esc = Cancel。
- **窗口过滤**（`WindowEnumerator.IsAltTabWindow`）：可见、有标题、非 `WS_EX_TOOLWINDOW`、顶层（无 owner 或 `WS_EX_APPWINDOW`）、排除 DWM cloaked 的后台 UWP、排除切换器自身。`EnumWindows` 返回 Z 序（≈MRU），默认选中第二项（上一个窗口）。
- **图标提取**优先级：`WM_GETICON` → 类图标 `GCLP_HICON` → 进程 exe 的 `ExtractAssociatedIcon`（仅最后一种按 exe 路径缓存；前两种句柄归窗口/类所有，不可销毁）。
- **窗口激活的前台锁定**（`WindowEnumerator.Activate`）：用 `AttachThreadInput` 把 UI 线程临时附加到前台线程输入队列，再 `SetForegroundWindow`，否则键盘触发切换时会出现「任务栏闪烁但不前置」。兜底用 `SwitchToThisWindow`。
- **键盘钩子与权限**：低级键盘钩子无法拦截发往「比自身完整性级别更高」进程的按键。以管理员运行的游戏（如 Unreal）会绕过钩子触发系统 Alt+Tab，故 `app.manifest` 设为 `requireAdministrator`。
- `SwitcherWindow` 用 `ShowActivated=false` 故意**不抢焦点**，以保持目标窗口的前台历史、让 `SetForegroundWindow` 更可靠；因此键盘输入全程依赖全局钩子而非窗口焦点，`OnDeactivated` 也被刻意置空（不随失焦自动隐藏）。

## 日志

`Logger` 写入 `App.BaseDir` 下按日期轮转的日志（自动清理 3 天前），可通过托盘菜单或内置 `logs` 命令打开。排查运行期问题优先看日志。
