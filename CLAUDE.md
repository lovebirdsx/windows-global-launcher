# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

回答与代码注释统一使用中文（与现有代码风格一致）。

## 项目概览

Windows 平台桌面工具，基于 **.NET 8 WPF**（`net8.0-windows`，同时启用 `UseWPF` 和 `UseWindowsForms`）。包含五个相互独立的功能：

1. **命令启动器**（`MainWindow`）：全局热键（默认 `Ctrl+Shift+I`）弹出的搜索式命令面板，从 JSON 配置读取命令并通过 `Process.Start` 执行。
2. **Alt+Tab 窗口切换器**（`SwitcherWindow`）：接管系统 Alt+Tab，竖向列出当前窗口（图标 + 标题）供切换。
3. **剪贴板历史**（`ClipboardWindow` + `ClipboardHistoryManager`）：后台记录系统复制历史（文本 + 图片），热键（默认 `Ctrl+Alt+C`）弹出紧凑历史面板，回车粘贴回原窗口。
4. **截图与贴图**（`ScreenshotManager` + `ScreenshotOverlayWindow` + `PinWindow`）：Snipaste 式区域截图（默认 `F4`，框选/窗口吸附/标注/取色，确认后复制到剪贴板或 OCR 识别选区文字）与屏幕贴图（默认 `F7`，剪贴板图片钉为最顶层浮窗）。
5. **护眼模式**（`EyeCareManager`）：内置「正常」「办公」两种色温/亮度模式，经命令启动器（搜「护眼」）或托盘菜单选择，通过 Magnification 全屏颜色矩阵生效。

前三者由 `App.OnStartup` 同时创建、常驻后台（通过系统托盘 `NotifyIcon` 管理），平时隐藏，靠热键/钩子唤出；截图/贴图为静态类无常驻窗口，由 `WindowActions` 热键或托盘菜单按需触发；护眼模式无独立窗口，启动时恢复上次选择。

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
- 内置动作：`CloseWindow`（关闭前台窗口，复用 `WindowEnumerator.CloseWindow`）、`VolumeUp`/`VolumeDown`/`ToggleMute`（`keybd_event` 模拟媒体键 `VK_VOLUME_*`）、`ShowClipboardHistory`（唤出剪贴板历史，经 `App.ClipboardHistoryWindow` 静态属性找到窗口实例）、`Screenshot`/`PinClipboard`（区域截图/剪贴板贴图，调 `ScreenshotManager` 静态方法；默认绑定裸 `F4`/`F7`——无修饰键热键只能走本表驱动路径，`HotKeyListener.RegisterHotKey` 拒绝无修饰键；注意裸键命中即全局吞掉，Excel 等应用内 F4 会失效，属用户已确认的取舍）。
- 旧配置文件无 `WindowActions` 字段时 `LoadConfig` 自动补默认绑定（`AppConfig.DefaultWindowActions()`）；后续新增动作的默认绑定也要在 `LoadConfig` 里做「缺失则追加」的定向迁移（参照 ShowClipboardHistory 的写法），否则已有配置的老用户拿不到新热键。新增字段须同步 `AppConfig.SaveConfig` 手写 JSON 拼接。

## 剪贴板历史实现要点

涉及文件：`ClipboardHistoryManager.cs`、`ClipboardWindow.cs`、`ClipboardEntry.cs`。

- **监听**：`AddClipboardFormatListener` + WinForms `NativeWindow` 消息窗口收 `WM_CLIPBOARDUPDATE`（同 `HotKeyListener` 的隐藏窗口做法，HwndSource 不支持 message-only parent）。读取剪贴板须在 UI（STA）线程，被占用时重试 5 次（每次间隔 30ms）。
- **内容**：仅文本与图片。超长文本（>5 万字符）与超大图片（PNG >5MB）跳过。去重键：文本直接比较内容，图片比较 PNG 字节的 SHA1；命中已有条目则置顶并刷新时间（Ditto 风格），不重复写文件。粘贴回写剪贴板也会触发监听，靠去重自然收敛，无需抑制标记。
- **持久化**：仿 `AppState` 单例模式，元数据写 `clipboard-history.json`（ UnsafeRelaxedJsonEscaping 保留中文），图片按条目 Id 存 `clipboard-images\{Id}.png`；上限 100 条（`MaxEntries`），淘汰/删除条目时连同 PNG 清理。加载时丢弃图片文件已丢失的条目。
- **唤出与粘贴**：`ShowHistory` 在 `Show()` 前先 `GetForegroundWindow` 记下目标窗口；回车后先 `SetToClipboard` 写回内容，再 `WindowEnumerator.Activate` 恢复前台，延迟 120ms 后 `keybd_event` 模拟 Ctrl+V。
- **弹出位置**：三级回退——`GetGUIThreadInfo` 取前台线程插入符矩形 + `ClientToScreen` 换算；VS Code 等 Electron/Chromium 应用光标自绘取不到，改用 UI Automation（`AutomationElement.FocusedElement` → `TextPattern` 选区的 `GetBoundingRectangles`）；再失败回退「鼠标所在屏幕居中」（同 `MainWindow.CenterWindowOnCurrentScreen`），并钳制在屏幕工作区内。UIA 是跨进程 COM 调用、无超时，目标应用（VS Code 等 Electron）卡顿时会长时间阻塞 UI 线程，故 `TryGetCaretViaUIA` 改为 `Task.Run` 后台执行 + `Wait(300ms)`（常量 `UiaTimeoutMs`）短超时，超时/失败静默回退居中定位并记 WARN；只把 `Point?` 纯数据传回 UI 线程，`AutomationElement` 等 COM 对象留在后台线程。
- **前台激活**：热键经低级钩子到达（输入未真正进入本进程队列，且经 `Dispatcher.BeginInvoke` 异步执行），直接 `Activate`/`SetForegroundWindow` 会被前台锁定间歇性拒绝（非文本输入框时 `PositionWindow` 的 UI Automation 首次调用很慢、进一步拖到锁定武装之后），导致第一次唤出一闪即隐。因此窗口改为 `ShowActivated=false` 不抢焦点，激活统一走 `TryActivateOnce`：用弹出前记录的 `_previousForeground` 取线程 `AttachThreadInput` 附加（同 `WindowEnumerator.Activate` 技巧）→ `BringWindowToTop` → `SetForegroundWindow`，失败时模拟一次 Alt 击发解锁再重试，仍未果则经 `DispatcherTimer` 短间隔重试（上限 `MaxActivationRetries`）。附加前同样先 `IsWindowHung` 探测弹出前前台窗口线程是否挂起，挂起则跳过 attach、改走 `SwitchToThisWindow` 兜底（避免共享输入队列把 UI 线程一起拖死，见切换器小节）；attach/detach 严格 `try/finally` 配对，异常路径也保证解除附加。失焦即隐藏（同 `MainWindow`），但显示后有 `ActivationGraceMs` 宽限期，此间失焦视为激活抖动、重试激活而非隐藏。
- **交互**：与命令启动器一致（↑↓/Ctrl+P/Ctrl+N 移动、回车执行、Esc 取消），另支持 Delete 删除选中条目；再按一次热键关闭（切换式）。键盘处理在搜索框 `PreviewKeyDown`，不依赖全局钩子。
- **条目预览**：选中条目时弹出独立预览窗（`_previewWindow`，`ShowActivated=false` 不抢焦点），位于主窗口右侧、放不下翻左侧，钳制在屏幕工作区内（定位统一走 `PlacePreview`）。图片条目尽量按原始像素显示，超过上限（720×560 与工作区 50%/60% 取较小）则等比缩小，预览图经 `ClipboardHistoryManager.LoadFullImage` 加载并缓存在条目上（`ClipboardEntry.PreviewImage`）。`ShowImagePreview` 先按预览上限（含 DPI 换算）算出 `maxPixelWidth` 传给 `LoadFullImage`，`LoadBitmap` 仅在原图更宽时才设 `DecodePixelWidth`（只降不升），避免接近 5MB 上限的大图在 UI 线程全量解码造成长阻塞；`ClipboardEntry.PreviewImageDecodedWidth`（`JsonIgnore`）记录实际解码宽度，复用缓存要求缓存宽度 ≥ 当前需求、否则按更大宽度重解码。`SetToClipboard`（全尺寸）与列表缩略图（52px）语义不变；文本条目单行预览放不下（`Preview.Length > TextPreviewThreshold`）时显示折行完整文本（超长截断至 `TextPreviewMaxChars`，滚轮可滚动，滚动条刻意隐藏——点击会激活预览窗导致主窗口失焦关闭）。注意 `RefreshList` 在 `Show()` 之前触发 `SelectionChanged` 时窗口尚不可见，`ShowHistory` 末尾需补调一次 `UpdatePreview()`。
- **列表滚动条**：刻意不显示——垂直 `Hidden`（保留滚动，键盘导航 `ScrollIntoView` 与滚轮仍可用），水平 `Disabled`（内容约束在列表宽度内，超长文本走省略号截断）。

## 截图与贴图实现要点

涉及文件：`ScreenshotManager.cs`（编排 + `SnipAction`/`SnipResult` 契约）、`ScreenCapture.cs`（抓屏）、`WindowRectSnapshot.cs`（窗口吸附）、`ScreenshotOverlayWindow.cs`（全屏遮罩交互）、`AnnotationController.cs`（标注层）、`PinWindow.cs`（贴图浮窗）、`ScreenshotGeometry.cs`（纯几何/格式化，配套单测 `ScreenshotGeometryTests`）。

- **DPI 前提：app.manifest 声明 PerMonitorV2**（为截图新增）。全进程坐标语义：`Screen`/`Cursor.Position`/抓屏矩形 = 物理像素，WPF `Left/Top/Width/Height` = DIP，换算因子 `VisualTreeHelper.GetDpi(window)`。现有窗口的「物理 ÷ 窗口 DPI」换算数学在 PMv2 下比原 system-aware 更精确，无需改动。
- **单一遮罩窗口覆盖整个虚拟屏**：PMv2 窗口 DWM 不做位图缩放（缓冲区 1:1 映射物理像素），故 `SetWindowPos` 以物理像素铺满虚拟屏后，所有换算只有一条公式 `DIP = (物理 − 虚拟屏原点) / 窗口scale`，冻结帧在所有屏（含混合 DPI）上像素精确。**选区真相源是虚拟屏物理像素 `System.Drawing.Rectangle`**，渲染时才换算 DIP。**遮罩窗口严禁订阅 `DpiChanged` 并在处理器里改布局**：系统会派发一次虚假 DpiChanged（Old==New==当前缩放），处理器中调用布局修改（如 ApplyLayout 设置 Canvas/Image 尺寸）会让 WPF 对该全屏窗口做一次「DPI 倍数」的二次缩放——屏幕左上出现黑边、内容放大 1.25 倍（对照实验：空操作处理器或不订阅均正常）。窗口每次截图新建、定位后不移动，DPI 恒定，无需响应。
- **先冻结再框选**（Snipaste 做法）：`Graphics.CopyFromScreen` 一次抓整个虚拟屏 → `CreateBitmapSourceFromHBitmap`（`DeleteObject` 释放 GDI 句柄）→ Freeze。遮罩、放大镜取色、标注、输出全部基于冻结帧。**护眼模式的 Magnification 颜色矩阵会包含在抓屏结果里**（实测 Win10 19045：`CopyFromScreen` 拿到的是矩阵处理后的像素）——为保证成品图/取色为真实色彩，`StartCapture` 在抓屏前 `EyeCareManager.SuspendEffect()` 临时写恒等矩阵（不改 CurrentModeName/持久化）、`DwmFlush()`×2 等 DWM 合成生效后抓屏，`finally` 中 `ResumeEffect()` 立即恢复；恢复后遮罩显示冻结帧时矩阵恰好生效一次，观感与平时护眼桌面一致。挂起失败（Mag API 失败）时降级为直接抓屏（含护眼色彩）并记 WARN。抓屏 INFO 日志带当前护眼模式名与是否挂起。
- **窗口吸附**：遮罩弹出前 `WindowRectSnapshot.Capture` 一次性 EnumWindows（Z 序）+ `DWMWA_EXTENDED_FRAME_BOUNDS` 缓存矩形快照，鼠标移动时命中测试（比 Alt+Tab 过滤更宽松：不查 owner，但排除本进程窗口）。
- **遮罩激活与键盘焦点**：截图由低级钩子触发（输入未进入本进程队列），`OnOverlayLoaded` 里直接 `Activate()` 会被前台锁定拒绝、窗口弹出却拿不到键盘焦点（表现为 Esc 无法取消截图）。故复用 `WindowEnumerator.Activate(hwnd)` 的 AttachThreadInput 技巧绕过前台锁定后再 `Focus()`——与剪贴板窗口 `TryActivateOnce` 同一问题同一解法。
- **遮罩状态机**：Hovering（吸附高亮 + 放大镜取色，`C`/`Shift+C` 复制 HEX/RGB）→ Dragging（拖拽 <4px 视为点击 = 选中悬停矩形）→ Selected（8 手柄 + 方向键微调 + 工具条）→ Annotating（工具激活即选区锁定，鼠标转发 `AnnotationController`）。`Completed` 事件**恰好触发一次**且**在 OnClosed 之后统一派发**——确认结果先暂存 `_pendingResult` 再 `Close()`，因为 `HandleResult` 里的 `SaveFileDialog` 是模态对话框，必须等全屏 Topmost 遮罩关闭后再弹，否则被挡住像卡死。
- **输出合成**：`CroppedBitmap` 裁冻结帧 + `VisualBrush`（`ViewboxUnits=Absolute`，Viewbox=选区 DIP）截取标注 Canvas，`RenderTargetBitmap(selW, selH, 96×scale, 96×scale)` 渲染——输出像素尺寸精确等于选区物理尺寸。复制走 `Clipboard.SetImage`，剪贴板历史经 `WM_CLIPBOARDUPDATE` 自动捕获（SHA1 去重），无需额外登记。
- **标注层**（`AnnotationController`）：挂在宿主 Canvas 上（`IsHitTestVisible=false`，鼠标由遮罩统一转发 DIP 坐标）；矩形/椭圆/箭头（`ScreenshotGeometry.BuildArrowPolygon` 七点实心多边形）/画笔（抽稀 Polyline）/文字（编辑中 TextBox，落定转 TextBlock；TextBox 的 Enter/Esc 自行 Handled，遮罩 PreviewKeyDown 见 `Keyboard.FocusedElement is TextBox` 即放行）；撤销栈只含已落定元素；`Clear` 用 `_ownedElements` 集合只删自己创建的。
- **标注参数可调与工具条**：线宽/字号已从硬编码常量改为可调（`StrokeWidth`/`TextFontSize` 只读属性 + `AdjustStrokeWidth`/`AdjustTextFontSize`，范围 1–12 / 8–48，滚轮步进 1）；遮罩窗口 `PreviewMouseWheel` 跟随当前工具——线条类（矩形/椭圆/箭头/画笔）调线宽、文字调字号、未选工具不响应；调整只作用于「正在绘制/编辑的对象」，或（无正在绘制/编辑时）「刚刚绘制的那个」，两者互斥、不回写更早的历史对象（拖拽中形状/编辑中文字框实时跟随；无拖拽/编辑时最近落定的元素跟随；箭头经 `_arrowEndpoints` 端点重算）；颜色（`StrokeColor`）同逻辑——选色后正在绘制/编辑的对象实时变色，无正在绘制时最近落定的元素变色。已落定元素支持点击拖动移动（`HitTestAnnotation` 几何命中测试，后画先命中；矩形/椭圆/文字改 Canvas 坐标、箭头/画笔平移点集，箭头移动时同步端点）。工具条按钮统一 30×30、单色简洁图标（彩色 emoji 已替换为 Segoe MDL2 Assets/文本），工具条内常驻「粗细 N / 字号 N」指示器（`_settingIndicator` + `UpdateSettingIndicator`，变化后经 `RepositionToolbar` 重排）。颜色/线宽/字号/工具跨会话持久化在 `AppState`（`GetAnnotation*`/`SetAnnotationSettings`）：截图窗口构造时读取恢复颜色（`ScreenshotGeometry.ParseHex`）/线宽/字号、`EnterSelected` 时按 `AnnotationTool` 名恢复上次工具并自动激活、`OnClosed` 时一次性写回。
- **贴图浮窗**（`PinWindow`）：构造即 `Show()`，1:1 物理像素显示（基准 DIP = `PixelWidth / DpiScale`，**必须用 PixelWidth**——剪贴板图片 DPI 元数据会让 `Width` 不可靠），`Loaded` 后重读 DPI 校正一次。滚轮缩放以光标为锚（10%~500%）、`Ctrl+滚轮` 调透明度、双击/Esc 关闭、右键菜单（复制/保存/缩放100%/关闭所有）。静态 `_open` 列表跟踪全部实例（构造加入、`Closed` 移除），`CloseAll` 遍历副本。
- **热键**：`Screenshot`(F4)/`PinClipboard`(F7) 走 `WindowActions` 表驱动路径（见上文窗口动作小节），入口 `ScreenshotManager.StartCapture`/`PinFromClipboard` 仅 UI 线程调用；`IsCapturing` 防截图会话重入。**截图会话进行中按 F7 = 钉图当前选区**：F7 被全局钩子吞掉、到不了遮罩窗口，`PinFromClipboard` 检测 `IsCapturing` 且 `_activeOverlay` 非空时转发 `overlay.PinCurrentSelection()`（Selected/Annotating 态等同点工具条 📌，未框选则忽略记 INFO）。剪贴板读写统一「`ExternalException` 重试 3 次 × 50ms」。
- **OCR 识别选区文字**（`OcrService` + `OcrResultWindow`，工具条 🔤 触发 `Finish(SnipAction.Ocr)`）：识别源是**纯冻结帧选区裁剪**——`CroppedBitmap`（冻结帧, 选区物理矩形相对虚拟屏原点, 与虚拟屏求交防越界）Freeze 后直接作为 `SnipResult.Image`，**不含标注、不含压暗层**（标注图形会干扰识别；护眼矩阵抓屏前已挂起故颜色干净）；非 OCR 动作仍走含标注的 `RenderTargetBitmap` 合成。图像不做任何预处理（交给 RapidOCR 自带预处理）——**勿加「深色反色/自适应放大」类预处理**：实测反而降低识别率（反色误伤混合底色、插值放大让笔画发虚），已试过并回退。结果窗可编辑/复制（`Ctrl+Enter` 复制并关窗）；`ScreenshotManager.RunOcr` 用 `async void` fire-and-forget，图片为 null 或识别失败也会 `SetResult(null)`，弹窗不卡加载态。
- **OCR 单引擎（RapidOCR / PP-OCR，无 Windows OCR 回退）**：`RapidOcrBackend` 常驻子进程经 stdin/stdout 传 JSON，`--ensureAscii=1` 规避编码、`--maxSideLen=2048`；`StandardInputEncoding` 必须用 `new UTF8Encoding(false)`（无 BOM）——`Encoding.UTF8` 带 BOM，`StandardInput` 的 `StreamWriter` 首次 Flush 会把 BOM 写到 stdin 首行 JSON 之前，引擎按 `jsonIn[0]=='{'` 判定失败 → 首次识别必失败、第二次才成功；安装检测 = 数据目录 `ocr-engine\` 递归找 exe（结果缓存，`InvalidateInstallCache()` 刷新）。`OcrService` 是薄门面：`IsReady => RapidOcrBackend.IsInstalled`，`RecognizeAsync` 包装 `RapidOcrBackend.RecognizeAsync`（`""` → null、异常兜底记日志）。引擎包（约 70MB，GitHub 直连 + 镜像多源，SharpCompress 解 7z）在 `App.OnStartup` 末尾 fire-and-forget 后台下载：`OcrEngineInstaller.EnsureInstalledAsync` 合流（lock + `_currentInstall`，多处并发共享同一进行中任务、不重复下载），成功记 INFO「识图引擎后台下载完成」、失败记 WARN 不弹窗。未就绪时 `RunOcr` 先 `SetDownloading()`（主文本区只读显示「识图引擎正在下载…」+ 订阅 `StatusChanged` 实时刷新进度，`Dispatcher` 切回 UI 线程、`OnClosed` 退订防泄漏），下载失败 `SetEngineUnavailable(reason)` 展示原因 + `ManualInstallHint` + 「重试下载」链接按钮（重试成功显示「引擎已就绪，请关闭后重新识别」）。`App.OnExit` 调 `RapidOcrBackend.Shutdown()` 杀常驻子进程（幂等，try/catch 不阻断其它清理）。

## Alt+Tab 切换器实现要点涉及文件：`KeyboardHook.cs`、`WindowEnumerator.cs`、`SwitcherWindow.cs`、`WindowInfo.cs`。

- **钩子回调运行在 UI 线程**（在 UI 线程安装），且系统对回调有超时（`LowLevelHooksTimeout`）。回调本体只做按键判定，所有重活（枚举窗口、显示、激活）通过 `Dispatcher.BeginInvoke` 异步执行。委托实例必须用字段强引用，防止被 GC 回收导致钩子失效。
- **激活态状态机**：`SwitcherWindow._isActive` 是唯一真相源（仅在 UI 线程读写）。钩子通过 `IsSwitcherActive` 委托读取它，决定是否吞掉 Esc / 导航键 / 触发 Commit。交互：Alt+Tab 显示并向后移动，Shift+Tab 向前，激活态下 `↑/↓` 与 `Ctrl+P/Ctrl+N` 也可移动，松开 Alt = Commit（激活选中窗口），Esc = Cancel。
- **窗口过滤**（`WindowEnumerator.IsAltTabWindow`）：可见、有标题、非 `WS_EX_TOOLWINDOW`、顶层（无 owner 或 `WS_EX_APPWINDOW`）、排除 DWM cloaked 的后台 UWP、排除切换器自身。`EnumWindows` 返回 Z 序（≈MRU），默认选中第二项（上一个窗口）。
- **图标提取**优先级：`WM_GETICON` → 类图标 `GCLP_HICON` → 进程 exe 的 `ExtractAssociatedIcon`（仅最后一种按 exe 路径缓存；前两种句柄归窗口/类所有，不可销毁）。
- **窗口激活的前台锁定**（`WindowEnumerator.Activate`）：用 `AttachThreadInput` 把 UI 线程临时附加到前台线程输入队列，再 `SetForegroundWindow`，否则键盘触发切换时会出现「任务栏闪烁但不前置」。兜底用 `SwitchToThisWindow`。
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

## 日志

`Logger` 写入 `App.BaseDir` 下按日期轮转的日志（自动清理 3 天前），可通过托盘菜单或内置 `logs` 命令打开。排查运行期问题优先看日志。

**全局未处理异常兜底**（`App.OnStartup` 最开头调用 `RegisterGlobalExceptionHandlers`）：注册三类处理器统一记 ERROR 日志，避免异常静默丢失——`DispatcherUnhandledException`（记 ERROR 后 `e.Handled = true`，让常驻托盘程序继续存活）、`AppDomain.CurrentDomain.UnhandledException`（记 ERROR 并附 `IsTerminating`，无法阻止终止）、`TaskScheduler.UnobservedTaskException`（记 ERROR 后 `SetObserved()`）。处理器内写日志代码自身用 try/catch 包住防递归。原先只有 `Program.Main` 对 `app.Run()` 的 try/catch 兜底，Dispatcher 回调 / 后台线程 / 未观察 Task 的异常不留日志、排查困难。

**关键路径日志约定**：`ShowHistory` 记 INFO「弹出前前台窗口」、首次激活失败记 WARN「开始短间隔重试」（每次唤出最多一条）、跳过 attach / UIA 超时或失败均记 WARN；刻意避开 `SelectionChanged` 等高频路径以免刷屏。

## 其它

- 修改或者新增功能，有必要的话，则同步更新CLAUDE.md和README.md
