# Windows Global Launcher

常驻系统托盘的 Windows 桌面工具,包含七个相互独立的功能:命令启动器、Alt+Tab 窗口切换器、剪贴板历史、截图与贴图、护眼模式、自动更新、开机自动启动。平时隐藏在后台,靠全局热键唤出。

[![CI](https://github.com/lovebirdsx/windows-global-launcher/actions/workflows/ci.yml/badge.svg)](https://github.com/lovebirdsx/windows-global-launcher/actions/workflows/ci.yml)

## 功能

### 命令启动器

全局热键(默认 `Ctrl+Shift+I`)弹出搜索式命令面板,从 JSON 配置读取命令并执行。

* 模糊搜索,按使用频率(执行次数 / 最近使用)排序
* 每条命令可单独配置热键,窗口弹出后即可直接触发
* 列表中会显示命令的执行次数与上次执行时间
* 程序虽以管理员权限运行,但启动的命令默认降权为**普通用户权限**(等同桌面双击),避免权限污染;如需以管理员启动可对单条命令设置 `"RunAsAdmin": true`

### Alt+Tab 窗口切换器

* 接管系统 Alt+Tab,竖向列出当前窗口(图标 + 标题)供切换。`Alt+Tab` 向后、`Shift+Tab` 向前,松开 `Alt` 选中,`Esc` 取消
* `j`/`k`、`p`/`n` 键也可向前/向后切换，`x` 键关闭当前选中应用
* `←`/`→` 方向键将选中窗口移到左侧/右侧显示器（已在最左/最右时无响应；最大化窗口移动后保持最大化）
* 有未读通知的窗口（taskbar 按钮闪烁）会以琥珀橙背景高亮显示，切换到该窗口后高亮消除

### 剪贴板历史

后台记录系统所有复制操作（文本 + 图片），默认热键 `Ctrl+Alt+C` 唤出（可在 `WindowActions` 段改绑）。

* 按时间倒序保留最近 100 条，重复内容自动置顶去重，重启不丢（持久化在数据目录）
* 窗口在当前输入光标位置弹出（取不到光标时回退到鼠标所在屏幕居中），失焦自动关闭
* 输入即模糊搜索；`↑`/`↓`、`Ctrl+P`/`Ctrl+N` 选择，回车直接把内容粘贴回原窗口，`Delete` 删除条目，`Esc` 取消
* 选中条目在旁边弹出预览：图片显示原图，过长文本显示折行完整内容
* 超长文本（>5 万字符）与超大图片（>5MB）不记录

### 截图与贴图

Snipaste 风格的区域截图（默认热键 `F4`）与屏幕贴图（默认 `F7`，均可在 `WindowActions` 段改绑）。完整支持多显示器与混合 DPI（PerMonitorV2），截图像素精确。

**截图（`F4`）**：冻结全屏后进入框选——

* 悬停自动吸附窗口边界（高亮该窗口），单击即选中；或拖拽自由框选
* 放大镜实时取色：`C` 复制 HEX、`Shift+C` 复制 RGB
* 护眼模式开启时截图不受影响：抓屏瞬间自动挂起护眼色彩矩阵，成品图与取色结果均为真实颜色
* 选中后可拖动/8 方向手柄调整选区；方向键平移 1 像素、`Shift+方向键` 缩放 1 像素
* 标注工具条：矩形、椭圆、箭头、画笔、文字，4 种颜色，`Ctrl+Z` 撤销
* 确认：`Enter`/双击/工具条 ✓ 复制到剪贴板（自动进入剪贴板历史）；也可钉图（工具条 📌 或直接按 `F7`）、保存 PNG 💾、工具条 🔤 识别选区文字（弹窗可编辑、复制，`Ctrl+Enter` 复制并关窗；识别引擎首次启动自动后台下载，下载完成前暂不可用）；`Esc` 取消

**贴图（`F7`）**：把剪贴板中的图片钉在屏幕最顶层（截图工具条的 📌 亦可）；剪贴板中没有图片但有文字时，把文字钉为便签式贴图（深色底白字，超高可滚动）——

* 左键拖动移动；滚轮缩放（10%~500%，光标为锚点）；`Ctrl+滚轮` 调透明度
* 双击或 `Esc` 关闭；右键菜单：复制图像 / 保存为文件 / 缩放 100% / 关闭所有贴图
* 文字贴图：窗口宽高随内容自适应（宽最多 480，超出折行；高最多屏幕工作区的 60%，超出滚动）；滚轮滚动内容（`Ctrl+滚轮` 仍调透明度）、不支持缩放；右键菜单「编辑」修改文字（`Enter` 保存、`Shift+Enter` 换行、`Esc` 取消、点击别处自动保存），**进入编辑时窗口自动放大到最大尺寸方便编辑**（编辑中保持最大、内容超高滚动，落定后按内容缩回），另有复制文本 / 保存为文件（txt）/ 分类子菜单（8 种颜色任选，仅描边颜色区分，默认灰）/ 关闭所有贴图
* `Shift+F7` 隐藏/显示所有贴图（图片贴图与文字便签统一切换；整体隐藏时新钉一张贴图会连同旧贴图一起恢复显示）；托盘菜单「隐藏所有贴图/显示所有贴图」同效（无贴图时置灰）
* `Win+Q`（或托盘菜单「框选移动贴图」）进入框选态：全屏拖蓝色虚线橡皮筋框，松手选中与框相交的贴图（描边变亮白加粗），之后拖动任一选中贴图即整体移动；`Esc` 取消选中（再按一次 `Esc` 关闭当前贴图）；隐藏中的贴图不参与框选
* 鼠标悬停时贴图阴影浮起（不再变蓝描边，便签分类色描边保持不变）
* 贴图与文字便签重启后自动恢复：退出时仍打开的贴图按原位置/缩放/透明度/分类/内容全部恢复（整体隐藏状态不记忆，恢复后直接显示）；被关闭的贴图不恢复

> 注意:默认热键是无修饰键的 `F4`/`F7`,命中后按键被全局吞掉——Excel 的 `F4`(重复上一操作/切换绝对引用)等应用内同名快捷键会失效。介意的话在配置中改绑其它组合键即可。

### 护眼模式

内置 2 种护眼模式（参数对照 CareUEyes 官方文档），在命令面板搜索「护眼」或「eye」即可选择，回车立即生效；托盘菜单「护眼模式」子菜单也可切换（当前模式打勾）。

| 模式 | 效果                         |
| ---- | ---------------------------- |
| 正常 | 关闭护眼效果（6500K / 100%） |
| 办公 | 色温 5500K，亮度 85%         |

* 通过 Magnification 全屏颜色矩阵实现（不依赖显卡 gamma 支持，HDR/新驱动下也能用）
* 当前模式自动保存，程序启动时恢复，退出时还原

### 自动更新

程序启动后自动在后台检查更新（每天最多一次），发现新版本时弹窗提示版本号与更新日志，可一键下载、替换并重启（详见下文「更新」章节）。

### 开机自动启动

可让程序在登录 Windows 后自动启动（经「任务计划程序」实现，登录后延迟 20 秒、以最高权限运行）：

* 托盘菜单勾选「开机自动启动」，或在命令面板输入 `autostart` 切换（执行后会弹窗告知当前状态）
* 重复启动不会出现第二个实例：开机自启已起一个、又手动双击时，会唤起已运行的命令面板

### 窗口动作热键

全局生效的热键，在配置文件的 `WindowActions` 段自定义，修改配置后自动热更新。默认绑定：

| 热键         | 动作                   | 说明                                                                             |
| ------------ | ---------------------- | -------------------------------------------------------------------------------- |
| `Alt+Q`      | `CloseWindow`          | 关闭当前前台窗口（等同 `Alt+F4`）                                                |
| `Win+F12`    | `VolumeUp`             | 增大系统音量                                                                     |
| `Win+F11`    | `VolumeDown`           | 减小系统音量                                                                     |
| `Win+F10`    | `ToggleMute`           | 切换系统静音                                                                     |
| `Ctrl+Alt+C` | `ShowClipboardHistory` | 弹出剪贴板历史                                                                   |
| `F4`         | `Screenshot`           | 区域截图                                                                         |
| `F7`         | `PinClipboard`         | 把剪贴板内容钉为屏幕贴图（图片优先，无图片有文字则钉为便签）                     |
| `Shift+F7`   | `TogglePinVisibility`  | 隐藏/显示所有贴图（图片贴图与文字便签）                                          |
| `Win+Q`      | `PinBoxSelect`         | 框选多张贴图后整体移动（拖橡皮筋框选，拖动任一选中贴图整体移动，`Esc` 取消选中） |

## 安装

从 [GitHub Releases](https://github.com/lovebirdsx/windows-global-launcher/releases/latest) 下载 `WindowsGlobalLauncher-vX.Y.Z-win-x64.zip`，解压到任意目录。

* **推荐双击 `Install.cmd` 安装到用户目录**：把程序装到 `%LOCALAPPDATA%\Programs\WindowsGlobalLauncher`（当前用户可写，自动更新可正常自替换），创建开始菜单快捷方式并配置开机自启。首次使用会自动检测并静默安装 .NET 8 桌面运行时（未安装时约 55MB）。
* **仅想临时运行、不安装时双击 `Start.cmd`**：自动检测并静默安装 .NET 8 桌面运行时（未安装时约 55MB），然后直接启动当前目录下的程序。
* 已安装 .NET 8 桌面运行时，可直接双击 `WindowsGlobalLauncher.exe` 启动。
* 程序需要管理员权限（用于全局键盘钩子与窗口切换），启动时若弹出 UAC 提示请选择「是」。
* 下载包可用同名 `.sha256` 文件校验完整性，在 PowerShell 中运行：

```powershell
Get-FileHash WindowsGlobalLauncher-vX.Y.Z-win-x64.zip -Algorithm SHA256
```

## 更新

* 程序启动后会在后台检查更新（每天最多一次），发现新版本时弹窗显示版本号与更新日志；点「立即更新」自动下载、校验、替换并重启，也可选「稍后」或「跳过此版本」。
* 随时可通过托盘菜单「检查更新」，或在命令面板输入 `update` 手动检查（手动检查忽略每日节流与「跳过此版本」）。
* 若程序装在无写权限的目录（如受系统保护的 Program Files），自动更新会提示手动下载；可将程序移动到有写权限的目录后重试。

## 运行

* 要求 **.NET 8 运行时**
* **需以管理员权限运行**:窗口切换器依赖低级键盘钩子,否则无法拦截以管理员权限运行的程序的 Alt+Tab
* OCR 识别引擎（基于 RapidOCR/PP-OCR，约 70MB）在首次启动时自动后台下载到数据目录，离线运行；下载完成前识别暂不可用（弹窗显示下载进度）
* 程序常驻系统托盘,右键托盘菜单可打开配置、查看日志或退出

## 配置

数据目录为 `%USERPROFILE%\.windows-global-launcher\`,默认配置文件为 `WindowsCommandLauncher.json`(首次运行自动生成)。修改配置文件后会自动重新加载。

在搜索框输入以下内置命令可直接操作:

| 命令        | 说明                |
| ----------- | ------------------- |
| `config`    | 打开配置文件        |
| `setconfig` | 选择 / 切换配置文件 |
| `logs`      | 查看日志            |
| `update`    | 检查更新            |
| `autostart` | 切换开机自动启动    |
| `exit`      | 退出程序            |

### 配置文件示例

```json
{
  "MaxDisplayItems": 12,
  "HotKey": "Ctrl+Shift+I",
  "WindowActions": [
    { "Action": "CloseWindow", "HotKey": "Alt+Q", "Enabled": true },
    { "Action": "VolumeUp", "HotKey": "Win+F12", "Enabled": true },
    { "Action": "VolumeDown", "HotKey": "Win+F11", "Enabled": true },
    { "Action": "ToggleMute", "HotKey": "Win+F10", "Enabled": true },
    { "Action": "ShowClipboardHistory", "HotKey": "Ctrl+Alt+C", "Enabled": true },
    { "Action": "Screenshot", "HotKey": "F4", "Enabled": true },
    { "Action": "PinClipboard", "HotKey": "F7", "Enabled": true },
    { "Action": "TogglePinVisibility", "HotKey": "Shift+F7", "Enabled": true },
    { "Action": "PinBoxSelect", "HotKey": "Win+Q", "Enabled": true }
  ],
  "Commands": [
    {
      "Name": "记事本",
      "Description": "打开Windows记事本",
      "Shell": "notepad.exe",
      "HotKey": "Ctrl+Alt+N",
      "RunAsAdmin": false
    }
  ]
}
```

> `RunAsAdmin` 可选,默认 `false`:命令以普通用户权限启动(借用桌面 Shell 令牌降权)。设为 `true` 则保留管理员权限启动。降权失败(如 explorer 未运行)时会报错且不启动该命令。

> `WindowActions` 可选,缺省时补默认绑定。`Action` 当前可用值:`CloseWindow`(关闭前台窗口)、`VolumeUp`/`VolumeDown`(增大/减小系统音量)、`ToggleMute`(切换静音)、`ShowClipboardHistory`(剪贴板历史)、`Screenshot`(区域截图)、`PinClipboard`(把剪贴板内容钉为屏幕贴图：图片优先，无图片有文字则钉为便签)、`TogglePinVisibility`(隐藏/显示所有贴图)、`PinBoxSelect`(框选贴图整体移动);`Enabled` 设为 `false` 可临时停用某条绑定。修饰键为精确匹配(如配置 `Alt+Q` 时 `Alt+Shift+Q` 不会触发)。

## 开发

源码位于 `src/WindowsGlobalLauncher`,解决方案文件为 `WindowsGlobalLauncher.sln`。

```bash
dotnet build        # 编译
dotnet test         # 运行单元测试
.\scripts\publish.ps1   # 发布并启动(PowerShell)
```

发布可用一键脚本 `scripts/release.ps1`（改 csproj 版本号 → 跑单元测试 → commit → 打 tag → push 触发 CI，中途失败自动回滚本地改动）：

```powershell
.\scripts\release.ps1 -Bump patch          # 版本号按 patch 递增（默认）
.\scripts\release.ps1 -Version 1.2.3       # 显式指定版本号
.\scripts\release.ps1 -Bump minor -DryRun  # 只打印将执行的步骤，不做任何改动
.\scripts\release.ps1 -Version 1.2.3 -SkipTests  # 跳过单元测试
.\scripts\release.ps1 -AllowAnyBranch      # 允许在非 main 分支发版（默认仅 main）
```

脚本做的事等价于下面这套手工流程：更新 `src/WindowsGlobalLauncher/WindowsGlobalLauncher.csproj` 中的 `<Version>` → 提交 → 打 tag 并推送：

```bash
git tag v1.2.3
git push origin v1.2.3
```

无论脚本还是手工，最终都是推送 tag，GitHub Actions（`.github/workflows/release.yml`）会自动构建、跑测试、打包并创建 Release（附 zip 与 sha256，release notes 自动生成）。
