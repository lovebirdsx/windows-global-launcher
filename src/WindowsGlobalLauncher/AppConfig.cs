using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;

namespace CommandLauncher
{
    public class ConfigCommand
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Shell { get; set; } = "";
        public string? HotKey { get; set; } = "";
        // 是否以管理员权限启动该命令。默认 false（普通用户权限），true 时保留管理员权限启动。
        public bool RunAsAdmin { get; set; } = false;
    }

    // 窗口动作热键配置：动作名（见 WindowActions 注册表）+ 热键字符串，全局生效
    public class ConfigWindowAction
    {
        public string Action { get; set; } = "CloseWindow";
        public string HotKey { get; set; } = "Alt+Q";
        public bool Enabled { get; set; } = true;
    }

    // 配置文件模型
    public class Config
    {
        public int MaxDisplayItems { get; set; } = 12;
        public string HotKey { get; set; } = AppConfig.DefaultHotKey;
        public List<ConfigCommand> Commands { get; set; } = [];
        public List<ConfigWindowAction> WindowActions { get; set; } = [];
    }

    public class AppConfig
    {
        public static readonly string DefaultHotKey = "Ctrl+Shift+I";

        /// <summary>默认的窗口动作热键（LoadConfig 兼容旧配置与 CreateDefaultConfig 共用）。
        /// 恒为每个注册动作给出一条绑定；不需要默认热键的新动作（如护眼模式）用空字符串表示「默认不绑定」。</summary>
        public static List<ConfigWindowAction> DefaultWindowActions() =>
        [
            new() { Action = "CloseWindow", HotKey = "Alt+Q" },
            new() { Action = "VolumeUp", HotKey = "Win+F12" },
            new() { Action = "VolumeDown", HotKey = "Win+F11" },
            new() { Action = "ToggleMute", HotKey = "Win+F10" },
            new() { Action = "ShowClipboardHistory", HotKey = "Ctrl+Alt+C" },
            new() { Action = "Screenshot", HotKey = "F4" },
            new() { Action = "PinClipboard", HotKey = "F7" },
            new() { Action = "TogglePinVisibility", HotKey = "Shift+F7" },
            new() { Action = "PinBoxSelect", HotKey = "Win+Q" },
            new() { Action = "ShowContextMenu", HotKey = "Ctrl+Alt+Enter" },
            new() { Action = "EyeCareNormal", HotKey = "" },
            new() { Action = "EyeCareOffice", HotKey = "" },
            new() { Action = "OpenHotkeySettings", HotKey = "" },
        ];
        public static string ConfigPath
        {
            get
            {
                var configSavePath = Path.Combine(App.BaseDir, "ConfigPath.txt");
                if (!File.Exists(configSavePath))
                {
                    return Path.Combine(App.BaseDir, App.AppName + ".json");
                }

                var configPath = File.ReadAllText(configSavePath).Trim();
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
                {
                    return Path.Combine(App.BaseDir, App.AppName + ".json");
                }

                return configPath;
            }
            set
            {
                // 确保用户配置目录存在
                if (!Directory.Exists(App.BaseDir))
                {
                    Directory.CreateDirectory(App.BaseDir);
                }

                File.WriteAllText(Path.Combine(App.BaseDir, "ConfigPath.txt"), value);
            }
        }

        private static AppConfig? _instance;
        public static AppConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new AppConfig();
                    _instance.LoadConfig();
                }
                return _instance;
            }
        }

        public static void OpenConfigFile()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ConfigPath,
                        UseShellExecute = true
                    });
                    Logger.LogInfo("打开配置文件");
                }
                else
                {
                    MessageBox.Show("配置文件不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("打开配置文件失败", ex);
                MessageBox.Show($"打开配置文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void SetConfigFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择配置文件",
                Filter = "JSON 文件 (*.json)|*.json",
                InitialDirectory = Path.GetDirectoryName(ConfigPath) ?? App.BaseDir,
            };

            if (dialog.ShowDialog() == true)
            {
                ConfigPath = dialog.FileName;
                Instance.LoadConfig();
                Instance.StartFileWatcher();
                Logger.LogInfo($"配置文件已设置为: {ConfigPath}");
            }
        }

        public Config Config => _config;
        private Config _config = new();

        private FileSystemWatcher? _fileWatcher;
        private DateTime _lastWriteTime = DateTime.MinValue;

        // 自身写盘后的 watcher 回声抑制窗口（Environment.TickCount64 毫秒刻度）。
        // SaveConfig 写盘前设置，OnConfigFileChanged 在其内直接忽略：写盘后再更新时间戳存在竞态
        // （watcher 线程可能在时间戳更新前就完成判定），提前设时间窗更可靠。
        // 只吞自己写入的回声；窗口内（1 秒）的外部改动理论上会被漏过一次，窗口极小，可接受。
        private long _suppressWatcherReloadUntil;

        /// <summary>反序列化配置的统一选项（LoadConfig 与 SaveConfig 写盘前自检共用）。</summary>
        private static readonly JsonSerializerOptions LoadOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        // 配置更新事件
        public event Action? ConfigUpdated;

        private AppConfig()
        {
            StartFileWatcher();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<Config>(json, LoadOptions) ?? throw new JsonException("反序列化配置文件失败");
                    // 字段兜底：缺字段或显式写 null 都会走到这里，避免后续 NRE 与空热键
                    config.Commands ??= [];
                    if (string.IsNullOrWhiteSpace(config.HotKey))
                    {
                        Logger.LogInfo($"配置中的命令面板热键为空，回退为默认热键 {DefaultHotKey}");
                        config.HotKey = DefaultHotKey;
                    }
                    // 向后兼容：旧配置文件没有 WindowActions 段时，补默认绑定，行为不回归
                    if (config.WindowActions == null || config.WindowActions.Count == 0)
                        config.WindowActions = DefaultWindowActions();
                    // 向后兼容：旧配置缺少后续新增动作的绑定时补上默认热键（仅追加，不覆盖用户已有绑定）。
                    // 各动作独立 if 判定（不能用 else-if 链，否则用户同时缺多个绑定时只会补第一个）
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "ShowClipboardHistory", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "ShowClipboardHistory", HotKey = "Ctrl+Alt+C" });
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "Screenshot", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "Screenshot", HotKey = "F4" });
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "PinClipboard", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "PinClipboard", HotKey = "F7" });
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "TogglePinVisibility", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "TogglePinVisibility", HotKey = "Shift+F7" });
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "PinBoxSelect", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "PinBoxSelect", HotKey = "Win+Q" });
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "ShowContextMenu", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "ShowContextMenu", HotKey = "Ctrl+Alt+Enter" });
                    // 后续新增的「默认不绑定」动作：补齐条目（空热键），让配置文件保持自解释、设置窗口开箱可见
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "EyeCareNormal", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "EyeCareNormal", HotKey = "" });
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "EyeCareOffice", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "EyeCareOffice", HotKey = "" });
                    if (!config.WindowActions.Any(a => string.Equals(a.Action, "OpenHotkeySettings", StringComparison.OrdinalIgnoreCase)))
                        config.WindowActions.Add(new() { Action = "OpenHotkeySettings", HotKey = "" });
                    _config = config;
                    Logger.LogInfo($"成功加载配置文件，包含 {config.Commands.Count} 个命令");

                    // 触发配置更新事件
                    ConfigUpdated?.Invoke();
                }
                else
                {
                    Logger.LogInfo("配置文件不存在，创建默认配置");
                    CreateDefaultConfig();
                }
            }
            catch (Exception ex)
            {
                // 解析/读取失败时刻意不覆写用户文件：旧行为是直接 CreateDefaultConfig 覆盖，
                // 一旦文件里有一个笔误（或写入方拼出非法 JSON），用户的全部配置就被清空。
                // 现在只在内存里退回默认值继续运行，文件原样保留供用户修复。
                Logger.LogError("加载配置文件失败，已保留原文件，本次以内存中的默认值运行（请检查配置文件格式）", ex);
                if (_config.WindowActions.Count == 0)
                    _config.WindowActions = DefaultWindowActions();
            }
        }

        private void CreateDefaultConfig()
        {
            _config = new Config
            {
                MaxDisplayItems = 12,
                HotKey = DefaultHotKey,
                Commands =
                [
                    new() { Name = "记事本", Description = "打开Windows记事本", Shell = "notepad.exe" , HotKey = "" },
                    new() { Name = "计算器", Description = "打开Windows计算器", Shell = "calc.exe" , HotKey = "" },
                    new() { Name = "命令提示符", Description = "打开命令提示符", Shell = "cmd.exe" , HotKey = "" },
                    new() { Name = "画图", Description = "打开Windows画图程序", Shell = "mspaint.exe" , HotKey = "" },
                    new() { Name = "任务管理器", Description = "打开任务管理器", Shell = "taskmgr.exe" , HotKey = "" }
                ],
                WindowActions = DefaultWindowActions()
            };
            SaveConfig();
        }

        private void StartFileWatcher()
        {
            try
            {
                StopFileWatcher();
                
                var configDir = Path.GetDirectoryName(ConfigPath);
                var configFileName = Path.GetFileName(ConfigPath);
                
                if (string.IsNullOrEmpty(configDir) || string.IsNullOrEmpty(configFileName))
                    return;

                _fileWatcher = new FileSystemWatcher(configDir, configFileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += OnConfigFileChanged;
                Logger.LogInfo("开始监听配置文件变化");
            }
            catch (Exception ex)
            {
                Logger.LogError("启动文件监听失败", ex);
            }
        }

        private void StopFileWatcher()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Changed -= OnConfigFileChanged;
                _fileWatcher.Dispose();
                _fileWatcher = null;
                Logger.LogInfo("停止监听配置文件变化");
            }
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // 自己写盘触发的回声：SaveConfig 写盘前置的抑制时间窗内直接忽略
                if (Environment.TickCount64 < _suppressWatcherReloadUntil)
                    return;

                // 防止重复触发
                var lastWrite = File.GetLastWriteTime(ConfigPath);
                if (lastWrite <= _lastWriteTime.AddMilliseconds(100))
                    return;
                
                _lastWriteTime = lastWrite;
                
                // 延迟一点时间，确保文件写入完成
                System.Threading.Thread.Sleep(100);
                
                Logger.LogInfo("检测到配置文件变化，正在重新加载...");
                LoadConfig();
            }
            catch (Exception ex)
            {
                Logger.LogError("重新加载配置文件失败", ex);
            }
        }

        public void ReloadConfig()
        {
            LoadConfig();
        }

        /// <summary>
        /// 保存配置并立即重新加载生效（快捷键设置窗口等 UI 修改后调用）。
        /// 保存失败不重载：避免界面显示出「已生效」而实际未写入的状态。
        /// </summary>
        public bool SaveAndReload()
        {
            if (!SaveConfig())
                return false;

            ReloadConfig();
            return true;
        }

        public static void RestartFileWatcher()
        {
            Instance.StopFileWatcher();
            Instance.StartFileWatcher();
        }

        /// <summary>
        /// 把配置对象拼装成带注释的 JSON 文本（纯静态，便于单测）。
        /// 保持手写注释格式便于用户直接编辑；所有字符串值经 JsonSerializer 转义——
        /// 旧版直接字符串拼插，Shell/名称里出现反斜杠（如 C:\Program Files\x.exe）或引号会写出非法 JSON，
        /// 下次加载解析失败会走「解析失败」分支导致配置丢失。
        /// </summary>
        public static string ComposeConfigJson(Config config)
        {
            var stringOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string Str(string? value) => JsonSerializer.Serialize(value ?? "", stringOptions);

            var sb = new StringBuilder();
            sb.Append(@"{
  // 最大显示条目数
  ""MaxDisplayItems"": ").Append(config.MaxDisplayItems).Append(@",
  // 全局热键配置 (支持: Ctrl, Alt, Shift, Win 修饰键 + 字母/数字/F1-F12/方向键/编辑键/常用标点)
  // 示例: ""Ctrl+Space"", ""Alt+R"", ""Win+L""；命令面板执行 hotkeys 可打开快捷键设置窗口可视化修改
  ""HotKey"": ").Append(Str(config.HotKey)).Append(@",
  // 窗口动作热键（全局生效；HotKey 为空表示不绑定该动作）
  // 可用 Action: ""CloseWindow""(关闭前台窗口，等同 Alt+F4) / ""VolumeUp""(增大音量) / ""VolumeDown""(减小音量) / ""ToggleMute""(切换静音) / ""ShowClipboardHistory""(剪贴板历史) / ""Screenshot""(区域截图) / ""PinClipboard""(把剪贴板图片钉为屏幕贴图) / ""TogglePinVisibility""(隐藏/显示所有贴图) / ""PinBoxSelect""(框选贴图整体移动) / ""ShowContextMenu""(模拟 Shift+F10 弹出右键菜单) / ""EyeCareNormal""(护眼：正常) / ""EyeCareOffice""(护眼：办公) / ""OpenHotkeySettings""(打开快捷键设置)
  ""WindowActions"": [");

            for (int i = 0; i < config.WindowActions.Count; i++)
            {
                var action = config.WindowActions[i];
                sb.Append(@"
    {
      ""Action"": ").Append(Str(action.Action)).Append(@",
      ""HotKey"": ").Append(Str(action.HotKey)).Append(@",
      ""Enabled"": ").Append(action.Enabled ? "true" : "false").Append(@"
    }");
                if (i < config.WindowActions.Count - 1)
                    sb.Append(',');
            }

            sb.Append(@"
  ],
  // 命令列表 (每个命令可选配置 HotKey；RunAsAdmin=true 时以管理员权限启动，默认 false 为普通用户权限)
  ""Commands"": [");

            for (int i = 0; i < config.Commands.Count; i++)
            {
                var cmd = config.Commands[i];
                sb.Append(@"
    {
      ""Name"": ").Append(Str(cmd.Name)).Append(@",
      ""Description"": ").Append(Str(cmd.Description)).Append(@",
      ""Shell"": ").Append(Str(cmd.Shell)).Append(@",
      ""HotKey"": ").Append(Str(cmd.HotKey)).Append(@",
      ""RunAsAdmin"": ").Append(cmd.RunAsAdmin ? "true" : "false").Append(@"
    }");
                if (i < config.Commands.Count - 1)
                    sb.Append(',');
            }

            sb.Append(@"
  ]
}");
            return sb.ToString();
        }

        /// <summary>
        /// 把当前配置写回配置文件。写盘前先用 LoadConfig 同款选项自检拼装结果，
        /// 拼出非法 JSON 就放弃保存（宁可不存也不能让用户配置在下次加载时被判定为损坏）；失败返回 false。
        /// </summary>
        public bool SaveConfig()
        {
            try
            {
                var json = ComposeConfigJson(_config);

                // 写盘前自检：反序列化一遍，确保写出的一定能被 LoadConfig 读回来
                _ = JsonSerializer.Deserialize<Config>(json, LoadOptions) ?? throw new JsonException("配置 JSON 自检反序列化为 null");

                _suppressWatcherReloadUntil = Environment.TickCount64 + 1000;
                File.WriteAllText(ConfigPath, json);
                _lastWriteTime = File.GetLastWriteTime(ConfigPath);
                Logger.LogInfo("配置文件保存成功");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("保存配置文件失败", ex);
                return false;
            }
        }

        ~AppConfig()
        {
            StopFileWatcher();
        }
    }
}
