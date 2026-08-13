# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

回答与代码注释统一使用中文（与现有代码风格一致）。

## 项目概览

Windows 平台桌面工具，基于 **.NET 8 WPF**（`net8.0-windows`，同时启用 `UseWPF` 和 `UseWindowsForms`）。包含四个相互独立的功能：

1. **命令启动器**（`MainWindow`）：全局热键（默认 `Ctrl+Shift+I`）弹出的搜索式命令面板，从 JSON 配置读取命令并通过 `Process.Start` 执行。
2. **Alt+Tab 窗口切换器**（`SwitcherWindow`）：接管系统 Alt+Tab，竖向列出当前窗口（图标 + 标题）供切换。
3. **剪贴板历史**（`ClipboardWindow` + `ClipboardHistoryManager`）：后台记录系统复制历史（文本 + 图片），热键（默认 `Ctrl+Alt+C`）弹出紧凑历史面板，回车粘贴回原窗口。
4. **护眼模式**（`EyeCareManager`）：内置「正常」「办公」两种色温/亮度模式，经命令启动器（搜「护眼」）或托盘菜单选择，通过 Magnification 全屏颜色矩阵生效。

前三者由 `App.OnStartup` 同时创建、常驻后台（通过系统托盘 `NotifyIcon` 管理），平时隐藏，靠热键/钩子唤出；护眼模式无独立窗口，启动时恢复上次选择。

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

**内置命令**：`config` / `setconfig` / `logs` / `exit` 与护眼模式命令（`护眼：xxx`，来自 `EyeCareManager.Modes`）在 `MainWindow.RefreshCommandList` 中动态注入命令列表，由 `ExecuteAppCommand` 特殊处理，而非走 `Process.Start`。

**子进程降权（普通用户权限启动）**：见 `MediumIntegrityProcess.cs`。launcher 自身以管理员运行，直接 `Process.Start` 出的子进程会继承管理员令牌。`MainWindow.ExecuteCommandImpl` 默认借用桌面 Shell（explorer.exe）令牌、通过 `MediumIntegrityProcess.Start`（`GetShellWindow` → 复制令牌 → `CreateProcessWithTokenW`）以中等完整性级别启动命令，等同用户桌面双击。`ConfigCommand`/`Command` 的 `RunAsAdmin` 字段（默认 `false`）控制：为 `true` 时走原 `Process.Start` 路径保留管理员权限。降权失败时抛异常 → 上层 `catch` 弹窗报错且不启动（不回退到管理员）。降权路径等价于 `UseShellExecute=false` 的直接 `CreateProcess`，不支持 URL/文档关联启动，也不做 stderr 重定向/退出码监听。新增/修改 `RunAsAdmin` 字段时记得同步 `AppConfig.SaveConfig` 的手写 JSON 拼接（布尔值要输出小写 `true`/`false`）。

**两套热键机制（不要混淆）**：
- 命令启动器用 `HotKeyListener`（`user32.dll` 的 `RegisterHotKey` + 隐藏消息窗口接收 `WM_HOTKEY`）。注意系统占用的组合键（如 `Alt+Tab`）无法用 `RegisterHotKey` 注册。
- 切换器用 `KeyboardHook`（`WH_KEYBOARD_LL` 低级键盘钩子），才能拦截并「吞掉」(`return (IntPtr)1`) 系统原生 Alt+Tab。
- 热键字符串解析统一在 `HotKeyParser.TryParse`（纯静态函数，配套单测），`HotKeyListener` 与 `KeyboardHook` 的动作绑定都经由它解析，修改语法只需改这一处。

**可配置窗口动作热键（如 Alt+Q 关闭前台窗口）**：表驱动结构，涉及 `HotKeyParser.cs`、`KeyboardHook.cs`（`HotKeyActionBinding` + `SetActionBindings`）、`WindowActions.cs`、`SwitcherWindow.cs`。
- 配置文件 `WindowActions` 段（`ConfigWindowAction`：动作名 + 热键字符串 + Enabled）定义绑定；`WindowActions.All` 字典是动作名 → 实现的唯一注册点，**新增动作 = 字典加条目 + 配置文件加一行**。
- `SwitcherWindow.ReloadActionBindings` 负责装配（跳过 Enabled=false / 解析失败 / 未知动作名，记日志），并在 `ConfigUpdated` 时 `Dispatcher.Invoke` 热更新。
- 修饰键**精确匹配**（配置 Alt+Q 时 Alt+Shift+Q 不触发）；命中即吞键。动作 Callback 必须轻量，实际执行统一包 `Dispatcher.BeginInvoke`（钩子回调有 `LowLevelHooksTimeout` 限制）。
- **Win 组合键的开始菜单抑制**：主键被吞掉后系统只看到 Win 按下+松开，会弹出开始菜单；`KeyboardHook` 在命中 Win 绑定时注入无映射掩码键（`keybd_event(0xFF)`，同 AutoHotkey 的 mask key 做法）避免此问题。
- 内置动作：`CloseWindow`（关闭前台窗口，复用 `WindowEnumerator.CloseWindow`）、`VolumeUp`/`VolumeDown`/`ToggleMute`（`keybd_event` 模拟媒体键 `VK_VOLUME_*`）、`ShowClipboardHistory`（唤出剪贴板历史，经 `App.ClipboardHistoryWindow` 静态属性找到窗口实例）。
- 旧配置文件无 `WindowActions` 字段时 `LoadConfig` 自动补默认绑定（`AppConfig.DefaultWindowActions()`）；后续新增动作的默认绑定也要在 `LoadConfig` 里做「缺失则追加」的定向迁移（参照 ShowClipboardHistory 的写法），否则已有配置的老用户拿不到新热键。新增字段须同步 `AppConfig.SaveConfig` 手写 JSON 拼接。

## 剪贴板历史实现要点

涉及文件：`ClipboardHistoryManager.cs`、`ClipboardWindow.cs`、`ClipboardEntry.cs`。

- **监听**：`AddClipboardFormatListener` + WinForms `NativeWindow` 消息窗口收 `WM_CLIPBOARDUPDATE`（同 `HotKeyListener` 的隐藏窗口做法，HwndSource 不支持 message-only parent）。读取剪贴板须在 UI（STA）线程，被占用时重试 5 次（每次间隔 30ms）。
- **内容**：仅文本与图片。超长文本（>5 万字符）与超大图片（PNG >5MB）跳过。去重键：文本直接比较内容，图片比较 PNG 字节的 SHA1；命中已有条目则置顶并刷新时间（Ditto 风格），不重复写文件。粘贴回写剪贴板也会触发监听，靠去重自然收敛，无需抑制标记。
- **持久化**：仿 `AppState` 单例模式，元数据写 `clipboard-history.json`（ UnsafeRelaxedJsonEscaping 保留中文），图片按条目 Id 存 `clipboard-images\{Id}.png`；上限 100 条（`MaxEntries`），淘汰/删除条目时连同 PNG 清理。加载时丢弃图片文件已丢失的条目。
- **唤出与粘贴**：`ShowHistory` 在 `Show()` 前先 `GetForegroundWindow` 记下目标窗口；回车后先 `SetToClipboard` 写回内容，再 `WindowEnumerator.Activate` 恢复前台，延迟 120ms 后 `keybd_event` 模拟 Ctrl+V。
- **弹出位置**：三级回退——`GetGUIThreadInfo` 取前台线程插入符矩形 + `ClientToScreen` 换算；VS Code 等 Electron/Chromium 应用光标自绘取不到，改用 UI Automation（`AutomationElement.FocusedElement` → `TextPattern` 选区的 `GetBoundingRectangles`）；再失败回退「鼠标所在屏幕居中」（同 `MainWindow.CenterWindowOnCurrentScreen`），并钳制在屏幕工作区内。
- **前台激活**：热键经低级钩子到达（输入未真正进入本进程队列，且经 `Dispatcher.BeginInvoke` 异步执行），直接 `Activate`/`SetForegroundWindow` 会被前台锁定间歇性拒绝（非文本输入框时 `PositionWindow` 的 UI Automation 首次调用很慢、进一步拖到锁定武装之后），导致第一次唤出一闪即隐。因此窗口改为 `ShowActivated=false` 不抢焦点，激活统一走 `TryActivateOnce`：用弹出前记录的 `_previousForeground` 取线程 `AttachThreadInput` 附加（同 `WindowEnumerator.Activate` 技巧）→ `BringWindowToTop` → `SetForegroundWindow`，失败时模拟一次 Alt 击发解锁再重试，仍未果则经 `DispatcherTimer` 短间隔重试（上限 `MaxActivationRetries`）。失焦即隐藏（同 `MainWindow`），但显示后有 `ActivationGraceMs` 宽限期，此间失焦视为激活抖动、重试激活而非隐藏。
- **交互**：与命令启动器一致（↑↓/Ctrl+P/Ctrl+N 移动、回车执行、Esc 取消），另支持 Delete 删除选中条目；再按一次热键关闭（切换式）。键盘处理在搜索框 `PreviewKeyDown`，不依赖全局钩子。
- **条目预览**：选中条目时弹出独立预览窗（`_previewWindow`，`ShowActivated=false` 不抢焦点），位于主窗口右侧、放不下翻左侧，钳制在屏幕工作区内（定位统一走 `PlacePreview`）。图片条目尽量按原始像素显示，超过上限（720×560 与工作区 50%/60% 取较小）则等比缩小，原图经 `ClipboardHistoryManager.LoadFullImage` 加载并缓存在条目上（`ClipboardEntry.PreviewImage`）；文本条目单行预览放不下（`Preview.Length > TextPreviewThreshold`）时显示折行完整文本（超长截断至 `TextPreviewMaxChars`，滚轮可滚动，滚动条刻意隐藏——点击会激活预览窗导致主窗口失焦关闭）。注意 `RefreshList` 在 `Show()` 之前触发 `SelectionChanged` 时窗口尚不可见，`ShowHistory` 末尾需补调一次 `UpdatePreview()`。
- **列表滚动条**：刻意不显示——垂直 `Hidden`（保留滚动，键盘导航 `ScrollIntoView` 与滚轮仍可用），水平 `Disabled`（内容约束在列表宽度内，超长文本走省略号截断）。

## Alt+Tab 切换器实现要点

涉及文件：`KeyboardHook.cs`、`WindowEnumerator.cs`、`SwitcherWindow.cs`、`WindowInfo.cs`。

- **钩子回调运行在 UI 线程**（在 UI 线程安装），且系统对回调有超时（`LowLevelHooksTimeout`）。回调本体只做按键判定，所有重活（枚举窗口、显示、激活）通过 `Dispatcher.BeginInvoke` 异步执行。委托实例必须用字段强引用，防止被 GC 回收导致钩子失效。
- **激活态状态机**：`SwitcherWindow._isActive` 是唯一真相源（仅在 UI 线程读写）。钩子通过 `IsSwitcherActive` 委托读取它，决定是否吞掉 Esc / 导航键 / 触发 Commit。交互：Alt+Tab 显示并向后移动，Shift+Tab 向前，激活态下 `↑/↓` 与 `Ctrl+P/Ctrl+N` 也可移动，松开 Alt = Commit（激活选中窗口），Esc = Cancel。
- **窗口过滤**（`WindowEnumerator.IsAltTabWindow`）：可见、有标题、非 `WS_EX_TOOLWINDOW`、顶层（无 owner 或 `WS_EX_APPWINDOW`）、排除 DWM cloaked 的后台 UWP、排除切换器自身。`EnumWindows` 返回 Z 序（≈MRU），默认选中第二项（上一个窗口）。
- **图标提取**优先级：`WM_GETICON` → 类图标 `GCLP_HICON` → 进程 exe 的 `ExtractAssociatedIcon`（仅最后一种按 exe 路径缓存；前两种句柄归窗口/类所有，不可销毁）。
- **窗口激活的前台锁定**（`WindowEnumerator.Activate`）：用 `AttachThreadInput` 把 UI 线程临时附加到前台线程输入队列，再 `SetForegroundWindow`，否则键盘触发切换时会出现「任务栏闪烁但不前置」。兜底用 `SwitchToThisWindow`。
- **键盘钩子与权限**：低级键盘钩子无法拦截发往「比自身完整性级别更高」进程的按键。以管理员运行的游戏（如 Unreal）会绕过钩子触发系统 Alt+Tab，故 `app.manifest` 设为 `requireAdministrator`。
- `SwitcherWindow` 用 `ShowActivated=false` 故意**不抢焦点**，以保持目标窗口的前台历史、让 `SetForegroundWindow` 更可靠；因此键盘输入全程依赖全局钩子而非窗口焦点，`OnDeactivated` 也被刻意置空（不随失焦自动隐藏）。

## 护眼模式实现要点

涉及文件：`EyeCareManager.cs`、`MainWindow.cs`、`AppState.cs`、`App.cs`。

- **实现通道是 Magnification 全屏颜色矩阵**（`MagSetFullscreenColorEffect`，5×5 行主序，输入向量 R,G,B,A,1），**不是** `SetDeviceGammaRamp`：传统 gamma API 在部分机器（HDR/新显卡驱动）上完全失效（实测设置/读取均返回失败）。色温 + 亮度效果统一用对角增益矩阵构造（`BuildMatrix`）。
- **色温算法**：Tanner Helland 黑体近似（`KelvinToRgb`），按 6500K 归一化使 6500K = 恒等（不调节）。
- **颜色效果跨进程残留**：程序退出后矩阵仍生效，因此 `App.OnStartup` 先还原再恢复上次模式（`RestoreLastMode`），`App.OnExit` 必调 `ResetEffect`。
- **模式表内置固定**（参数对照 CareUEyes 官方文档，取日间值；仅保留「正常」「办公」两种）：改模式改 `EyeCareManager.Modes` 一处即可，命令注入/托盘子菜单/持久化都自动跟随。旧版本持久化的已删除模式名（如「智能」）在 `RestoreLastMode` 中找不到对应模式，自然回退为不应用。
- **交互**：与 `config` 等内置命令同机制——`RefreshCommandList` 注入「护眼：xxx」、`ExecuteAppCommand` 分发；托盘「护眼模式」子菜单在 `DropDownOpening` 时按 `EyeCareManager.CurrentModeName` 刷新勾选。当前模式名持久化在 `AppState`（`GetEyeCareMode`/`SetEyeCareMode`）。

## 日志

`Logger` 写入 `App.BaseDir` 下按日期轮转的日志（自动清理 3 天前），可通过托盘菜单或内置 `logs` 命令打开。排查运行期问题优先看日志。

## 其它

- 修改或者新增功能，有必要的话，则同步更新CLAUDE.md和README.md
