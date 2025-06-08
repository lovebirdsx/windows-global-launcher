using System;
using System.ComponentModel;

namespace CommandLauncher
{
    // 命令模型
    public class Command : INotifyPropertyChanged
    {
        public required string Name { get; set; }
        public required string Description { get; set; }
        public required string Shell { get; set; }
        public string HotKey { get; set; } = "";
        public DateTime LastExecuted { get; set; } = DateTime.MinValue;
        public double MatchScore { get; set; } = 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
