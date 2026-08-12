# Windows Global Launcher

常驻系统托盘的 Windows 桌面工具,包含四个相互独立的功能:命令启动器、Alt+Tab 窗口切换器、剪贴板历史与护眼模式。平时隐藏在后台,靠全局热键唤出。

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

### 护眼模式

内置 7 种护眼模式（参数对照 CareUEyes 官方文档），在命令面板搜索「护眼」或「eye」即可选择，回车立即生效；托盘菜单「护眼模式」子菜单也可切换（当前模式打勾）。

| 模式 | 效果 |
| ---- | ---- |
| 正常 | 关闭护眼效果（6500K / 100%） |
| 智能 | 色温 5000K，亮度 90%，最大蓝光过滤 |
| 办公 | 色温 5500K，亮度 85% |
| 游戏 | 色温 6500K，亮度 90%，保持画质 |
| 电影 | 色温 6000K，亮度 90% |
| 编辑 | 颜色反转，亮度 85%，适合弱光环境 |
| 阅读 | 灰度墨水屏，色温 5500K，亮度 85% |

* 通过 Magnification 全屏颜色矩阵实现（不依赖显卡 gamma 支持，HDR/新驱动下也能用）
* 当前模式自动保存，程序启动时恢复，退出时还原

### 窗口动作热键

全局生效的热键，在配置文件的 `WindowActions` 段自定义，修改配置后自动热更新。默认绑定：

| 热键         | 动作                   | 说明                             |
| ------------ | ---------------------- | -------------------------------- |
| `Alt+Q`      | `CloseWindow`          | 关闭当前前台窗口（等同 `Alt+F4`） |
| `Win+F12`    | `VolumeUp`             | 增大系统音量                     |
| `Win+F11`    | `VolumeDown`           | 减小系统音量                     |
| `Win+F10`    | `ToggleMute`           | 切换系统静音                     |
| `Ctrl+Alt+C` | `ShowClipboardHistory` | 弹出剪贴板历史                   |

## 运行

* 要求 **.NET 8 运行时**
* **需以管理员权限运行**:窗口切换器依赖低级键盘钩子,否则无法拦截以管理员权限运行的程序的 Alt+Tab
* 程序常驻系统托盘,右键托盘菜单可打开配置、查看日志或退出

## 配置

数据目录为 `%USERPROFILE%\.windows-global-launcher\`,默认配置文件为 `WindowsCommandLauncher.json`(首次运行自动生成)。修改配置文件后会自动重新加载。

在搜索框输入以下内置命令可直接操作:

| 命令        | 说明                |
| ----------- | ------------------- |
| `config`    | 打开配置文件        |
| `setconfig` | 选择 / 切换配置文件 |
| `logs`      | 查看日志            |
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
    { "Action": "ShowClipboardHistory", "HotKey": "Ctrl+Alt+C", "Enabled": true }
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

> `WindowActions` 可选,缺省时补默认绑定。`Action` 当前可用值:`CloseWindow`(关闭前台窗口)、`VolumeUp`/`VolumeDown`(增大/减小系统音量)、`ToggleMute`(切换静音)、`ShowClipboardHistory`(剪贴板历史);`Enabled` 设为 `false` 可临时停用某条绑定。修饰键为精确匹配(如配置 `Alt+Q` 时 `Alt+Shift+Q` 不会触发)。

## 开发

源码位于 `src/WindowsGlobalLauncher`,解决方案文件为 `WindowsGlobalLauncher.sln`。

```bash
dotnet build        # 编译
dotnet test         # 运行单元测试
.\scripts\publish.ps1   # 发布并启动(PowerShell)
```
