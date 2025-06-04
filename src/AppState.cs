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
                var json = File.ReadAllText(AppStateFilePath);
                _state = JsonSerializer.Deserialize<State>(json) ?? new State();
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
        
        public void SetCommandLastExecutedTime(string name, DateTime lastExecuted)
        {
            var info = _state.CommandExecuteInfos.FirstOrDefault(i => i.Name == name);
            if (info != null)
            {
                info.LastExecuted = lastExecuted;
            }
            else
            {
                _state.CommandExecuteInfos.Add(new CommandExecuteInfo { Name = name, LastExecuted = lastExecuted });
            }
            SaveState();
        }
    }
}
