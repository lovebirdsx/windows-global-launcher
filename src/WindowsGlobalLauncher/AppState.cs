using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CommandLauncher
{
    class AppState
    {
        class CommandExecuteInfo
        {
            public string Name { get; set; } = "";
            public DateTime LastExecuted { get; set; } = DateTime.MinValue;
            public int ExecuteCount { get; set; } = 0;
        }

        class State
        {
            public List<CommandExecuteInfo> CommandExecuteInfos { get; set; } = [];
            // 当前护眼模式名（见 EyeCareManager.Modes），空串表示未设置
            public string EyeCareMode { get; set; } = "";
            // 上次截图标注：工具枚举名（"None"/"Rectangle"/...），空串等同 None
            public string AnnotationTool { get; set; } = "";
            // 上次截图标注：颜色（"#RRGGBB"）、线宽与文字字号（默认与 AnnotationController 一致）
            public string AnnotationStrokeColor { get; set; } = "#FF4040";
            public double AnnotationStrokeWidth { get; set; } = 3.0;
            public double AnnotationTextFontSize { get; set; } = 16.0;
            // 上次检查更新的 UTC 时间，MinValue 表示从未检查
            public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;
            // 用户点「跳过此版本」记下的版本号，如 "1.2.3"，空串表示未跳过
            public string SkippedUpdateVersion { get; set; } = "";
        }

        static string AppStateFilePath
        {
            get
            {
                return Path.Combine(App.BaseDir, "AppState.json");
            }
        }

        static AppState? _instance;
        public static AppState Instance
        {
            get
            {
                _instance ??= new AppState();
                return _instance;
            }
        }

        private State _state = new();

        AppState()
        {
            LoadState();
        }

        void LoadState()
        {
            if (File.Exists(AppStateFilePath))
            {
                try
                {
                    var json = File.ReadAllText(AppStateFilePath);
                    _state = JsonSerializer.Deserialize<State>(json) ?? new State();
                }
                catch (Exception ex)
                {
                    Logger.LogError("读取应用状态失败", ex);
                    // 保持 _state 为默认值
                }
            }
        }

        void SaveState()
        {
            JsonSerializerOptions jsonSerializerOptions = new()
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var options = jsonSerializerOptions;
            var json = JsonSerializer.Serialize(_state, options);
            File.WriteAllText(AppStateFilePath, json);
        }

        public DateTime GetCommandLastExecutedTime(string name)
        {
            return _state.CommandExecuteInfos.FirstOrDefault(info => info.Name == name)?.LastExecuted ?? DateTime.MinValue;
        }

        public int GetCommandExecuteCount(string name)
        {
            return _state.CommandExecuteInfos.FirstOrDefault(info => info.Name == name)?.ExecuteCount ?? 0;
        }

        public string GetEyeCareMode()
        {
            return _state.EyeCareMode;
        }

        public void SetEyeCareMode(string name)
        {
            _state.EyeCareMode = name;
            SaveState();
        }

        public string GetAnnotationTool()
        {
            return _state.AnnotationTool;
        }

        public string GetAnnotationStrokeColor()
        {
            return _state.AnnotationStrokeColor;
        }

        public double GetAnnotationStrokeWidth()
        {
            return _state.AnnotationStrokeWidth;
        }

        public double GetAnnotationTextFontSize()
        {
            return _state.AnnotationTextFontSize;
        }

        /// <summary>一次写回截图标注设置（会话结束保存一次，避免滚轮高频写盘）。</summary>
        public void SetAnnotationSettings(string tool, string strokeColor, double strokeWidth, double textFontSize)
        {
            _state.AnnotationTool = tool;
            _state.AnnotationStrokeColor = strokeColor;
            _state.AnnotationStrokeWidth = strokeWidth;
            _state.AnnotationTextFontSize = textFontSize;
            SaveState();
        }

        /// <summary>上次检查更新的 UTC 时间（从未检查过为 DateTime.MinValue）。</summary>
        public DateTime GetLastUpdateCheckUtc()
        {
            return _state.LastUpdateCheckUtc;
        }

        /// <summary>记录一次更新检查时间（应传 UTC now）。</summary>
        public void SetLastUpdateCheckUtc(DateTime utc)
        {
            _state.LastUpdateCheckUtc = utc;
            SaveState();
        }

        /// <summary>用户「跳过此版本」记下的版本号，空串表示未跳过。</summary>
        public string GetSkippedUpdateVersion()
        {
            return _state.SkippedUpdateVersion;
        }

        /// <summary>记录用户跳过的版本号（如 "1.2.3"）。</summary>
        public void SetSkippedUpdateVersion(string version)
        {
            _state.SkippedUpdateVersion = version;
            SaveState();
        }

        public void RecordCommandExecution(string name)
        {
            var info = _state.CommandExecuteInfos.FirstOrDefault(i => i.Name == name);
            if (info != null)
            {
                info.LastExecuted = DateTime.Now;
                info.ExecuteCount++;
            }
            else
            {
                _state.CommandExecuteInfos.Add(new CommandExecuteInfo { Name = name, LastExecuted = DateTime.Now, ExecuteCount = 1 });
            }
            SaveState();
        }
    }
}
