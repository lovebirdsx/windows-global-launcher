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
