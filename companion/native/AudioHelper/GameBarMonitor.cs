using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Foundation.Metadata;
using Windows.Gaming.UI;

// Reports whether the Xbox Game Bar overlay (including compact mode) is open.
// Game Bar only accepts Xbox-style gamepads for navigation. With
// --virtual-gamepad the monitor adds a temporary built-in Windows virtual
// gamepad while Game Bar is open, so the bridge's DualSense stays attached to
// the game. The companion falls back to switching the bridge persona when
// that is unavailable.
//
// Detection is event driven: Game Bar's own visibility events plus a
// foreground-window hook. Nothing is polled.
sealed class GameBarMonitor
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly string[] GameBarProcessNames =
    [
        "GameBar",
        "XboxGameBar",
        "GameBarFTServer",
        "GameBarElevatedFT_Alias"
    ];

    private readonly object sync = new();
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly VirtualGamepad? virtualPad;
    private bool apiAvailable;
    private bool? lastVisible;
    private string? lastApiError;
    private uint hookThreadId;
    // Kept in a field so the delegate outlives the native hook.
    private WinEventDelegate? foregroundHookProc;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public GameBarMonitor(bool virtualGamepad)
    {
        virtualPad = virtualGamepad ? new VirtualGamepad() : null;
    }

    public Task RunAsync()
    {
        Console.Error.WriteLine($"status: game-bar-monitor-started virtualGamepad={virtualPad is not null}");
        var inputThread = new Thread(ReadControlLoop)
        {
            IsBackground = true,
            Name = "GameBarMonitorControl"
        };
        inputThread.Start();

        SubscribeGameBarEvents();
        var hookThread = new Thread(RunForegroundHookLoop)
        {
            IsBackground = true,
            Name = "GameBarForegroundHook"
        };
        hookThread.Start();

        Evaluate("startup");
        stopped.Wait();

        UnsubscribeGameBarEvents();
        if (hookThreadId != 0)
        {
            PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
        virtualPad?.Dispose();
        Console.Error.WriteLine("status: game-bar-monitor-stopped");
        return Task.CompletedTask;
    }

    private void Evaluate(string trigger)
    {
        lock (sync)
        {
            if (stopped.IsSet)
            {
                return;
            }

            var visible = IsGameBarVisible(out var source);
            if (visible == lastVisible)
            {
                return;
            }
            lastVisible = visible;

            string? padState = null;
            string? padDetail = null;
            if (virtualPad is not null)
            {
                if (visible)
                {
                    padState = virtualPad.Connect(out padDetail) ? "connected" : "unavailable";
                }
                else
                {
                    virtualPad.Disconnect();
                    padState = "disconnected";
                }
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                visible,
                source,
                trigger,
                virtualPad = padState,
                virtualPadDetail = padDetail
            }, JsonOptions));
            Console.Out.Flush();
        }
    }

    private bool IsGameBarVisible(out string source)
    {
        if (TryReadGameBarApi(out var apiVisible) && apiVisible)
        {
            source = "api";
            return true;
        }

        // Game Bar takes the foreground while its overlay is open. Pinned
        // widgets stay visible after it closes but do not keep focus.
        if (ForegroundWindowBelongsToGameBar())
        {
            source = "foreground";
            return true;
        }

        source = "none";
        return false;
    }

    private void SubscribeGameBarEvents()
    {
        try
        {
            if (!ApiInformation.IsTypePresent("Windows.Gaming.UI.GameBar"))
            {
                Console.Error.WriteLine("status: game-bar-api-unavailable reason=type-not-present");
                return;
            }
            GameBar.VisibilityChanged += HandleGameBarEvent;
            GameBar.IsInputRedirectedChanged += HandleGameBarEvent;
            apiAvailable = true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"status: game-bar-api-unavailable {error.GetType().Name}: {error.Message}");
        }
    }

    private void UnsubscribeGameBarEvents()
    {
        if (!apiAvailable)
        {
            return;
        }
        try
        {
            GameBar.VisibilityChanged -= HandleGameBarEvent;
            GameBar.IsInputRedirectedChanged -= HandleGameBarEvent;
        }
        catch
        {
        }
    }

    private void HandleGameBarEvent(object? sender, object args)
    {
        Evaluate("api-event");
    }

    private bool TryReadGameBarApi(out bool visible)
    {
        visible = false;
        if (!apiAvailable)
        {
            return false;
        }

        try
        {
            visible = GameBar.Visible || GameBar.IsInputRedirected;
            lastApiError = null;
            return true;
        }
        catch (Exception error)
        {
            var message = $"{error.GetType().Name}: {error.Message}";
            if (message != lastApiError)
            {
                lastApiError = message;
                Console.Error.WriteLine($"status: game-bar-api-error {message}");
            }
            return false;
        }
    }

    private void RunForegroundHookLoop()
    {
        hookThreadId = GetCurrentThreadId();
        foregroundHookProc = (_, _, _, _, _, _, _) => Evaluate("foreground-event");
        // An out-of-context hook is delivered through this thread's message
        // queue, so the thread must pump messages for as long as it is set.
        var hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            foregroundHookProc,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        if (hook == IntPtr.Zero)
        {
            Console.Error.WriteLine("status: game-bar-foreground-hook-failed");
            return;
        }

        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnhookWinEvent(hook);
        }
    }

    private static bool ForegroundWindowBelongsToGameBar()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            return GameBarProcessNames.Any((candidate) => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void ReadControlLoop()
    {
        try
        {
            string? line;
            while (!stopped.IsSet && (line = Console.In.ReadLine()) is not null)
            {
                if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }
        catch
        {
        }
        stopped.Set();
    }
}
