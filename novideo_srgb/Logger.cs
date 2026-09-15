using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace novideo_srgb
{
    public static class Logger
    {
        public static readonly ObservableCollection<LogEntry> Entries = new ObservableCollection<LogEntry>();

        private static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static readonly string LogFilePath = Path.Combine(LogDirectory, "novideo_srgb.log");

        private static readonly BlockingCollection<string> WriteQueue = new BlockingCollection<string>();
        private static readonly Thread WriterThread;
        private static readonly bool PersistenceEnabled;
        private static StreamWriter _writer;
        private static int _shutdownCalled;

        static Logger()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                LoadExistingEntries();

                // Kept open for the process lifetime instead of opening/closing the file for
                // every single log line, which used to add avoidable file I/O on every Log() call.
                var stream = new FileStream(LogFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                stream.Seek(0, SeekOrigin.End);
                _writer = new StreamWriter(stream) { AutoFlush = true };

                PersistenceEnabled = true;
            }
            catch
            {
                PersistenceEnabled = false;
            }

            WriterThread = new Thread(ProcessWriteQueue) { IsBackground = true };
            WriterThread.Start();
        }

        public static void Log(LogLevel level, string message)
        {
            var entry = new LogEntry(DateTime.Now, level, message);
            Entries.Add(entry);

            if (!PersistenceEnabled) return;

            try
            {
                WriteQueue.Add(Serialize(entry));
            }
            catch
            {
            }
        }

        public static void Shutdown()
        {
            if (Interlocked.Exchange(ref _shutdownCalled, 1) == 1) return;

            WriteQueue.CompleteAdding();
            WriterThread.Join(1000);
        }

        public static void Clear()
        {
            Entries.Clear();

            if (!PersistenceEnabled) return;

            try
            {
                WriteQueue.Add(null);
            }
            catch
            {
            }
        }

        public static long GetLogFileSizeBytes()
        {
            try
            {
                return new FileInfo(LogFilePath).Length;
            }
            catch
            {
                return 0;
            }
        }

        public static event Action Flushed;

        private static void ProcessWriteQueue()
        {
            foreach (var line in WriteQueue.GetConsumingEnumerable())
            {
                try
                {
                    if (line == null)
                    {
                        _writer.BaseStream.SetLength(0);
                        _writer.BaseStream.Position = 0;
                    }
                    else
                    {
                        _writer.WriteLine(line);
                    }
                }
                catch
                {
                }

                Flushed?.Invoke();
            }

            try
            {
                _writer?.Dispose();
            }
            catch
            {
            }
        }

        private static void LoadExistingEntries()
        {
            if (!File.Exists(LogFilePath)) return;

            foreach (var line in File.ReadAllLines(LogFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    Entries.Add(Deserialize(line));
                }
                catch
                {
                }
            }
        }

        private static string Serialize(LogEntry entry)
        {
            var dto = new LogEntryDto
            {
                Timestamp = entry.Timestamp,
                Level = entry.Level,
                Message = entry.Message
            };

            var serializer = new DataContractJsonSerializer(typeof(LogEntryDto));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, dto);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static LogEntry Deserialize(string line)
        {
            var serializer = new DataContractJsonSerializer(typeof(LogEntryDto));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
            {
                var dto = (LogEntryDto)serializer.ReadObject(stream);
                return new LogEntry(dto.Timestamp, dto.Level, dto.Message);
            }
        }

        [DataContract]
        private class LogEntryDto
        {
            [DataMember]
            public DateTime Timestamp { get; set; }

            [DataMember]
            public LogLevel Level { get; set; }

            [DataMember]
            public string Message { get; set; }
        }
    }
}
