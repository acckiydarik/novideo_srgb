using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;

namespace novideo_srgb
{
    // ---------------------------------------------------------------------------------------
    // CLI/IPC support: single-instance names, argument parsing, length-prefixed pipe protocol,
    // client (secondary process) and server (primary instance) sides, and console output for a
    // WinExe process. See plan.md ("CLI-команды управления clamp") for the full contract.
    // ---------------------------------------------------------------------------------------

    public enum CliCommandType
    {
        None, // normal GUI start (with or without -minimize)
        Help,
        Version,
        Enable,
        Disable,
        Toggle,
        Status
    }

    public class CliArguments
    {
        public CliCommandType Command = CliCommandType.None;
        public int? Index;
        public string Error;

        // Modifier for a normal GUI start, like -minimize: forces WPF onto the software
        // rendering path before any window exists. Deliberately NOT a CliCommandType - the
        // "only one command per invocation" and "-minimize cannot be combined" checks below
        // key off Command, and a modifier must not trip either of them.
        public bool SoftwareRender;

        // Set only by a process restarting itself after a render thread failure. The old
        // instance may still hold the single-instance mutex for a moment, so this start is
        // allowed to wait for it - an ordinary launch is not, because there the mutex is held
        // by a healthy instance that should be activated immediately.
        public bool RestartHandover;

        public bool IsControlCommand =>
            Command == CliCommandType.Enable || Command == CliCommandType.Disable ||
            Command == CliCommandType.Toggle || Command == CliCommandType.Status;

        public static CliArguments Parse(string[] args)
        {
            var result = new CliArguments();
            var minimizeSeen = false;
            var softwareRenderSeen = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                CliCommandType type;
                switch (arg.ToLowerInvariant())
                {
                    case "-minimize":
                        minimizeSeen = true;
                        continue;
                    case "--software-render":
                        softwareRenderSeen = true;
                        result.SoftwareRender = true;
                        continue;
                    case "--restart-handover":
                        // Internal: passed by a process restarting itself. Not listed in the
                        // help output because it is meaningless on a hand-typed command line.
                        result.RestartHandover = true;
                        continue;
                    case "--help":
                        type = CliCommandType.Help;
                        break;
                    case "--version":
                        type = CliCommandType.Version;
                        break;
                    case "--enable":
                        type = CliCommandType.Enable;
                        break;
                    case "--disable":
                        type = CliCommandType.Disable;
                        break;
                    case "--toggle":
                        type = CliCommandType.Toggle;
                        break;
                    case "--status":
                        type = CliCommandType.Status;
                        break;
                    default:
                        result.Error = "Unknown argument: " + arg;
                        return result;
                }

                if (result.Command != CliCommandType.None)
                {
                    result.Error = "Only one command per invocation is supported";
                    return result;
                }

                result.Command = type;

                var takesIndex = type == CliCommandType.Enable || type == CliCommandType.Disable ||
                                 type == CliCommandType.Toggle || type == CliCommandType.Status;
                if (takesIndex && i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    i++;
                    if (!int.TryParse(args[i], out var index) || index <= 0)
                    {
                        result.Error = "Invalid monitor index: " + args[i] +
                                       " (expected a positive number matching the # column)";
                        return result;
                    }

                    result.Index = index;
                }
            }

            // -minimize only makes sense for a normal GUI start; combined with a control
            // command the GUI is never created, so treat it as an argument error rather than
            // silently ignoring it.
            if (minimizeSeen && result.Command != CliCommandType.None)
            {
                result.Error = "-minimize cannot be combined with " + "--" +
                               result.Command.ToString().ToLowerInvariant();
            }

            // Same reasoning for --software-render: it only affects how this process renders
            // its own window, which a control command never creates.
            if (softwareRenderSeen && result.Command != CliCommandType.None)
            {
                result.Error = "--software-render cannot be combined with " + "--" +
                               result.Command.ToString().ToLowerInvariant();
            }

            return result;
        }
    }

    public class CliCommandResult
    {
        public int Code { get; }
        public string Text { get; }

        public CliCommandResult(int code, string text)
        {
            Code = code;
            Text = text;
        }

        public static CliCommandResult FromOutcome(ClampCommandOutcome outcome, MonitorData monitor)
        {
            var text = "#" + monitor.Number + " " + monitor.Name + ": " + DescribeOutcome(outcome, monitor);
            switch (outcome)
            {
                case ClampCommandOutcome.Applied:
                case ClampCommandOutcome.AlreadyInState:
                case ClampCommandOutcome.AcceptedPending:
                    return new CliCommandResult(0, text);
                case ClampCommandOutcome.Unavailable:
                    return new CliCommandResult(4, text);
                default:
                    return new CliCommandResult(5, text);
            }
        }

        public static string DescribeOutcome(ClampCommandOutcome outcome, MonitorData monitor)
        {
            switch (outcome)
            {
                case ClampCommandOutcome.Applied:
                    return monitor.Clamped ? "clamp enabled" : "clamp disabled";
                case ClampCommandOutcome.AlreadyInState:
                    return "already in requested state";
                case ClampCommandOutcome.AcceptedPending:
                    return "accepted, driver busy (-104), retrying in background";
                case ClampCommandOutcome.Unavailable:
                    return "unavailable (" + monitor.ClampOffReason + ")";
                default:
                    return "error applying clamp (see log)";
            }
        }
    }

    public static class CliHelp
    {
        public const string Usage =
            "novideo_srgb command line usage:\r\n" +
            "\r\n" +
            "  novideo_srgb.exe --help             Show this help\r\n" +
            "  novideo_srgb.exe --version          Print the application version\r\n" +
            "  novideo_srgb.exe --enable [index]   Enable the sRGB clamp\r\n" +
            "  novideo_srgb.exe --disable [index]  Disable the sRGB clamp\r\n" +
            "  novideo_srgb.exe --toggle [index]   Toggle the sRGB clamp\r\n" +
            "  novideo_srgb.exe --status [index]   Print clamp state without changing it\r\n" +
            "\r\n" +
            "  index  Optional 1-based monitor number matching the # column in the GUI.\r\n" +
            "         Without an index the command applies to all monitors.\r\n" +
            "\r\n" +
            "Control commands require an already running instance (they do not start one).\r\n" +
            "\r\n" +
            "Startup options (these start the GUI, they are not control commands and return\r\n" +
            "no exit code of their own):\r\n" +
            "\r\n" +
            "  novideo_srgb.exe -minimize          Start hidden in the tray\r\n" +
            "  novideo_srgb.exe --software-render  Start with GPU rendering disabled\r\n" +
            "\r\n" +
            "  --software-render draws the window on the CPU. Use it if the window stays\r\n" +
            "  blank or never redraws after a display driver problem. The two options can be\r\n" +
            "  combined; neither can be combined with a control command.\r\n" +
            "\r\n" +
            "Exit codes:\r\n" +
            "  0  success (including \"already in requested state\" and accepted-but-retrying)\r\n" +
            "  1  application is not running\r\n" +
            "  2  invalid arguments\r\n" +
            "  3  no monitor with the given index\r\n" +
            "  4  clamp is unavailable right now (e.g. HDR active, no ICC profile)\r\n" +
            "  5  internal error while applying the command\r\n" +
            "  6  access denied (elevation mismatch with the running instance)\r\n" +
            "  7  the running instance did not respond (IPC timeout/failure)\r\n" +
            "\r\n" +
            "Note: this is a GUI-subsystem executable - an interactive cmd/PowerShell prompt\r\n" +
            "does not wait for it. Batch files and Task Scheduler work as expected; for\r\n" +
            "interactive use run it via 'start /wait' (cmd) or 'Start-Process -Wait'.";
    }

    // Writes CLI output to the parent console when there is one (attached via kernel32
    // AttachConsole), otherwise falls back to a message box - this is a WinExe process with no
    // console of its own.
    public static class ConsoleOutput
    {
        private const int AttachParentProcess = -1;
        private const int StdOutputHandle = -11;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        private static bool _initialized;
        private static bool _usable;
        private static bool _attachedToConsole;

        private static bool TryInitialize()
        {
            if (_initialized) return _usable;
            _initialized = true;

            // If stdout was redirected by the caller (batch `> file`, PowerShell pipeline),
            // the handle is already inherited and usable - and calling AttachConsole would
            // RESET the standard handles to the console screen buffer, silently breaking the
            // redirection. Only attach when there is no inherited stdout at all.
            var stdoutHandle = GetStdHandle(StdOutputHandle);
            var hasInheritedStdout = stdoutHandle != IntPtr.Zero && stdoutHandle != new IntPtr(-1);

            if (!hasInheritedStdout)
            {
                if (!AttachConsole(AttachParentProcess)) return false;
                _attachedToConsole = true;
            }

            // The process started without a console, so the cached standard streams point
            // nowhere - reopen them before first use.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stderr);
            _usable = true;
            return true;
        }

        public static void Write(string text, bool allowGuiFallback)
        {
            if (TryInitialize())
            {
                if (_attachedToConsole)
                {
                    // Leading newline separates the output from the interactive prompt that
                    // has already been printed (the shell does not wait for a GUI-subsystem
                    // process).
                    Console.WriteLine();
                }

                Console.WriteLine(text);
                return;
            }

            // No console at all (launched from a process that has none: Task Scheduler,
            // AutoHotkey, a desktop shortcut). A modal box here would block the process until
            // somebody dismisses it - and under "run whether user is logged on or not" it is
            // drawn on an invisible session-0 desktop, so the command would hang forever and
            // never return its exit code. Only commands a user explicitly runs for their own
            // benefit (--help/--version) may fall back to a window; control commands stay
            // silent and communicate purely through the exit code.
            if (allowGuiFallback)
            {
                System.Windows.MessageBox.Show(text, "novideo_srgb");
            }
        }
    }

    public static class SingleInstance
    {
        // Names are bound to the current user's SID so that multiple users on one machine
        // (fast user switching, terminal server) get independent instances instead of
        // colliding on a fixed string.
        private static string Sid
        {
            get
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    return identity.User != null ? identity.User.Value : "nosid";
                }
            }
        }

        public static string MutexName => "novideo_srgb_single_instance_" + Sid;
        public static string PipeName => "novideo_srgb_cli_" + Sid;
    }

    [DataContract]
    public class PipeMessage
    {
        [DataMember(Name = "kind")] public string Kind;   // "hello" | "command" | "response"
        [DataMember(Name = "pid")] public int Pid;        // hello: server process id
        [DataMember(Name = "type")] public string Type;   // command: enable/disable/toggle/status/activate
        [DataMember(Name = "index")] public int Index;    // command: 1-based monitor index, 0 = all
        [DataMember(Name = "code")] public int Code;      // response: exit code
        [DataMember(Name = "text")] public string Text;   // response: human-readable result
    }

    internal static class PipeProtocol
    {
        public const int MaxCommandBytes = 4 * 1024;
        public const int MaxResponseBytes = 64 * 1024;

        public static void WriteMessage(PipeStream stream, PipeMessage message, DateTime deadlineUtc)
        {
            byte[] payload;
            var serializer = new DataContractJsonSerializer(typeof(PipeMessage));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, message);
                payload = ms.ToArray();
            }

            var frame = new byte[4 + payload.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, frame, 0, 4);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);

            var task = stream.WriteAsync(frame, 0, frame.Length);
            if (!WaitFor(task, deadlineUtc)) throw new TimeoutException("Pipe write timed out");
            stream.Flush();
        }

        public static PipeMessage ReadMessage(PipeStream stream, int maxPayloadBytes, DateTime deadlineUtc)
        {
            var lengthBuffer = ReadExactly(stream, 4, deadlineUtc);
            var length = BitConverter.ToInt32(lengthBuffer, 0);
            // Reject nonsense lengths (including <= 0) before allocating or reading anything -
            // a foreign/broken client on the same pipe name must not make the server buffer
            // arbitrary amounts of data.
            if (length <= 0 || length > maxPayloadBytes)
                throw new InvalidDataException("Invalid pipe frame length: " + length);

            var payload = ReadExactly(stream, length, deadlineUtc);
            var serializer = new DataContractJsonSerializer(typeof(PipeMessage));
            using (var ms = new MemoryStream(payload))
            {
                return (PipeMessage)serializer.ReadObject(ms);
            }
        }

        private static byte[] ReadExactly(PipeStream stream, int count, DateTime deadlineUtc)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var task = stream.ReadAsync(buffer, offset, count - offset);
                if (!WaitFor(task, deadlineUtc)) throw new TimeoutException("Pipe read timed out");
                var read = task.Result;
                if (read == 0) throw new EndOfStreamException("Pipe closed mid-message");
                offset += read;
            }

            return buffer;
        }

        private static bool WaitFor(System.Threading.Tasks.Task task, DateTime deadlineUtc)
        {
            var remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            try
            {
                return task.Wait(remaining);
            }
            catch (AggregateException e)
            {
                throw e.InnerException ?? e;
            }
        }
    }

    // Client side: runs in the secondary process, connects to the primary instance's pipe.
    public static class CliClient
    {
        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ExchangeBudget = TimeSpan.FromSeconds(5);

        public static CliCommandResult SendCommand(CliArguments args)
        {
            var type = args.Command.ToString().ToLowerInvariant();
            return Send(type, args.Index, activate: false);
        }

        public static CliCommandResult SendActivate()
        {
            return Send("activate", null, activate: true);
        }

        private static CliCommandResult Send(string type, int? index, bool activate)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", SingleInstance.PipeName,
                           PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    if (!ConnectWithRetry(pipe))
                    {
                        return new CliCommandResult(7,
                            "The running instance did not accept a connection (IPC timeout)");
                    }

                    var deadline = DateTime.UtcNow + ExchangeBudget;

                    var hello = PipeProtocol.ReadMessage(pipe, PipeProtocol.MaxResponseBytes, deadline);
                    if (hello == null || hello.Kind != "hello")
                    {
                        return new CliCommandResult(7, "Unexpected IPC handshake");
                    }

                    if (activate)
                    {
                        // Delegate this (freshly launched, foreground-permitted) process's right
                        // to set the foreground window to the server, which is about to call
                        // Activate() on our behalf. Best effort - Windows may still refuse.
                        AllowSetForegroundWindow(hello.Pid);
                    }

                    PipeProtocol.WriteMessage(pipe,
                        new PipeMessage { Kind = "command", Type = type, Index = index ?? 0 },
                        deadline);

                    var response = PipeProtocol.ReadMessage(pipe, PipeProtocol.MaxResponseBytes, deadline);
                    if (response == null || response.Kind != "response")
                    {
                        return new CliCommandResult(7, "Unexpected IPC response");
                    }

                    return new CliCommandResult(response.Code, response.Text);
                }
            }
            catch (UnauthorizedAccessException)
            {
                return new CliCommandResult(6,
                    "Access denied - the running instance may have different privileges (elevated vs not)");
            }
            catch (Exception e)
            {
                return new CliCommandResult(7, "IPC failure: " + e.Message);
            }
        }

        // Adaptive retry: the primary instance holds the mutex from early startup, but its
        // pipe server only comes up after MainWindow construction (EDID enumeration, NVAPI
        // calls) - that can take well over a few hundred milliseconds, so keep trying with a
        // growing interval until the overall connect budget runs out.
        private static bool ConnectWithRetry(NamedPipeClientStream pipe)
        {
            var deadline = DateTime.UtcNow + ConnectBudget;
            var delayMs = 50;
            while (true)
            {
                try
                {
                    pipe.Connect(200);
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (DateTime.UtcNow >= deadline) return false;
                    Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 400);
                }
            }
        }
    }

    // Server side: runs in the primary instance on a background thread, one connection at a
    // time, marshalling command execution onto the UI dispatcher.
    public static class CliServer
    {
        private static readonly TimeSpan PerConnectionBudget = TimeSpan.FromSeconds(5);

        private static Thread _thread;
        private static NamedPipeServerStream _current;
        private static volatile bool _stopping;

        private static MainViewModel _viewModel;
        private static Dispatcher _dispatcher;
        private static Action _restoreWindow;

        public static void Start(MainViewModel viewModel, Dispatcher dispatcher, Action restoreWindow)
        {
            _viewModel = viewModel;
            _dispatcher = dispatcher;
            _restoreWindow = restoreWindow;
            _stopping = false;

            _thread = new Thread(Loop) { IsBackground = true, Name = "novideo_srgb CLI pipe server" };
            _thread.Start();
        }

        // Called on shutdown from the UI thread. Disposing the current stream aborts a
        // blocking WaitForConnection/read; do NOT Join the server thread here - it may be
        // waiting on Dispatcher.Invoke, and joining from the UI thread would deadlock. The
        // thread is IsBackground and dies with the process, which covers the small window
        // where Stop() lands before _current has been assigned.
        public static void Stop()
        {
            _stopping = true;
            try
            {
                _current?.Dispose();
            }
            catch
            {
            }
        }

        private static void Loop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(SingleInstance.PipeName,
                        PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }
                catch (Exception e)
                {
                    if (_stopping) return;
                    // Creating the listener itself failed - most likely the previous process
                    // has not released the (single-instance) pipe name yet. Back off instead of
                    // spinning the CPU: without this the loop would retry with no delay at all.
                    Debug.WriteLine("CLI pipe server could not be created: " + e.Message);
                    Thread.Sleep(250);
                    continue;
                }

                try
                {
                    using (server)
                    {
                        _current = server;
                        if (_stopping) return;
                        server.WaitForConnection();
                        if (_stopping) return;
                        HandleConnection(server);
                    }
                }
                catch (Exception e)
                {
                    if (_stopping) return;
                    // A single bad connection (malformed frame, timeout, abrupt disconnect)
                    // must not kill the server - log at most and keep listening.
                    Debug.WriteLine("CLI pipe server connection error: " + e.Message);
                }
                finally
                {
                    _current = null;
                }
            }
        }

        private static void HandleConnection(NamedPipeServerStream server)
        {
            var deadline = DateTime.UtcNow + PerConnectionBudget;

            PipeProtocol.WriteMessage(server,
                new PipeMessage { Kind = "hello", Pid = Process.GetCurrentProcess().Id },
                deadline);

            var command = PipeProtocol.ReadMessage(server, PipeProtocol.MaxCommandBytes, deadline);
            if (command == null || command.Kind != "command") return;

            var response = Execute(command);
            PipeProtocol.WriteMessage(server,
                new PipeMessage { Kind = "response", Code = response.Code, Text = response.Text },
                deadline);

            // Give the client a bounded moment to drain the response before the using block
            // closes the pipe. WaitForPipeDrain() itself has no timeout: a client that
            // connects, sends a command and then never reads would otherwise block this
            // single-connection server thread forever, killing every later CLI command and
            // the "second launch activates the window" behavior until a restart.
            var drain = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    server.WaitForPipeDrain();
                }
                catch
                {
                    // Pipe already closed/broken - nothing to drain.
                }
            });
            drain.Wait(TimeSpan.FromSeconds(1));
        }

        private static CliCommandResult Execute(PipeMessage command)
        {
            int? index = command.Index > 0 ? command.Index : (int?)null;
            try
            {
                switch (command.Type)
                {
                    case "activate":
                        _dispatcher.Invoke(_restoreWindow);
                        return new CliCommandResult(0, "activated");
                    case "enable":
                        return _dispatcher.Invoke(() => _viewModel.EnableClamp(index));
                    case "disable":
                        return _dispatcher.Invoke(() => _viewModel.DisableClamp(index));
                    case "toggle":
                        return _dispatcher.Invoke(() => _viewModel.ToggleClamp(index));
                    case "status":
                        return _dispatcher.Invoke(() => _viewModel.GetStatus(index));
                    default:
                        return new CliCommandResult(2, "Unknown command: " + command.Type);
                }
            }
            catch (Exception e)
            {
                // Dispatcher shutdown or an unexpected failure while marshalling - report as an
                // internal error; the client-side timeout covers the cases where we cannot even
                // respond.
                return new CliCommandResult(5, "Failed to execute command: " + e.Message);
            }
        }
    }
}
