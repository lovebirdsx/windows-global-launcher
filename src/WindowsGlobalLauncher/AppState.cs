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
