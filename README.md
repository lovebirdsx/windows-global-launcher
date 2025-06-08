# 说明

本项目是一个 Windows 平台的命令行工具启动器，旨在通过系统托盘图标快速启动常用命令行工具。

该软件支持通过 JSON 配置文件定义要显示的指令列表，每个指令可单独配置快捷键，
一旦设置快捷键，在界面中会显示对应提示，并且在窗口弹出后即可触发。

## 开发

项目源码位于 `src/WindowsGlobalLauncher` 目录，解决方案文件为 `WindowsGlobalLauncher.sln`。

* 单元测试：`dotnet test`
* 发布: `.\scripts\publish.ps1`

## 运行

要求 .NET 8 运行时

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
