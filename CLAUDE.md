# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

回答与代码注释统一使用中文（与现有代码风格一致）。

## 项目概览

Windows 平台桌面工具，基于 **.NET 8 WPF**（`net8.0-windows`，同时启用 `UseWPF` 和 `UseWindowsForms`）。包含七个相互独立的功能：

1. **命令启动器**（`MainWindow`）：全局热键（默认 `Ctrl+Shift+I`）弹出的搜索式命令面板，从 JSON 配置读取命令并通过 `Process.Start` 执行。
2. **Alt+Tab 窗口切换器**（`SwitcherWindow`）：接管系统 Alt+Tab，竖向列出当前窗口（图标 + 标题）供切换。
3. **剪贴板历史**（`ClipboardWindow` + `ClipboardHistoryManager`）：后台记录系统复制历史（文本 + 图片），热键（默认 `Ctrl+Alt+C`）弹出紧凑历史面板，回车粘贴回原窗口。
4. **截图与贴图**（`ScreenshotManager` + `ScreenshotOverlayWindow` + `PinWindow`）：Snipaste 式区域截图（默认 `F4`，框选/窗口吸附/标注/取色，确认后复制到剪贴板或 OCR 识别选区文字）与屏幕贴图（默认 `F7`，剪贴板图片钉为图片贴图，无图片有文字时钉为便签式文字贴图）；`Shift+F7` 整体隐藏/显示所有贴图（新钉贴图自动退出整体隐藏状态）。
5. **护眼模式**（`EyeCareManager`）：内置「正常」「办公」两种色温/亮度模式，经命令启动器（搜「护眼」）或托盘菜单选择，通过 Magnification 全屏颜色矩阵生效。
6. **自动更新**（`UpdateChecker` + `UpdateInstaller` + `UpdateCoordinator` + `UpdateWindow`）：启动后延迟后台检查 GitHub Release（每天最多一次），发现新版本弹窗提示，一键下载校验、自替换 exe 并重启。
7. **开机自启与安装**（`AutoStartManager` + `SingleInstance` + `scripts/install.ps1`）：登录后经计划任务（非注册表 Run 键）自动启动；单实例互斥 + 广播唤起，重复启动时唤醒已运行实例而非起第二个；安装脚本装到 `%LOCALAPPDATA%\Programs`。

前三者由 `App.OnStartup` 同时创建、常驻后台（通过系统托盘 `NotifyIcon` 管理），平时隐藏，靠热键/钩子唤出；截图/贴图为静态类无常驻窗口，由 `WindowActions` 热键或托盘菜单按需触发；护眼模式无独立窗口，启动时恢复上次选择；自动更新在 `App.OnStartup` 末尾 fire-and-forget 后台检查，仅发现新版本时弹提示窗（`UpdateWindow`），无常驻窗口。单实例互斥与开机自启同样无常驻窗口：前者是 `Program.Main` 里的命名 Mutex + 隐藏消息窗口（拿不到互斥量的第二个实例广播唤起后即退出），后者是登录触发的计划任务，运行期不产生任何窗口。

## 常用命令

```bash
dotnet build WindowsGlobalLauncher.sln        # 编译
dotnet test                                    # 运行全部单元测试 (xUnit)
dotnet test --filter "FullyQualifiedName~CommandNameWithHotKeyConverter"  # 运行单个测试
```

发布与本地运行用 PowerShell 脚本（`scripts/publish.ps1`）：会先 `Stop-Process` 掉正在运行的实例，`dotnet publish` 出单文件 exe（`-r win-x64 --self-contained false`，依赖已装 .NET 8 运行时）到 `dist/`，再启动它。

正式发版走 CI：推送 `v1.2.3` 形式的 tag 即触发 `.github/workflows/release.yml` 自动跑单元测试、发布单文件 exe（用 `-p:Version=<tag 去 v 前缀>` 覆盖版本号）并创建 GitHub Release（附 zip 与 sha256，release notes 自动生成）；`scripts/publish.ps1` 仅用于本地开发运行。

一键发版用 `scripts/release.ps1`：自动改 csproj 的 `<Version>` → 跑单元测试 → commit → 打 tag → push 触发 CI（与上面手打 tag 等效，最终都是 push tag 触发 release.yml；中途失败自动回滚本地改动，已 push 的 commit/tag 不回滚）。参数：`-Version 1.2.3` 显式指定或 `-Bump patch|minor|major` 自动递增（默认 `patch`）、`-DryRun` 只打印不执行、`-SkipTests` 跳过测试、`-AllowAnyBranch` 放行非 main 分支发版（默认仅 main）。版本号严格三段校验与 `CompareVersion` 口径一致。

安装用 `scripts/install.ps1`：装到 `%LOCALAPPDATA%\Programs\WindowsGlobalLauncher`（检测/静默安装 .NET 8 桌面运行时 → 停旧实例 → 复制 exe → 建开始菜单快捷方式 → 经 `--install-autostart` 配置开机自启 → 启动）。参数：`-NoAutoStart`（跳过开机自启）、`-DesktopShortcut`（额外建桌面快捷方式）、`-NoLaunch`（装完不启动）、`-Source`/`-Dest`（自定义源/目标目录）。分发包里的 `Install.cmd` 即 `powershell -ExecutionPolicy Bypass -File Install.ps1` 的封装。

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

**内置命令**：`config` / `setconfig` / `logs` / `update` / `autostart` / `exit` 与护眼模式命令（`护眼：xxx`，来自 `EyeCareManager.Modes`）在 `MainWindow.RefreshCommandList` 中动态注入命令列表，由 `ExecuteAppCommand` 特殊处理，而非走 `Process.Start`。其中 `autostart` 切换开机自启（调 `ToggleAutoStart`，与托盘菜单「开机自动启动」共用）。

**子进程降权（普通用户权限启动）**：见 `MediumIntegrityProcess.cs`。launcher 自身以管理员运行，直接 `Process.Start` 出的子进程会继承管理员令牌。`MainWindow.ExecuteCommandImpl` 默认借用桌面 Shell（explorer.exe）令牌、通过 `MediumIntegrityProcess.Start`（`GetShellWindow` → 复制令牌 → `CreateProcessWithTokenW`）以中等完整性级别启动命令，等同用户桌面双击。`ConfigCommand`/`Command` 的 `RunAsAdmin` 字段（默认 `false`）控制：为 `true` 时走原 `Process.Start` 路径保留管理员权限。降权失败时抛异常 → 上层 `catch` 弹窗报错且不启动（不回退到管理员）。降权路径等价于 `UseShellExecute=false` 的直接 `CreateProcess`，不支持 URL/文档关联启动，也不做 stderr 重定向/退出码监听。新增/修改 `RunAsAdmin` 字段时记得同步 `AppConfig.SaveConfig` 的手写 JSON 拼接（布尔值要输出小写 `true`/`false`）。

**两套热键机制（不要混淆）**：
- 命令启动器用 `HotKeyListener`（`user32.dll` 的 `RegisterHotKey` + 隐藏消息窗口接收 `WM_HOTKEY`）。注意系统占用的组合键（如 `Alt+Tab`）无法用 `RegisterHotKey` 注册。
- 切换器用 `KeyboardHook`（`WH_KEYBOARD_LL` 低级键盘钩子），才能拦截并「吞掉」(`return (IntPtr)1`) 系统原生 Alt+Tab。
- 热键字符串解析统一在 `HotKeyParser.TryParse`（纯静态函数，配套单测），`HotKeyListener` 与 `KeyboardHook` 的动作绑定都经由它解析，修改语法只需改这一处。
- **命令启动器热键唤出的激活加固**（`ForegroundActivator.cs`）：`RegisterHotKey` 注册成功、`WM_HOTKEY` 也到达，但 `Show()` 自带的激活会被前台锁定间歇性拒绝，产生「短暂激活→立刻失活」抖动，`OnDeactivated` 无条件 `HideWindow()` 又把刚显示的窗口隐藏，用户看到「按热键完全没反应」。解法：`ShowActivated=false` 不靠 Show 抢焦点，激活统一走 `ForegroundActivator.ForceForeground`（AttachThreadInput 绕前台锁定，附加前先 `IsWindowHung` 探测），失败按 60ms 间隔重试至多 8 次；显示后 600ms 宽限期内失焦视为抖动、重试激活而非隐藏。与剪贴板历史窗口同一套策略，公共逻辑（AttachThreadInput + Alt 解锁重试 + 挂起探测 + `SwitchToThisWindow` 兜底）抽到 `ForegroundActivator`、两个窗口共用，宽限期/重试次数这类交互策略各窗口自留。另去掉了构造函数里的 `WindowState.Minimized` 初始态（只 Hide 即可）：否则首次唤出要走「Show(最小化)→还原」两段状态切换，且进程首次 `ShowWindow` 的 nCmdShow 会被 STARTUPINFO 的 `wShowWindow` 替换（同 `WindowEnumerator.Activate` 的既有坑）。
- **热键注册失败会弹托盘气泡提示**：此前只写日志、用户完全看不到，而热键是本程序唯一入口。`RegisterLauncherHotKey` 里配置热键失败先退回默认热键并气泡告知，两者都失败则气泡提示改用托盘图标唤出或换绑。

**可配置窗口动作热键（如 Alt+Q 关闭前台窗口）**：表驱动结构，涉及 `HotKeyParser.cs`、`KeyboardHook.cs`（`HotKeyActionBinding` + `SetActionBindings`）、`WindowActions.cs`、`SwitcherWindow.cs`。
- 配置文件 `WindowActions` 段（`ConfigWindowAction`：动作名 + 热键字符串 + Enabled）定义绑定；`WindowActions.All` 字典是动作名 → 实现的唯一注册点，**新增动作 = 字典加条目 + 配置文件加一行**。
- `SwitcherWindow.ReloadActionBindings` 负责装配（跳过 Enabled=false / 解析失败 / 未知动作名，记日志），并在 `ConfigUpdated` 时 `Dispatcher.Invoke` 热更新。
- 修饰键**精确匹配**（配置 Alt+Q 时 Alt+Shift+Q 不触发）；命中即吞键。动作 Callback 必须轻量，实际执行统一包 `Dispatcher.BeginInvoke`（钩子回调有 `LowLevelHooksTimeout` 限制）。
- **Win 组合键的开始菜单抑制**：主键被吞掉后系统只看到 Win 按下+松开，会弹出开始菜单；`KeyboardHook` 在命中 Win 绑定时注入无映射掩码键（`keybd_event(0xFF)`，同 AutoHotkey 的 mask key 做法）避免此问题。
- 内置动作：`CloseWindow`（关闭前台窗口，复用 `WindowEnumerator.CloseWindow`）、`VolumeUp`/`VolumeDown`/`ToggleMute`（`keybd_event` 模拟媒体键 `VK_VOLUME_*`）、`ShowClipboardHistory`（唤出剪贴板历史，经 `App.ClipboardHistoryWindow` 静态属性找到窗口实例）、`Screenshot`/`PinClipboard`（区域截图/剪贴板贴图，调 `ScreenshotManager` 静态方法；默认绑定裸 `F4`/`F7`——无修饰键热键只能走本表驱动路径，`HotKeyListener.RegisterHotKey` 拒绝无修饰键；注意裸键命中即全局吞掉，Excel 等应用内 F4 会失效，属用户已确认的取舍）、`TogglePinVisibility`（整体隐藏/显示所有贴图，调 `PinWindow.ToggleAllVisibility`；默认 `Shift+F7`，与裸 `F7` 的 `PinClipboard` 修饰键精确匹配互不冲突）。
- 旧配置文件无 `WindowActions` 字段时 `LoadConfig` 自动补默认绑定（`AppConfig.DefaultWindowActions()`）；后续新增动作的默认绑定也要在 `LoadConfig` 里做「缺失则追加」的定向迁移（参照 ShowClipboardHistory 的写法），否则已有配置的老用户拿不到新热键。新增字段须同步 `AppConfig.SaveConfig` 手写 JSON 拼接。

## 剪贴板历史实现要点

涉及文件：`ClipboardHistoryManager.cs`、`ClipboardWindow.cs`、`ClipboardEntry.cs`。

- **监听**：`AddClipboardFormatListener` + WinForms `NativeWindow` 消息窗口收 `WM_CLIPBOARDUPDATE`（同 `HotKeyListener` 的隐藏窗口做法，HwndSource 不支持 message-only parent）。读取剪贴板须在 UI（STA）线程，被占用时重试 5 次（每次间隔 30ms）。
- **内容**：仅文本与图片。超长文本（>5 万字符）与超大图片（PNG >5MB）跳过。去重键：文本直接比较内容，图片比较 PNG 字节的 SHA1；命中已有条目则置顶并刷新时间（Ditto 风格），不重复写文件。粘贴回写剪贴板也会触发监听，靠去重自然收敛，无需抑制标记。
- **持久化**：仿 `AppState` 单例模式，元数据写 `clipboard-history.json`（ UnsafeRelaxedJsonEscaping 保留中文），图片按条目 Id 存 `clipboard-images\{Id}.png`；上限 100 条（`MaxEntries`），淘汰/删除条目时连同 PNG 清理。加载时丢弃图片文件已丢失的条目。
- **唤出与粘贴**：`ShowHistory` 在 `Show()` 前先 `GetForegroundWindow` 记下目标窗口；回车后先 `SetToClipboard` 写回内容，再 `WindowEnumerator.Activate` 恢复前台，延迟 120ms 后 `keybd_event` 模拟 Ctrl+V。
- **弹出位置**：三级回退——`GetGUIThreadInfo` 取前台线程插入符矩形 + `ClientToScreen` 换算；VS Code 等 Electron/Chromium 应用光标自绘取不到，改用 UI Automation（`AutomationElement.FocusedElement` → `TextPattern` 选区的 `GetBoundingRectangles`）；再失败回退「鼠标所在屏幕居中」（同 `MainWindow.CenterWindowOnCurrentScreen`），并钳制在屏幕工作区内。UIA 是跨进程 COM 调用、无超时，目标应用（VS Code 等 Electron）卡顿时会长时间阻塞 UI 线程，故 `TryGetCaretViaUIA` 改为 `Task.Run` 后台执行 + `Wait(300ms)`（常量 `UiaTimeoutMs`）短超时，超时/失败静默回退居中定位并记 WARN；只把 `Point?` 纯数据传回 UI 线程，`AutomationElement` 等 COM 对象留在后台线程。
- **前台激活**：热键经低级钩子到达（输入未真正进入本进程队列，且经 `Dispatcher.BeginInvoke` 异步执行），直接 `Activate`/`SetForegroundWindow` 会被前台锁定间歇性拒绝（非文本输入框时 `PositionWindow` 的 UI Automation 首次调用很慢、进一步拖到锁定武装之后），导致第一次唤出一闪即隐。因此窗口改为 `ShowActivated=false` 不抢焦点，激活统一走 `TryActivateOnce` → `ForegroundActivator.ForceForeground`（与命令启动器共用同一实现）：用弹出前记录的 `_previousForeground` 取线程 `AttachThreadInput` 附加（同 `WindowEnumerator.Activate` 技巧）→ `BringWindowToTop` → `SetForegroundWindow`，失败时模拟一次 Alt 击发解锁再重试，仍未果则由调用方经 `DispatcherTimer` 短间隔重试（上限 `MaxActivationRetries`）。附加前同样先 `ForegroundActivator.IsWindowHung` 探测弹出前前台窗口线程是否挂起，挂起则跳过 attach、改走 `SwitchToThisWindow` 兜底（避免共享输入队列把 UI 线程一起拖死，见切换器小节）；attach/detach 严格 `try/finally` 配对，异常路径也保证解除附加。失焦即隐藏（同 `MainWindow`），但显示后有 `ActivationGraceMs` 宽限期，此间失焦视为激活抖动、重试激活而非隐藏。
- **交互**：与命令启动器一致（↑↓/Ctrl+P/Ctrl+N 移动、回车执行、Esc 取消），另支持 Delete 删除选中条目；再按一次热键关闭（切换式）。键盘处理在搜索框 `PreviewKeyDown`，不依赖全局钩子。
- **条目预览**：选中条目时弹出独立预览窗（`_previewWindow`，`ShowActivated=false` 不抢焦点），位于主窗口右侧、放不下翻左侧，钳制在屏幕工作区内（定位统一走 `PlacePreview`）。图片条目尽量按原始像素显示，超过上限（720×560 与工作区 50%/60% 取较小）则等比缩小，预览图经 `ClipboardHistoryManager.LoadFullImage` 加载并缓存在条目上（`ClipboardEntry.PreviewImage`）。`ShowImagePreview` 先按预览上限（含 DPI 换算）算出 `maxPixelWidth` 传给 `LoadFullImage`，`LoadBitmap` 仅在原图更宽时才设 `DecodePixelWidth`（只降不升），避免接近 5MB 上限的大图在 UI 线程全量解码造成长阻塞；`ClipboardEntry.PreviewImageDecodedWidth`（`JsonIgnore`）记录实际解码宽度，复用缓存要求缓存宽度 ≥ 当前需求、否则按更大宽度重解码。`SetToClipboard`（全尺寸）与列表缩略图（52px）语义不变；文本条目单行预览放不下（`Preview.Length > TextPreviewThreshold`）时显示折行完整文本（超长截断至 `TextPreviewMaxChars`，滚轮可滚动，滚动条刻意隐藏——点击会激活预览窗导致主窗口失焦关闭）。注意 `RefreshList` 在 `Show()` 之前触发 `SelectionChanged` 时窗口尚不可见，`ShowHistory` 末尾需补调一次 `UpdatePreview()`。
- **列表滚动条**：刻意不显示——垂直 `Hidden`（保留滚动，键盘导航 `ScrollIntoView` 与滚轮仍可用），水平 `Disabled`（内容约束在列表宽度内，超长文本走省略号截断）。

## 截图与贴图实现要点

涉及文件：`ScreenshotManager.cs`（编排 + `SnipAction`/`SnipResult` 契约）、`ScreenCapture.cs`（抓屏）、`WindowRectSnapshot.cs`（窗口吸附）、`ScreenshotOverlayWindow.cs`（全屏遮罩交互）、`AnnotationController.cs`（标注层）、`PinWindow.cs`（贴图浮窗）、`ScreenshotGeometry.cs`（纯几何/格式化，配套单测 `ScreenshotGeometryTests`）。

- **DPI 前提：app.manifest 声明 PerMonitorV2**（为截图新增）。全进程坐标语义：`Screen`/`Cursor.Position`/抓屏矩形 = 物理像素，WPF `Left/Top/Width/Height` = DIP，换算因子 `VisualTreeHelper.GetDpi(window)`。现有窗口的「物理 ÷ 窗口 DPI」换算数学在 PMv2 下比原 system-aware 更精确，无需改动。
- **单一遮罩窗口覆盖整个虚拟屏**：PMv2 窗口 DWM 不做位图缩放（缓冲区 1:1 映射物理像素），故 `SetWindowPos` 以物理像素铺满虚拟屏后，所有换算只有一条公式 `DIP = (物理 − 虚拟屏原点) / 窗口scale`，冻结帧在所有屏（含混合 DPI）上像素精确。**选区真相源是虚拟屏物理像素 `System.Drawing.Rectangle`**，渲染时才换算 DIP。**遮罩窗口严禁订阅 `DpiChanged` 并在处理器里改布局**：系统会派发一次虚假 DpiChanged（Old==New==当前缩放），处理器中调用布局修改（如 ApplyLayout 设置 Canvas/Image 尺寸）会让 WPF 对该全屏窗口做一次「DPI 倍数」的二次缩放——屏幕左上出现黑边、内容放大 1.25 倍（对照实验：空操作处理器或不订阅均正常）。窗口每次截图新建、定位后不移动，DPI 恒定，无需响应。
- **先冻结再框选**（Snipaste 做法）：`Graphics.CopyFromScreen` 一次抓整个虚拟屏 → `CreateBitmapSourceFromHBitmap`（`DeleteObject` 释放 GDI 句柄）→ Freeze。遮罩、放大镜取色、标注、输出全部基于冻结帧。**护眼模式的 Magnification 颜色矩阵会包含在抓屏结果里**（实测 Win10 19045：`CopyFromScreen` 拿到的是矩阵处理后的像素）——为保证成品图/取色为真实色彩，`StartCapture` 在抓屏前 `EyeCareManager.SuspendEffect()` 临时写恒等矩阵（不改 CurrentModeName/持久化）、`DwmFlush()`×2 等 DWM 合成生效后抓屏，`finally` 中 `ResumeEffect()` 立即恢复；恢复后遮罩显示冻结帧时矩阵恰好生效一次，观感与平时护眼桌面一致。挂起失败（Mag API 失败）时降级为直接抓屏（含护眼色彩）并记 WARN。抓屏 INFO 日志带当前护眼模式名与是否挂起。
- **窗口吸附**：遮罩弹出前 `WindowRectSnapshot.Capture` 一次性 EnumWindows（Z 序）+ `DWMWA_EXTENDED_FRAME_BOUNDS` 缓存矩形快照，鼠标移动时命中测试（比 Alt+Tab 过滤更宽松：不查 owner，但排除本进程窗口）。
- **遮罩激活与键盘焦点**：截图由低级钩子触发（输入未进入本进程队列），`OnOverlayLoaded` 里直接 `Activate()` 会被前台锁定拒绝、窗口弹出却拿不到键盘焦点（表现为 Esc 无法取消截图）。故复用 `WindowEnumerator.Activate(hwnd)` 的 AttachThreadInput 技巧绕过前台锁定后再 `Focus()`——与剪贴板窗口 `TryActivateOnce` 同一问题同一解法。遮罩销毁前（`OnClosing`，此刻仍全屏覆盖）先把 `StartCapture` 记录的截图前前台窗口（`_previousForeground`，经 `IsWindow` 校验存活后 `Activate`）激活到遮罩之下，遮罩销毁时自身已非活动窗口、系统不再自行挑窗口激活，消除关闭后「先激活错误窗口再跳回」的闪屏；`HandleResult` 的 finally 仅作幂等兜底（正常路径下句柄已置空）与 OCR 清理（OCR 结果窗 `OcrResultWindow` 需要键盘焦点，不归还）。
- **遮罩状态机**：Hovering（吸附高亮 + 放大镜取色，`C`/`Shift+C` 复制 HEX/RGB）→ Dragging（拖拽 <4px 视为点击 = 选中悬停矩形）→ Selected（8 手柄 + 方向键微调 + 工具条）→ Annotating（工具激活即选区锁定，鼠标转发 `AnnotationController`）。`Completed` 事件**恰好触发一次**且**在 OnClosed 之后统一派发**——确认结果先暂存 `_pendingResult` 再 `Close()`，因为 `HandleResult` 里的 `SaveFileDialog` 是模态对话框，必须等全屏 Topmost 遮罩关闭后再弹，否则被挡住像卡死。
- **双击选区 = 复制确认**：在 `PreviewMouseLeftButtonDown`（隧道阶段）统一拦截 `ClickCount == 2`，Selected 与 Annotating 态均生效。**必须用 Preview 而非 bubbling**：`EnterSelected` 末尾 `RestoreLastTool` 会自动恢复上次标注工具直接进入 Annotating 态，bubbling 阶段鼠标已被转发给标注层、文字工具首击创建的 TextBox 还会吃掉第二击。三处显式排除：工具条内的点击（Preview 先于工具条自身的 Handled 标记，且工具条可能摆在选区内部，快速连点撤销/颜色不能误确认）、选区手柄、正在编辑的**非空**文字框（用户双击选词）；空文字框（双击首击刚创建的）放行，由 `Finish` 内 `CommitPendingText` 丢弃。命中后 `e.Handled = true` 抑制 bubbling，避免第二击被当成移动选区/开始标注。形状类工具双击首击产生的退化元素由 `AnnotationController.OnMouseUp` 的误点击阈值（MinShapeSize 等）自然丢弃。从 Hovering 直接双击某窗口 = 首击选中 + 次击确认，等效「双击窗口即截取该窗口」。
- **输出合成**：`CroppedBitmap` 裁冻结帧 + `VisualBrush`（`ViewboxUnits=Absolute`，Viewbox=选区 DIP）截取标注 Canvas，`RenderTargetBitmap(selW, selH, 96×scale, 96×scale)` 渲染——输出像素尺寸精确等于选区物理尺寸。复制走 `Clipboard.SetImage`，剪贴板历史经 `WM_CLIPBOARDUPDATE` 自动捕获（SHA1 去重），无需额外登记。
- **标注层**（`AnnotationController`）：挂在宿主 Canvas 上（`IsHitTestVisible=false`，鼠标由遮罩统一转发 DIP 坐标）；矩形/椭圆/箭头（`ScreenshotGeometry.BuildArrowPolygon` 七点实心多边形）/画笔（抽稀 Polyline）/文字（编辑中 TextBox，落定转 TextBlock；TextBox 的 Enter/Esc 自行 Handled，遮罩 PreviewKeyDown 见 `Keyboard.FocusedElement is TextBox` 即放行）；撤销栈只含已落定元素；`Clear` 用 `_ownedElements` 集合只删自己创建的。
- **标注参数可调与工具条**：线宽/字号已从硬编码常量改为可调（`StrokeWidth`/`TextFontSize` 只读属性 + `AdjustStrokeWidth`/`AdjustTextFontSize`，范围 1–12 / 8–48，滚轮步进 1）；遮罩窗口 `PreviewMouseWheel` 跟随当前工具——线条类（矩形/椭圆/箭头/画笔）调线宽、文字调字号、未选工具不响应；调整只作用于「正在绘制/编辑的对象」，或（无正在绘制/编辑时）「刚刚绘制的那个」，两者互斥、不回写更早的历史对象（拖拽中形状/编辑中文字框实时跟随；无拖拽/编辑时最近落定的元素跟随；箭头经 `_arrowEndpoints` 端点重算）；颜色（`StrokeColor`）同逻辑——选色后正在绘制/编辑的对象实时变色，无正在绘制时最近落定的元素变色。已落定元素支持点击拖动移动（`HitTestAnnotation` 几何命中测试，后画先命中；矩形/椭圆/文字改 Canvas 坐标、箭头/画笔平移点集，箭头移动时同步端点）。工具条按钮统一 30×30、单色简洁图标（彩色 emoji 已替换为 Segoe MDL2 Assets/文本），工具条内常驻「粗细 N / 字号 N」指示器（`_settingIndicator` + `UpdateSettingIndicator`，变化后经 `RepositionToolbar` 重排）。颜色/线宽/字号/工具跨会话持久化在 `AppState`（`GetAnnotation*`/`SetAnnotationSettings`）：截图窗口构造时读取恢复颜色（`ScreenshotGeometry.ParseHex`）/线宽/字号、`EnterSelected` 时按 `AnnotationTool` 名恢复上次工具并自动激活、`OnClosed` 时一次性写回。
- **贴图浮窗**（`PinWindow`）：单类双模式（内部 `ContentMode` 枚举 + 静态工厂 `FromImage`/`FromText`，共同部分抽 `InitChrome`）。图片模式：构造即 `Show()`，1:1 物理像素显示（基准 DIP = `PixelWidth / DpiScale`，**必须用 PixelWidth**——剪贴板图片 DPI 元数据会让 `Width` 不可靠），`Loaded` 后重读 DPI 校正一次；滚轮缩放以光标为锚（10%~500%）、`Ctrl+滚轮` 调透明度；右键菜单（复制图像/保存 PNG/缩放100%/关闭/关闭所有）。文本模式（便签）：深色底白字（同剪贴板历史预览窗画刷）+ 圆角 8 + 内边距 10，宽固定 480 DIP（受窗口当前所在屏工作区约束）、高按内容 `Measure` 后钳制（下限 20 DIP、上限工作区高 60%），超出由 ScrollViewer 滚动（`ApplyTextSize`，构造、Loaded DPI 校正、编辑落定各调一次——文本尺寸为 DIP 语义、DPI 无关，但高度钳制依赖 scale 且构造时 GetDpi 可能取到非目标显示器）；**嵌套 `TextScrollViewer` 拦下 Ctrl+滚轮**（ScrollViewer 类处理器不检查修饰键、Ctrl 时也会照常滚动，不拦的话调透明度的同时文本会滚；拦截方式是不消费、不标 Handled，让事件冒泡到 Window 级处理）；**`OnMouseLeftButtonDown` 也刻意空实现**——ScrollViewer 基类按下时会 `Focus()` 并标 Handled（自身不可聚焦时焦点落到窗口本身、Focus 仍返回 true），事件到不了窗口级 handler，表现为文本区无法拖动/双击关闭（边缘内边距不经 ScrollViewer 所以正常）；**滚动条用深色扁平样式**（同切换器 `ApplyFlatScrollBar` 先例的 XAML，`TextScrollViewer` 构造时注入 Resources：透明窄轨 + 细圆角半透明白 Thumb、隐藏箭头，替换默认白色滚动条，编辑态 TextBox 内部滚动条经逻辑树 Resources 查找同样生效）；右键菜单（编辑/复制文本/保存 txt/关闭/关闭所有）。**编辑态**：`EnterEditMode` 把 `_textScroll.Content` 从 TextBlock 换成 TextBox（透明底无边框融入深色底，`CaretBrush` 设白——默认黑色光标在深色底上看不清），描边变蓝提示、窗口 ContextMenu 置空让位给 TextBox 默认剪切/复制/粘贴菜单、`SelectAll` 全选；Enter 落定保存 / Shift+Enter 换行 / Esc 取消恢复原文——`PreviewKeyDown` 隧道阶段拦截并标 Handled（阻止 Enter 被 TextBox 换行处理、Esc 冒泡到窗口级关闭），点击别处失焦自动保存；`ExitEditMode` 先置 `_isEditing=false` 再切换 Content（防切换引发的 LostKeyboardFocus 重入），保存后 `ApplyTextSize` 重测高度；编辑态下窗口不响应拖动/双击关闭/Esc 关闭。两模式共享：双击/Esc 关闭、左键拖动、悬停描边变蓝、提示角标、`ClampOutsideToNearestScreen`。静态 `_open` 列表跟踪全部实例（构造加入、`Closed` 移除，两模式混合计数），`CloseAll` 遍历副本两类都关。**整体隐藏/显示**（`Shift+F7` → `ToggleAllVisibility`）：静态 `_allHidden` 标记是唯一真相源（不变式：true ⇒ `_open` 非空且全部隐藏；`HideAll` 置位、`ShowAll` 复位、`Closed` 中 `_open` 清空时复位）；`Hide()` 不触发 `Closed`、窗口留在 `_open`，`Show()` 恢复原位置/尺寸/透明度（重触发 `Loaded` 的 DPI 重校幂等无害）；两模式共用混合列表无需特判；构造函数末尾 `ShowInitially()`——整体隐藏状态下新钉贴图先 `ShowAll()` 把旧贴图连同自己（此刻已在 `_open`）一并恢复显示，即「钉图自动退出整体隐藏状态」；文本便签编辑中隐藏经失焦自动保存；托盘菜单「隐藏/显示所有贴图」在 `Opening` 时按 `IsAllHidden`/`OpenCount` 刷新文案与置灰。
- **热键**：`Screenshot`(F4)/`PinClipboard`(F7) 走 `WindowActions` 表驱动路径（见上文窗口动作小节），入口 `ScreenshotManager.StartCapture`/`PinFromClipboard` 仅 UI 线程调用；`IsCapturing` 防截图会话重入。**截图会话进行中按 F7 = 钉图当前选区**：F7 被全局钩子吞掉、到不了遮罩窗口，`PinFromClipboard` 检测 `IsCapturing` 且 `_activeOverlay` 非空时转发 `overlay.PinCurrentSelection()`（Selected/Annotating 态等同点工具条 📌，未框选则忽略记 INFO）。**F7 贴图图片优先、无图片有文字则钉文字便签**：文字经 `ContainsText`/`GetText` 读取，空/全空白或超长（`MaxPinTextLength = 50_000`，与剪贴板历史上限语义一致）忽略记 INFO；所有过滤与读取策略留在 `ScreenshotManager`，`PinWindow.FromText` 保证收到非空且 ≤50k 的文本。剪贴板读写统一「`ExternalException` 重试 3 次 × 50ms」。
- **OCR 识别选区文字**（`OcrService` + `OcrResultWindow`，工具条 🔤 触发 `Finish(SnipAction.Ocr)`）：识别源是**纯冻结帧选区裁剪**——`CroppedBitmap`（冻结帧, 选区物理矩形相对虚拟屏原点, 与虚拟屏求交防越界）Freeze 后直接作为 `SnipResult.Image`，**不含标注、不含压暗层**（标注图形会干扰识别；护眼矩阵抓屏前已挂起故颜色干净）；非 OCR 动作仍走含标注的 `RenderTargetBitmap` 合成。图像不做任何预处理（交给 RapidOCR 自带预处理）——**勿加「深色反色/自适应放大」类预处理**：实测反而降低识别率（反色误伤混合底色、插值放大让笔画发虚），已试过并回退。结果窗可编辑/复制（`Ctrl+Enter` 复制并关窗）；`ScreenshotManager.RunOcr` 用 `async void` fire-and-forget，图片为 null 或识别失败也会 `SetResult(null)`，弹窗不卡加载态。
- **OCR 单引擎（RapidOCR / PP-OCR，无 Windows OCR 回退）**：`RapidOcrBackend` 常驻子进程经 stdin/stdout 传 JSON，`--ensureAscii=1` 规避编码、`--maxSideLen=2048`；`StandardInputEncoding` 必须用 `new UTF8Encoding(false)`（无 BOM）——`Encoding.UTF8` 带 BOM，`StandardInput` 的 `StreamWriter` 首次 Flush 会把 BOM 写到 stdin 首行 JSON 之前，引擎按 `jsonIn[0]=='{'` 判定失败 → 首次识别必失败、第二次才成功；安装检测 = 数据目录 `ocr-engine\` 递归找 exe（结果缓存，`InvalidateInstallCache()` 刷新）。`OcrService` 是薄门面：`IsReady => RapidOcrBackend.IsInstalled`，`RecognizeAsync` 包装 `RapidOcrBackend.RecognizeAsync`（`""` → null、异常兜底记日志）。引擎包（约 70MB，GitHub 直连 + 镜像多源，SharpCompress 解 7z）在 `App.OnStartup` 末尾 fire-and-forget 后台下载：`OcrEngineInstaller.EnsureInstalledAsync` 合流（lock + `_currentInstall`，多处并发共享同一进行中任务、不重复下载），成功记 INFO「识图引擎后台下载完成」、失败记 WARN 不弹窗。未就绪时 `RunOcr` 先 `SetDownloading()`（主文本区只读显示「识图引擎正在下载…」+ 订阅 `StatusChanged` 实时刷新进度，`Dispatcher` 切回 UI 线程、`OnClosed` 退订防泄漏），下载失败 `SetEngineUnavailable(reason)` 展示原因 + `ManualInstallHint` + 「重试下载」链接按钮（重试成功显示「引擎已就绪，请关闭后重新识别」）。`App.OnExit` 调 `RapidOcrBackend.Shutdown()` 杀常驻子进程（幂等，try/catch 不阻断其它清理）。**子进程防孤儿双保险**：优雅退出走 `Shutdown()`，但主程序被强杀（`scripts/publish.ps1` 每次发布都 `Stop-Process -Force`）/ 崩溃 / 任务管理器结束时 `OnExit` 不会执行，子进程会成为孤儿累积——① `StartProcessAsync` 启动后立即 `ChildProcessJob.TryAssign` 把子进程挂入带 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的 Job 对象（`ChildProcessJob.cs`，Job 句柄**故意永不关闭**，主进程以任何方式退出时内核关句柄即自动杀掉挂入的子进程；挂入失败只记 WARN 降级）；② `App.OnStartup` 后台调一次 `RapidOcrBackend.KillOrphanedEngines()` 清扫历史孤儿（判定 = 进程名以 RapidOCR 开头 + exe 路径位于数据目录 `ocr-engine\` 之下，按目录边界前缀匹配，跳过当前常驻进程，绝不抛异常）。

## Alt+Tab 切换器实现要点涉及文件：`KeyboardHook.cs`、`WindowEnumerator.cs`、`SwitcherWindow.cs`、`WindowInfo.cs`。

- **钩子回调运行在 UI 线程**（在 UI 线程安装），且系统对回调有超时（`LowLevelHooksTimeout`）。回调本体只做按键判定，所有重活（枚举窗口、显示、激活）通过 `Dispatcher.BeginInvoke` 异步执行。委托实例必须用字段强引用，防止被 GC 回收导致钩子失效。
- **激活态状态机**：`SwitcherWindow._isActive` 是唯一真相源（仅在 UI 线程读写）。钩子通过 `IsSwitcherActive` 委托读取它，决定是否吞掉 Esc / 导航键 / 触发 Commit。交互：Alt+Tab 显示并向后移动，Shift+Tab 向前，激活态下 `↑/↓` 与 `Ctrl+P/Ctrl+N` 也可移动，松开 Alt = Commit（激活选中窗口），Esc = Cancel。
- **窗口过滤**（`WindowEnumerator.IsAltTabWindow`）：可见、有标题、非 `WS_EX_TOOLWINDOW`、顶层（无 owner 或 `WS_EX_APPWINDOW`）、排除 DWM cloaked 的后台 UWP、排除切换器自身。`EnumWindows` 返回 Z 序（≈MRU），默认选中第二项（上一个窗口）。
- **图标提取**优先级：`WM_GETICON` → 类图标 `GCLP_HICON` → 进程 exe 的 `ExtractAssociatedIcon`（仅最后一种按 exe 路径缓存；前两种句柄归窗口/类所有，不可销毁）。
- **窗口激活的前台锁定**（`WindowEnumerator.Activate`）：用 `AttachThreadInput` 把 UI 线程临时附加到前台线程输入队列，再 `SetForegroundWindow`，否则键盘触发切换时会出现「任务栏闪烁但不前置」。兜底用 `SwitchToThisWindow`。`Activate` 中 `ShowWindow(SW_SHOW)` 仅对不可见窗口调用——进程首次 `ShowWindow` 调用的 nCmdShow 会被 STARTUPINFO 的 `wShowWindow` 替换（SW_SHOW→SW_SHOWNORMAL），会把最大化的目标窗口意外还原（实测启动后首次激活必现）。
- **附加前先探测目标线程是否挂起（`IsWindowHung`）**：`AttachThreadInput` 是共享输入队列，若目标窗口无响应（例如刚被 `CloseWindow` 动作请求关闭、正在退出或已挂起），附加会把本线程一起拖死——无异常、无日志、只能强杀；且低级键盘钩子装在同一 UI 线程，UI 线程挂死后所有热键全部失效。故附加前用 `SendMessageTimeout` 发 `WM_NULL` + `SMTO_ABORTIFHUNG`（200ms 超时）探测；`WM_NULL` 返回值恒为 0，无法用返回值区分成功与超时，须给 P/Invoke 声明加 `SetLastError = true`、用 `Marshal.GetLastWin32Error() == ERROR_TIMEOUT`(1460) 判定。探测到挂起就跳过 attach、改走不附加兜底（`SetForegroundWindow` + `SwitchToThisWindow`）并记 WARN。attach/detach 严格 `try/finally` 配对，异常路径也保证解除附加（原先 detach 在 try 内，异常会让线程永久处于 attached 状态）。
- **键盘钩子与权限**：低级键盘钩子无法拦截发往「比自身完整性级别更高」进程的按键。以管理员运行的游戏（如 Unreal）会绕过钩子触发系统 Alt+Tab，故 `app.manifest` 设为 `requireAdministrator`。
- `SwitcherWindow` 用 `ShowActivated=false` 故意**不抢焦点**，以保持目标窗口的前台历史、让 `SetForegroundWindow` 更可靠；因此键盘输入全程依赖全局钩子而非窗口焦点，`OnDeactivated` 也被刻意置空（不随失焦自动隐藏）。

## 护眼模式实现要点

涉及文件：`EyeCareManager.cs`、`MainWindow.cs`、`AppState.cs`、`App.cs`。

- **实现通道是 Magnification 全屏颜色矩阵**（`MagSetFullscreenColorEffect`，5×5 行主序，输入向量 R,G,B,A,1），**不是** `SetDeviceGammaRamp`：传统 gamma API 在部分机器（HDR/新显卡驱动）上完全失效（实测设置/读取均返回失败）。色温 + 亮度效果统一用对角增益矩阵构造（`BuildMatrix`）。
- **色温算法**：Tanner Helland 黑体近似（`KelvinToRgb`），按 6500K 归一化使 6500K = 恒等（不调节）。
- **颜色效果跨进程残留**：程序退出后矩阵仍生效，因此 `App.OnStartup` 先还原再恢复上次模式（`RestoreLastMode`），`App.OnExit` 必调 `ResetEffect`。
- **模式表内置固定**（参数对照 CareUEyes 官方文档，取日间值；仅保留「正常」「办公」两种）：改模式改 `EyeCareManager.Modes` 一处即可，命令注入/托盘子菜单/持久化都自动跟随。旧版本持久化的已删除模式名（如「智能」）在 `RestoreLastMode` 中找不到对应模式，自然回退为不应用。
- **交互**：与 `config` 等内置命令同机制——`RefreshCommandList` 注入「护眼：xxx」、`ExecuteAppCommand` 分发；托盘「护眼模式」子菜单在 `DropDownOpening` 时按 `EyeCareManager.CurrentModeName` 刷新勾选。当前模式名持久化在 `AppState`（`GetEyeCareMode`/`SetEyeCareMode`）。

## 自动更新与发布实现要点

涉及文件：`UpdateChecker.cs`（UpdateInfo/UpdateCheckResult 模型 + GitHub API 查询 + 版本比较 + 节流/跳过状态）、`UpdateInstaller.cs`（下载/校验/解压/自替换重启/残留清理）、`UpdateCoordinator.cs`（自动/手动两种检查策略编排）、`UpdateWindow.cs`（更新提示窗）、`StartupArgs.cs`（命令行参数统一解析）、`App.cs`（版本号读取 + OnStartup 末尾 fire-and-forget 检查）、`Program.cs`（Main 收参/等旧进程/清理残留）、`AppState.cs`（LastUpdateCheckUtc/SkippedUpdateVersion）、`MainWindow.cs`（内置命令 `update` + 托盘菜单）、`.github/workflows/ci.yml`、`.github/workflows/release.yml`、`scripts/dist/*`。

- **版本号单一来源**：csproj `<Version>` 是唯一真相源（当前 `1.0.0`），CI 发布时用 `-p:Version=<tag 去 v 前缀>` 覆盖（见 release.yml）。运行期 `App.AppVersionString` 读 `AssemblyInformationalVersionAttribute` 并按 `+` 截断 commit hash——**不能用 `Assembly.Location`/`FileVersionInfo`：`PublishSingleFile` 下 `Assembly.Location` 为空串**；特性缺失回退 `GetName().Version`，再失败回退 `"0.0.0"`（版本号读取绝不抛异常、不能影响启动）。
- **版本比较陷阱**：`System.Version` 里 `1.2.3`（Revision=-1）与 `1.2.3.0`（Revision=0）不相等且后者更大，直接比较会在三段/四段混用时误判。故 `UpdateChecker.CompareVersion`（私有）只比 Major/Minor/Build 且把负 Build 归零；`IsNewerThanCurrent`/`IsSkipped` 都走它，其余版本比较不要再手写。
- **命令行参数统一解析（重要坑）**：原先 `MainWindow` 直接把 `Environment.GetCommandLineArgs()[1]` 当配置文件路径；自动更新重启要传 `--wait-for-pid <pid>`，会被误当配置路径。现一律经 `StartupArgs.Parse`（`Program.Main` 最开始调用一次），`MainWindow` 改读 `StartupArgs.ConfigPath`。**新增命令行参数只改 `StartupArgs` 一处**，不要再在别处直接读 `GetCommandLineArgs`（未知 `--` 参数会被 `Parse` 忽略，避免误当配置路径）。
- **发布产物命名是客户端更新的契约**：`WindowsGlobalLauncher-v<ver>-win-x64.zip` + 同名 `.sha256`（release.yml 生成）；`UpdateChecker.SelectAsset` 按此挑资产——精确名优先，退而取第一个「含 `win-x64` 的 zip」。**改 workflow 里的命名必须同步改 `UpdateChecker` 的资产选择逻辑**，否则客户端找不到包。
- **检查策略**：端点 `api.github.com/repos/lovebirdsx/windows-global-launcher/releases/latest`，**必须带 User-Agent 否则 GitHub 返回 403**；**不对 API 做镜像回退**（ghfast.top/gh-proxy.com 只可靠代理资产下载，不代理 API），镜像仅用于下载（`UpdateInstaller.BuildMirrorUrls`）。启动后延迟 30s 再查（避开启动高峰与开机网络未就绪），每天最多一次（`AppState.LastUpdateCheckUtc`，`ShouldAutoCheck` 按 24h 判定，时钟回拨视为允许检查）；**限流（403/429）也写检查时间戳**避免反复撞墙、普通网络失败不写以便下次重试。`draft`/`prerelease` 视为「无可用更新」而非错误；手动检查忽略节流与「跳过此版本」且无论结果如何都给用户反馈（无更新也弹「已是最新版本」）。
- **自替换机制（核心不变式）**：Windows 允许重命名正在运行的 exe（只改目录项，已打开的映像句柄仍指向同一文件）但不允许删除/覆盖它。流程：「写权限预检（`CanWriteToDirectory` 建临时文件探测，Program Files 等无权限场景在下载前就挡掉）→ ① 新 exe `File.Copy` 到同目录的 `exe.new` → ② 旧 exe 改名 `.old` → ③ `File.Move(exe.new → exe)` → ④ 启动新进程 → 旧进程 `Shutdown` → 新进程删 `.old`」。**先复制到 `.new` 再 rename 落位，不能直接 `File.Copy` 到 exePath**：CopyFile 中途失败（磁盘满 / I/O 错误 / 杀软拦截）不保证清理目标文件，会在原路径留下一个损坏的 exe——那时既回滚不了（原路径已被占），损坏的 exe 若还能启动还会由 `CleanupLeftovers` 把唯一可用的 `.old` 备份删掉，彻底变砖。改成「复制 + 两次同卷 rename」后，危险窗口只剩 rename，不存在部分写入的中间态。每步失败都回滚，**绝不留下「旧的已改名、新的没落位」的半残局面**；`.old` 被占用时依次尝试 `.old1`~`.old9`。
- **回滚必须无条件**：`RollbackRename` 先无条件删掉 exePath 上可能存在的残留、再把备份搬回，**不要加「exePath 不存在才回滚」这类前置条件**——一旦某个失败路径在原路径留下半个文件，回滚就会被静静跳过（即上一条说的变砖路径）。
- **`.old` 残留清理用精确白名单**：`CleanupLeftovers` 遍历 `EnumerateBackupCandidates`（`.old` + `.old1`~`.old9`，与生成备份名共用同一份定义），**不能用 `Directory.GetFiles(dir, name + ".old*")`**：通配的 `*` 会匹配任意后缀（含点号），把用户自己放在同目录的 `WindowsGlobalLauncher.exe.old.bak` 之类文件永久删除。同时清理未落位的 `exe.new`。
- **新旧进程并存的资源冲突**：新进程带 `--wait-for-pid <旧 pid>`，在 `Program.Main` 里 `new App()` **之前**同步等待旧进程退出（上限 15s，超时也继续启动并记 WARN），否则会撞上 RegisterHotKey 失败、低级键盘钩子重复安装、双托盘图标、旧进程 `OnExit` 的 `ResetEffect` 抹掉新进程刚设的护眼矩阵。等待前**先核对进程名**：pid 会被系统复用，旧进程若已退出且 pid 被分配给了无关的长命进程，不校验就会白等满 15 秒拖慢启动。
- **进程启动方式**：当前进程已是管理员，`UseShellExecute=false` + `ArgumentList` 启动同样 requireAdministrator 的 exe 不会弹 UAC；仅在收到 `ERROR_ELEVATION_REQUIRED`(740) 时才回退 `UseShellExecute=true` + `Verb=runas`（那条路径不支持 `ArgumentList`，只能 `BuildQuotedArguments` 手工拼引号）。
- **校验值拿不到就拒绝安装，不降级**：优先用 GitHub 资产的 `digest` 字段（`sha256:<hex>`，取冒号后小写 hex；只接受 64 位小写 hex，格式异常返回空串让调用方走下一档而非拿坏值比对导致更新永远失败）——它来自 `api.github.com` 直连响应，是整条链路里唯一未经镜像中转的可信锚点；取不到才下载同名 `.sha256` 资产（sha256sum 风格取第一个空白分隔字段，同样直连优先，取自镜像时记 WARN 说明信任降级）。**两者都拿不到时拒绝自动安装并引导手动下载**，刻意不降级为仅体积校验：更新包要拿管理员权限直接替换自身可执行文件，做一个体积相同的替换品毫无难度，仅体积校验等同于没有校验；而 release.yml 保证每个 zip 都带 `.sha256`、GitHub 也会自动生成 digest，两者同时缺失只可能是异常情况。
- **下载中退出程序的协作**：`UpdateWindow.OnClosing` 在下载态 `e.Cancel = true`（拦「用户误关窗口」），但同一条路径也会把 `Application.Shutdown()` 挡下来。而 `MainWindow.ExitApplication` 是**先 `Dispose`（移除托盘图标、注销热键与钩子）再 `Shutdown`**，Shutdown 一旦被取消，程序就停在「没有托盘图标、没有热键、也退不掉」的半死状态。故 `ExitApplication` 里：`UpdateInstaller.IsBusy` 时先弹确认框，用户坚持退出则调 `UpdateWindow.PrepareForApplicationShutdown()` 放行拦截。**给窗口加「拒绝关闭」逻辑时都要想一遍应用级退出这条路径。**
- **zip 用内置 `System.IO.Compression.ZipFile`**：SharpCompress 只为 7z（OCR 引擎包）引入，更新包是 zip，勿混用。
- **单实例机制已引入**（见「单实例、开机自启与安装实现要点」）：`--wait-for-pid` 只负责更新场景的新旧进程并存（新进程等旧进程退出），「用户重复双击起两个实例」改由 `SingleInstance` 命名 Mutex + 广播唤起处理，不再是不在此功能范围的既有问题。
- **AppState 加字段**：`AppState` 走 `JsonSerializer`（内部 `State` 类），加字段只改 `State` 类 + getter/setter 即可，**区别于 `AppConfig.SaveConfig` 那套手写 JSON 拼接**（那里加字段必须同步改拼接代码）。本功能新增 `LastUpdateCheckUtc`/`SkippedUpdateVersion` 两字段。注意 `AppState` 以整文件覆写方式持久化、本身不做并发保护，故**更新检查在后台线程拿到结果后要经 `UpdateCoordinator.MarkCheckedOnUiThread` 切回 UI 线程再写**，避免与 UI 线程的其它 `SaveState` 撞车。
- **内置命令 `update`**：与 `config`/`logs` 同机制，四处——`AppCommands` 数组、`RefreshCommandList` 注入命令项、`ExecuteAppCommand` 分支（`_ = UpdateCoordinator.RunManualCheckAsync()`，网络操作 fire-and-forget），另加托盘菜单「检查更新」一处。新增内置命令照此四处改。
- **release.yml 的两条硬约束**：① tag 名与手动输入是外部可控字符串，**必须经 `env:` 传进 pwsh 脚本**，不能用 `'${{ ... }}'` 直接插值——插值发生在脚本执行之前，带引号或换行的 tag 名会闭合字符串并执行任意 PowerShell；② 版本号正则严格三段 `^\d+\.\d+\.\d+$`，与客户端 `CompareVersion` 忽略第四段的口径一致——放行四段会让 `v1.2.3` 与 `v1.2.3.1` 被判为同一版本，用户永远收不到后者的更新提示。
- **`scripts/dist/Start.ps1` 用 `$env:ProgramW6432` 而非 `$env:ProgramFiles`** 定位 .NET 运行时目录：32 位 PowerShell 宿主下后者指向 `Program Files (x86)`，而 x64 运行时装在 `Program Files`，会误判为未安装并重复下载安装。
- **`.gitignore` 的 `dist` 必须写成 `/dist`**：无锚点的 `dist` 会连 `scripts/dist/`（打包进 zip 的启动脚本）一起忽略，导致 CI 组装 staging 目录失败。

## 单实例、开机自启与安装实现要点

涉及文件：`SingleInstance.cs`（单实例互斥 + 广播唤起）、`AutoStartManager.cs`（计划任务）、`ForegroundActivator.cs`（前台激活，见「两套热键机制」）、`MainWindow.cs`（`autostart` 内置命令 + 托盘项 + 热键气泡）、`Program.cs`（Main 单实例判定 + 自启维护开关）、`StartupArgs.cs`（`--install-autostart`/`--uninstall-autostart`）、`scripts/install.ps1`、`scripts/dist/Install.cmd`、`scripts/release.ps1`。

- **为什么必须用计划任务而不是 HKCU\Run**：`app.manifest` 是 `requireAdministrator`，登录时由 Run 键启动会**静默失败**（不弹 UAC）。任务 XML 关键项：`RunLevel=HighestAvailable`、`LogonTrigger` + `Delay=PT20S`（避开登录高峰，与「启动后延迟 30s 查更新」同款克制）、`ExecutionTimeLimit=PT0S`（不设执行时长上限）、电池策略全关（`DisallowStartIfOnBatteries=false`/`StopIfGoingOnBatteries=false`）。取 exe 路径用 `Environment.ProcessPath`（`PublishSingleFile` 下 `Assembly.Location` 是空串，同更新小节）。
- **任务 XML 必须以 UTF-16(Unicode) 写盘**：`schtasks /XML` 不认 UTF-8，会报「不是有效的 XML」——`File.WriteAllText(path, xml, Encoding.Unicode)`。路径/用户名插入 XML 前用 `SecurityElement.Escape` 转义，避免 `&` 等字符破坏 XML。
- **`IsEnabled()` 只看 schtasks 退出码、不解析输出**：中文系统输出是 GBK，.NET Core 默认不带该代码页，解析会乱码/异常。副作用：「任务存在但被用户手动禁用」会被当成已启用（`/Query` 对禁用任务仍返回 0）。
- **`IsEnabled()` 会起 schtasks 子进程，绝不能放热路径**：`RefreshCommandList` 每敲一个字符就跑一遍，故 `MainWindow` 用 `_autoStartEnabled` 缓存。启动时那次经 `RefreshAutoStartStateAsync` 丢后台线程查、回填时 `Dispatcher.BeginInvoke` 切回 UI 线程——它在启动阶段的唯一用途只是渲染 autostart 命令的描述文本，不值得让程序就绪时间同步等一个子进程；托盘菜单 `Opening` 与 `ToggleAutoStart` 需要即时准确，仍走同步查询。
- **schtasks 超时后排空输出流必须限时**：`ReadToEndAsync` 只有进程退出、管道关闭才完成，`Kill` 万一失败而进程仍僵死，无限的 `Task.WaitAll` 会挂住调用线程——而 `IsEnabled()` 是 UI 线程同步调用的，那就是界面冻结。故超时路径用 `Task.WaitAll(..., StreamDrainTimeoutMs)` 且吞掉异常（该路径本就丢弃输出，不吞会留下未观察的 Task 异常刷 ERROR 日志）。
- **单实例**：命名 Mutex（`Local\` 而非 `Global\`——实例冲突只发生在同一登录会话，`Local\` 权限更低、多用户机器不易互相干扰）+ `RegisterWindowMessage` 广播唤起（第二个实例通知已有实例弹出命令面板后静默退出）。坑：必须捕获 `AbandonedMutexException`（旧进程被 `Stop-Process -Force` 强杀时所有权已转移，应视为获取成功）；`ReleaseMutex` 只能在持有所有权时调用、否则抛 `ApplicationException`；监听窗口 `WndProc` **必须先排除消息 ID 为 0**——`RegisterWindowMessage` 失败返回 0，而 0 就是 `WM_NULL`，系统和本程序的挂起探测（`ForegroundActivator.IsWindowHung`）都会发 `WM_NULL`，不排除会让面板被随机弹出。本程序恒以管理员运行、两实例完整性级别相同，广播不会被 UIPI 过滤，无需 `ChangeWindowMessageFilterEx`。
- **只有「用户重复启动」才广播唤起**：拿不到互斥量时若带着 `--wait-for-pid`（自动更新重启，说明旧进程超时仍未退出），广播就成了「让正在被更新掉的旧实例弹出命令面板」——更新中断、动作语义也完全不对。该场景静默退出并记 WARN，把机会留给下次更新检查（exe 已替换，下次启动即新版本）。
- **`previousForeground` 可能是 0**：记录那一刻系统确实没有前台窗口时就是 0，照直用就没有线程可 `AttachThreadInput`。故 `ForegroundActivator.ForceForeground` 在它为 0 或就是自己时，回退用**此刻的**前台窗口做附加目标——附加只为借一个别的输入队列解锁，用哪个窗口都成立，排除自己即可；连一个可附加窗口都没有（`foreThread == 0`）时走 `SwitchToThisWindow` 兜底。
- **排查激活问题前先确认桌面没锁**：锁屏时活动输入桌面是 Winlogon 而非本进程所在的 Default 桌面，`GetForegroundWindow()` 恒返回 0、`SetForegroundWindow` 必然失败，日志里会刷出「首次激活失败，开始短间隔重试」「多次激活失败」——这是锁屏的必然结果，不是缺陷，据此改代码等于追一个不存在的 bug。判定方法：`LogonUI.exe` 是否在跑，或 `OpenInputDesktop` 是否失败。
- **互斥量等待时长分两档**：`--wait-for-pid`（自动更新重启）等 5 秒（旧进程可能仍在退出的最后阶段），普通重复启动只等 500ms——否则用户双击后会有几秒「什么都没发生」的空窗期。
- **`--install-autostart` / `--uninstall-autostart`**：只做计划任务注册/注销后退出（退出码 0/1 供脚本判断），不创建 `App`、不占用热键/钩子/托盘，可在程序运行期间被安装脚本安全调用。计划任务的 XML 细节只保留 `AutoStartManager` 一份实现，脚本不重复造轮子（`scripts/install.ps1` 直接 `Start-Process -Verb RunAs -ArgumentList '--install-autostart'`）。
- **安装目录选 `%LOCALAPPDATA%\Programs`**：按用户安装的约定位置（VS Code、Git 等默认也在此）、当前用户天然可写，自动更新原地自替换 exe（`UpdateInstaller` 有写权限预检）才能成功；`Program Files` 会因无写权限被更新流程直接拒绝。
- **新增命令行参数只改 `StartupArgs` 一处**（既有约定仍然成立，本次新增两个开关 `--install-autostart`/`--uninstall-autostart` 就是照此办理；未知 `--` 参数仍被 `Parse` 忽略）。注意 `--uninstall-autostart` 目前在仓库内没有调用方，是刻意保留的对称开关（卸载/清理脚本注销计划任务的唯一入口），不是漏接线。
- **`release.ps1` 回滚 csproj 必须写成 `git checkout HEAD -- <csproj>`**：不带 `HEAD` 的 `git checkout -- <path>` 是从**暂存区**恢复工作区。回滚停在 `VersionWritten` 阶段时 `git add` 可能已经执行过（add 成功、commit 失败，如未配 `user.email`／hook 拒绝／签名失败），此时暂存区里正是要回滚掉的新版本号——不带 `HEAD` 会把工作区也刷成新版本、暂存区还留着改动，却照常打印「回滚完成」。

## 日志

`Logger` 写入 `App.BaseDir` 下按日期轮转的日志（自动清理 3 天前），可通过托盘菜单或内置 `logs` 命令打开。排查运行期问题优先看日志。

**全局未处理异常兜底**（`App.OnStartup` 最开头调用 `RegisterGlobalExceptionHandlers`）：注册三类处理器统一记 ERROR 日志，避免异常静默丢失——`DispatcherUnhandledException`（记 ERROR 后 `e.Handled = true`，让常驻托盘程序继续存活）、`AppDomain.CurrentDomain.UnhandledException`（记 ERROR 并附 `IsTerminating`，无法阻止终止）、`TaskScheduler.UnobservedTaskException`（记 ERROR 后 `SetObserved()`）。处理器内写日志代码自身用 try/catch 包住防递归。原先只有 `Program.Main` 对 `app.Run()` 的 try/catch 兜底，Dispatcher 回调 / 后台线程 / 未观察 Task 的异常不留日志、排查困难。

**关键路径日志约定**：`ShowHistory` 记 INFO「弹出前前台窗口」、首次激活失败记 WARN「开始短间隔重试」（每次唤出最多一条）、跳过 attach / UIA 超时或失败均记 WARN；`HotKeyListener` 收到 `WM_HOTKEY` 记一条 INFO、`MainWindow.ShowWindow` 记一条带位置/尺寸/弹出前前台窗口的 INFO——排查「按了热键没反应」的分水岭：日志里没有 `WM_HOTKEY` = 按键根本没到（热键被别的程序抢注、或被更前面的低级钩子吞掉）；有 `WM_HOTKEY` 和唤出日志但用户没看到窗口 = 位置或激活的问题；刻意避开 `SelectionChanged` 等高频路径以免刷屏。

## 其它

- 修改或者新增功能，有必要的话，则同步更新CLAUDE.md和README.md
