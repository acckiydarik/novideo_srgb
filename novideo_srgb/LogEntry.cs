using System;

namespace novideo_srgb
{
    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error,
        Off
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Message { get; }

        public LogEntry(DateTime timestamp, LogLevel level, string message)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
        }

        public string DateLabel => Timestamp.ToString("yyyy-MM-dd");
        public string TimeLabel => Timestamp.ToString("HH:mm:ss");
    }
}
