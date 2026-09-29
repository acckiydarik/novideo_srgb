using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace novideo_srgb
{
    public static class Logger
    {
        // Upper bound on what is loaded into memory and shown in the log window. Older entries
        // stay on disk (see RetentionDays) and are reachable through "Open folder". The window
        // reports this number next to the entry count, so it must stay an invariant: Log()
        // trims the collection to it as well, not just the startup load.
        public const int MaxLoadedEntries = 6000;

        private const int RetentionDays = 30;
        private const string FilePrefix = "novideo_srgb-";
        private const string FileSuffix = ".log";
        private const string DateFormat = "yyyy-MM-dd";

        // Pre-rotation versions wrote everything here; kept only for the one-time migration.
        private const string LegacyFileName = "novideo_srgb.log";

        public static readonly ObservableCollection<LogEntry> Entries = new ObservableCollection<LogEntry>();

        private static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        private static readonly BlockingCollection<WriteCommand> WriteQueue = new BlockingCollection<WriteCommand>();
        private static readonly Thread WriterThread;
        private static readonly bool PersistenceEnabled;

        private static StreamWriter _writer;
        private static DateTime _writerDate;
        private static int _shutdownCalled;

        public static string LogDirectoryPath => LogDirectory;

        // A queued line together with the date of the entry it belongs to. The date has to
        // travel with the line instead of being taken when the writer runs: this application
        // stays up for days, so a line logged at 23:59:59 can reach the writer after midnight
        // and must still land in the file for the day it was actually logged.
        private struct WriteCommand
        {
            public readonly string Line; // null means "clear everything"
            public readonly DateTime Date;

            public WriteCommand(string line, DateTime date)
            {
                Line = line;
                Date = date;
            }
        }

        private struct LogFile
        {
            public readonly string FullPath;
            public readonly DateTime Date;

            public LogFile(string fullPath, DateTime date)
            {
                FullPath = fullPath;
                Date = date;
            }
        }

        static Logger()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                MigrateLegacyFile();
                DeleteExpiredFiles();
                LoadExistingEntries();

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

            // Keep the in-memory view within the same bound the startup load uses, otherwise a
            // long session would drift past it and the "N / MaxLoadedEntries" counter in the
            // log window would start lying.
            while (Entries.Count > MaxLoadedEntries)
            {
                Entries.RemoveAt(0);
            }

            if (!PersistenceEnabled) return;

            try
            {
                WriteQueue.Add(new WriteCommand(Serialize(entry), entry.Timestamp));
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
                WriteQueue.Add(new WriteCommand(null, DateTime.Now));
            }
            catch
            {
            }
        }

        public static long GetLogFileSizeBytes()
        {
            long total = 0;
            foreach (var file in EnumerateLogFiles())
            {
                try
                {
                    total += new FileInfo(file.FullPath).Length;
                }
                catch
                {
                }
            }

            return total;
        }

        public static event Action Flushed;

        private static void ProcessWriteQueue()
        {
            foreach (var command in WriteQueue.GetConsumingEnumerable())
            {
                try
                {
                    if (command.Line == null)
                    {
                        // Clearing happens on this thread rather than on the caller's: the
                        // FileStream is owned here, so deleting the files anywhere else would
                        // race with an open handle and leave the current day undeletable.
                        CloseWriter();
                        foreach (var file in EnumerateLogFiles())
                        {
                            try
                            {
                                File.Delete(file.FullPath);
                            }
                            catch
                            {
                            }
                        }
                    }
                    else
                    {
                        EnsureWriter(command.Date);
                        _writer.WriteLine(command.Line);
                    }
                }
                catch
                {
                }

                Flushed?.Invoke();
            }

            CloseWriter();
        }

        private static void EnsureWriter(DateTime date)
        {
            if (_writer != null && _writerDate == date.Date) return;

            CloseWriter();

            // Kept open for the process lifetime (or until the date changes) instead of
            // opening/closing the file for every single log line.
            var stream = new FileStream(FilePathFor(date), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            stream.Seek(0, SeekOrigin.End);
            _writer = new StreamWriter(stream) { AutoFlush = true };
            _writerDate = date.Date;
        }

        private static void CloseWriter()
        {
            if (_writer == null) return;

            try
            {
                _writer.Dispose();
            }
            catch
            {
            }

            _writer = null;
        }

        private static string FilePathFor(DateTime date)
        {
            return Path.Combine(LogDirectory,
                FilePrefix + date.ToString(DateFormat, CultureInfo.InvariantCulture) + FileSuffix);
        }

        // Returns the rotated files in chronological order, skipping anything whose name does
        // not parse as a date - the folder is user-visible, so unrelated files must not break
        // loading or, worse, get deleted by the retention pass.
        private static List<LogFile> EnumerateLogFiles()
        {
            var result = new List<LogFile>();

            string[] paths;
            try
            {
                paths = Directory.GetFiles(LogDirectory, FilePrefix + "*" + FileSuffix);
            }
            catch
            {
                return result;
            }

            foreach (var path in paths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name == null || name.Length != FilePrefix.Length + DateFormat.Length) continue;

                DateTime date;
                if (!DateTime.TryParseExact(name.Substring(FilePrefix.Length), DateFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    continue;
                }

                result.Add(new LogFile(path, date));
            }

            result.Sort((a, b) => a.Date.CompareTo(b.Date));
            return result;
        }

        private static void DeleteExpiredFiles()
        {
            var cutoff = DateTime.Now.Date.AddDays(-RetentionDays);

            foreach (var file in EnumerateLogFiles())
            {
                if (file.Date >= cutoff) continue;

                try
                {
                    File.Delete(file.FullPath);
                }
                catch
                {
                }
            }
        }

        // Walks the files newest-first and stops once MaxLoadedEntries is reached, so startup
        // cost is bounded by that number rather than by how much history is on disk.
        private static void LoadExistingEntries()
        {
            var files = EnumerateLogFiles();
            var budget = MaxLoadedEntries;
            var chunks = new List<List<LogEntry>>();

            for (var i = files.Count - 1; i >= 0 && budget > 0; i--)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(files[i].FullPath);
                }
                catch
                {
                    continue;
                }

                var parsed = new List<LogEntry>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        parsed.Add(Deserialize(line));
                    }
                    catch
                    {
                    }
                }

                // A single day can blow the whole budget on its own (an incident produced 765
                // entries in one day, and a storm can produce far more) - keep its tail, which
                // is the part closest to now, not its head.
                if (parsed.Count > budget)
                {
                    parsed.RemoveRange(0, parsed.Count - budget);
                }

                budget -= parsed.Count;
                chunks.Add(parsed);
            }

            // Files were walked newest-first to honour the budget; restore chronological order.
            for (var i = chunks.Count - 1; i >= 0; i--)
            {
                foreach (var entry in chunks[i])
                {
                    Entries.Add(entry);
                }
            }
        }

        // Splits a pre-rotation novideo_srgb.log into the per-day files this version expects.
        // Grouping by the entry's own date (rather than dumping everything into "today") keeps
        // the retention pass and the log window's date grouping honest after an upgrade.
        private static void MigrateLegacyFile()
        {
            var legacyPath = Path.Combine(LogDirectory, LegacyFileName);
            if (!File.Exists(legacyPath)) return;

            try
            {
                var byDate = new Dictionary<DateTime, List<string>>();

                foreach (var line in File.ReadAllLines(legacyPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    DateTime date;
                    try
                    {
                        date = Deserialize(line).Timestamp.Date;
                    }
                    catch
                    {
                        continue;
                    }

                    List<string> bucket;
                    if (!byDate.TryGetValue(date, out bucket))
                    {
                        bucket = new List<string>();
                        byDate[date] = bucket;
                    }

                    bucket.Add(line);
                }

                foreach (var pair in byDate)
                {
                    File.AppendAllLines(FilePathFor(pair.Key), pair.Value);
                }

                File.Delete(legacyPath);
            }
            catch
            {
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
