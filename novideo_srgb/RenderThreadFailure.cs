using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Media;

namespace novideo_srgb
{
    // Detection and bookkeeping for WPF render thread failures ("zombied composition
    // partition"), plus storm suppression for the log.
    //
    // Background (see DEVELOPMENT.md for the full investigation): when the composition render
    // thread dies, CPartitionManager::ZombifyPartitionAndCompleteProcessing collapses the real
    // HRESULT into one of three buckets and stores it in the channel. The stored value is never
    // reset - clientchannel.h has a setter and no resetter - so every subsequent SyncFlush
    // rethrows it. HwndTarget.UpdateWindowSettings throws before it can post the message that
    // would re-enable the render target, leaving the window permanently unpainted while the
    // process keeps running.
    //
    // Recovery inside the process is impossible through public WPF APIs. This was verified
    // experimentally: switching ProcessRenderMode/HwndTarget.RenderMode to SoftwareOnly,
    // recreating the window, and even creating a window on a brand new UI thread all keep
    // failing (713, 702 and "thread dies on window creation" respectively), and releasing the
    // resource pressure does not help either (1317 further exceptions). The only cure is
    // restarting the process.
    public static class RenderThreadFailure
    {
        // NVAPI-style raw HRESULTs the composition layer reports through the managed wrapper.
        private const int UceErrRenderThreadFailure = unchecked((int)0x88980406);
        private const int EOutOfMemory = unchecked((int)0x8007000E);

        // A single occurrence is not proof: the WPF team states it "not means that the wpf
        // render thread completely break. But in most cases, it will break the render thread".
        // Requiring a short run of them keeps a one-off transient from triggering a restart.
        private const int ConfirmAfterCount = 5;
        private const int ConfirmWindowSeconds = 10;

        // Storm suppression, modelled on syslog's "last message repeated N times" and
        // journald's RateLimitBurst: log the first few, then keep counting silently.
        private const int LogBurst = 3;
        private const int AggregateAfterQuietSeconds = 5;

        private static readonly object Sync = new object();

        private static string _currentKey;
        private static int _suppressedCount;
        private static DateTime _keyFirstSeen;
        private static DateTime _keyLastSeen;

        private static int _failureCount;
        private static DateTime _failureWindowStart;
        private static bool _snapshotLogged;

        /// <summary>
        /// True if the exception came from the composition layer rather than from ordinary
        /// managed code. Matching is done on HRESULT and stack frames only - the exception
        /// message is localised and must never be used.
        /// </summary>
        public static bool IsCompositionFailure(Exception exception)
        {
            if (exception == null) return false;

            if (exception.HResult == UceErrRenderThreadFailure || exception.HResult == EOutOfMemory)
            {
                // A real managed OutOfMemoryException also carries E_OUTOFMEMORY, so the code
                // alone is not enough - the stack still has to point at the composition layer.
                if (HasCompositionFrame(exception)) return true;
            }

            if (!(exception is OutOfMemoryException) && !(exception is InvalidOperationException))
            {
                return false;
            }

            return HasCompositionFrame(exception);
        }

        private static bool HasCompositionFrame(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                var stack = current.StackTrace;
                if (stack == null) continue;

                if (stack.IndexOf("SyncFlush", StringComparison.Ordinal) >= 0 ||
                    stack.IndexOf("NotifyPartitionIsZombie", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Records a recognised composition failure and reports whether the partition should
        /// now be treated as permanently dead.
        /// </summary>
        public static bool RegisterFailure()
        {
            lock (Sync)
            {
                var now = DateTime.Now;

                if (_failureCount == 0 || (now - _failureWindowStart).TotalSeconds > ConfirmWindowSeconds)
                {
                    _failureWindowStart = now;
                    _failureCount = 0;
                }

                _failureCount++;
                return _failureCount >= ConfirmAfterCount;
            }
        }

        /// <summary>
        /// Writes the one-off diagnostic snapshot for the first failure of a process lifetime.
        /// </summary>
        public static void LogSnapshotOnce(Exception exception)
        {
            lock (Sync)
            {
                if (_snapshotLogged) return;
                _snapshotLogged = true;
            }

            var text = new StringBuilder("Render thread failure diagnostics: ");

            try
            {
                text.Append("hresult=0x").Append(exception.HResult.ToString("X8"));

                var process = Process.GetCurrentProcess();
                text.Append(", uptime=").Append((int)(DateTime.Now - process.StartTime).TotalMinutes).Append(" min");
                text.Append(", privateMB=").Append(process.PrivateMemorySize64 / 1024 / 1024);

                // Total handle count, not just GDI/USER: in dotnet/wpf#3633 the only case where
                // this stack was ever traced to a root cause, it was a kernel handle leak
                // injected into the process by the NVIDIA overlay, with GDI/USER looking normal.
                text.Append(", handles=").Append(process.HandleCount);
                text.Append(", threads=").Append(process.Threads.Count);
                text.Append(", renderTier=").Append(RenderCapability.Tier >> 16);
                text.Append(", renderMode=").Append(RenderOptions.ProcessRenderMode);
            }
            catch (Exception e)
            {
                text.Append(" (incomplete: ").Append(e.Message).Append(')');
            }

            Logger.Log(LogLevel.Error, text.ToString());
        }

        /// <summary>
        /// Decides whether this exception should be written to the log in full. Repeats of the
        /// same signature are counted instead, and released later by
        /// <see cref="FlushAggregate"/>.
        /// </summary>
        public static bool ShouldLog(Exception exception, out string pendingAggregate)
        {
            pendingAggregate = null;
            var key = BuildKey(exception);
            var now = DateTime.Now;

            lock (Sync)
            {
                if (key != _currentKey)
                {
                    // A different failure must never be hidden behind another one's counter:
                    // release what was pending and start over.
                    pendingAggregate = BuildAggregate();
                    _currentKey = key;
                    _suppressedCount = 0;
                    _keyFirstSeen = now;
                    _keyLastSeen = now;
                    return true;
                }

                _keyLastSeen = now;
                _suppressedCount++;

                // _suppressedCount counts occurrences after the first one, so the burst covers
                // entries 1..LogBurst.
                return _suppressedCount < LogBurst;
            }
        }

        /// <summary>
        /// Returns the "repeated N more times" line once a storm has gone quiet, or null if
        /// there is nothing to report yet. Called from the poll timer.
        /// </summary>
        public static string FlushIfQuiet()
        {
            lock (Sync)
            {
                if (_currentKey == null || _suppressedCount < LogBurst) return null;
                if ((DateTime.Now - _keyLastSeen).TotalSeconds < AggregateAfterQuietSeconds) return null;

                var text = BuildAggregate();
                _currentKey = null;
                _suppressedCount = 0;
                return text;
            }
        }

        /// <summary>
        /// Releases any pending aggregate unconditionally - used on shutdown so the tail of a
        /// storm is not lost.
        /// </summary>
        public static string FlushAggregate()
        {
            lock (Sync)
            {
                var text = BuildAggregate();
                _currentKey = null;
                _suppressedCount = 0;
                return text;
            }
        }

        // Caller must hold Sync.
        private static string BuildAggregate()
        {
            if (_currentKey == null || _suppressedCount < LogBurst) return null;

            var hidden = _suppressedCount - (LogBurst - 1);
            var seconds = Math.Max(1, (int)(_keyLastSeen - _keyFirstSeen).TotalSeconds);
            return "Previous error repeated " + hidden + (hidden == 1 ? " more time in " : " more times in ") +
                   seconds + " s";
        }

        // Type plus the first stack frame: stable across occurrences of the same failure, and
        // free of the variable data a formatted message would carry.
        private static string BuildKey(Exception exception)
        {
            var stack = exception.StackTrace;
            var frame = "(no stack)";

            if (!string.IsNullOrEmpty(stack))
            {
                var end = stack.IndexOf('\n');
                frame = (end < 0 ? stack : stack.Substring(0, end)).Trim();
            }

            return exception.GetType().FullName + " | " + frame;
        }
    }
}
