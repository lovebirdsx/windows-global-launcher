# Windows Global Launcher

常驻系统托盘的 Windows 桌面工具,包含两个相互独立的功能:命令启动器与 Alt+Tab 窗口切换器。平时隐藏在后台,靠全局热键唤出。

## 功能

### 命令启动器

全局热键(默认 `Ctrl+Shift+I`)弹出搜索式命令面板,从 JSON 配置读取命令并执行。

* 模糊搜索,按使用频率(执行次数 / 最近使用)排序
* 每条命令可单独配置热键,窗口弹出后即可直接触发
* 列表中会显示命令的执行次数与上次执行时间

### Alt+Tab 窗口切换器

* 接管系统 Alt+Tab,竖向列出当前窗口(图标 + 标题)供切换。`Alt+Tab` 向后、`Shift+Tab` 向前,松开 `Alt` 选中,`Esc` 取消
* j，k，p，n 键也可向前/向后切换，x 键关闭当前选中应用

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
  "Commands": [
    {
      "Name": "记事本",
      "Description": "打开Windows记事本",
      "Shell": "notepad.exe",
      "HotKey": "Ctrl+Alt+N"
    }
  ]
}
```

## 开发

源码位于 `src/WindowsGlobalLauncher`,解决方案文件为 `WindowsGlobalLauncher.sln`。

```bash
dotnet build        # 编译
dotnet test         # 运行单元测试
.\scripts\publish.ps1   # 发布并启动(PowerShell)
```
