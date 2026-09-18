using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace Toolbar.Services;

/// <summary>
/// Diagnostics for UI freezes (reported during graphics-driver upgrades). A
/// background thread pings the UI dispatcher; if it stays unresponsive past
/// <see cref="HangThreshold"/>, a memory dump of the whole process is written so
/// the stuck thread's stack can be inspected afterwards — the process is usually
/// killed from Task Manager, so this must happen while it is still hung.
/// Output: %LOCALAPPDATA%\Toolbar\diagnostics (hang.log + *.dmp).
/// </summary>
public static class HangWatchdog
{
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(10);
    private const int MaxDumps = 3; // dumps are ~100 MB each

    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toolbar", "diagnostics");

    private static readonly object LogLock = new();

    public static void Start(Dispatcher ui)
    {
        AppContext.TryGetSwitch("Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport", out bool noStylus);
        Log($"Started v{typeof(HangWatchdog).Assembly.GetName().Version} pid {Environment.ProcessId} stylusDisabled={noStylus}");
        new Thread(() => Run(ui)) { IsBackground = true, Name = "HangWatchdog" }.Start();
    }

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "hang.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never take the app down */ }
    }

    private static void Run(Dispatcher ui)
    {
        long lastPong = Stopwatch.GetTimestamp();
        bool hung = false;

        while (!ui.HasShutdownStarted)
        {
            // Send is the highest priority, so a busy-but-alive UI thread still answers.
            ui.BeginInvoke(DispatcherPriority.Send, () => Interlocked.Exchange(ref lastPong, Stopwatch.GetTimestamp()));
            Thread.Sleep(1000);

            var silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastPong));
            if (!hung && silent >= HangThreshold)
            {
                hung = true;
                Log($"UI thread unresponsive for {silent.TotalSeconds:F0}s, writing dump");
                Log(WriteDump());
            }
            else if (hung && silent < TimeSpan.FromSeconds(2))
            {
                hung = false;
                Log("UI thread responsive again");
            }
        }
    }

    private static string WriteDump()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var old in new DirectoryInfo(Dir).GetFiles("*.dmp")
                         .OrderByDescending(f => f.CreationTimeUtc).Skip(MaxDumps - 1))
                old.Delete();

            var path = Path.Combine(Dir, $"hang-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var proc = Process.GetCurrentProcess();
            return MiniDumpWriteDump(proc.SafeHandle, proc.Id, fs.SafeFileHandle, DumpType,
                       IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                ? $"Dump written: {path}"
                : $"Dump failed: Win32 error {Marshal.GetLastPInvokeError()}";
        }
        catch (Exception ex)
        {
            return $"Dump failed: {ex.Message}";
        }
    }

    // Equivalent of `dotnet-dump collect --type Heap`: enough memory for managed
    // stacks and objects to be readable, without a full-memory dump's size.
    private const uint DumpType =
        0x00000001 | // WithDataSegs
        0x00000004 | // WithHandleData
        0x00000020 | // WithUnloadedModules
        0x00000200 | // WithPrivateReadWriteMemory
        0x00000800 | // WithFullMemoryInfo
        0x00001000 | // WithThreadInfo
        0x00010000;  // WithPrivateWriteCopyMemory

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(SafeProcessHandle hProcess, int processId,
        SafeFileHandle hFile, uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
