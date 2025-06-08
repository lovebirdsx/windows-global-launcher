using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace CommandLauncher
{
    public class ConfigCommand
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Shell { get; set; } = "";
        public string HotKey { get; set; } = "";
    }

    // 配置文件模型
    public class Config
    {
        public int MaxDisplayItems { get; set; } = 12;
        public string HotKey { get; set; } = AppConfig.DefaultHotKey;
        public List<ConfigCommand> Commands { get; set; } = [];
    }

    public class AppConfig
    {
        public static readonly string DefaultHotKey = "Ctrl+Shift+I";
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
                    var options = new JsonSerializerOptions
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    var config = JsonSerializer.Deserialize<Config>(json, options) ?? throw new JsonException("反序列化配置文件失败");
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
                Logger.LogError("加载配置文件失败，使用默认配置", ex);
                CreateDefaultConfig();
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
                ]
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

        public static void RestartFileWatcher()
        {
            Instance.StopFileWatcher();
            Instance.StartFileWatcher();
        }

        private void SaveConfig()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_config, options);
                
                // 添加注释说明
                var jsonWithComments = @"{
  // 最大显示条目数
  ""MaxDisplayItems"": " + _config.MaxDisplayItems + @",
  // 全局热键配置 (支持: Ctrl, Alt, Shift, Win + 字母/数字/功能键)
  // 示例: ""Ctrl+Space"", ""Alt+R"", ""Win+L""
  ""HotKey"": """ + _config.HotKey + @""",
  // 命令列表 (每个命令可选配置 HotKey)
  ""Commands"": [";

                for (int i = 0; i < _config.Commands.Count; i++)
                {
                    var cmd = _config.Commands[i];
                    jsonWithComments += $@"
    {{
      ""Name"": ""{cmd.Name}"",
      ""Description"": ""{cmd.Description}"",
      ""Shell"": ""{cmd.Shell}"",
      ""HotKey"": ""{cmd.HotKey}""
    }}";
                    if (i < _config.Commands.Count - 1)
                        jsonWithComments += ",";
                }

                jsonWithComments += @"
  ]
}";
                File.WriteAllText(ConfigPath, jsonWithComments);
                Logger.LogInfo("配置文件保存成功");
            }
            catch (Exception ex)
            {
                Logger.LogError("保存配置文件失败", ex);
                MessageBox.Show($"保存配置文件失败: {ex.Message}");
            }
        }

        ~AppConfig()
        {
            StopFileWatcher();
        }
    }
}
