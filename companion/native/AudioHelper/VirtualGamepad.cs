using System.ComponentModel;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using Windows.Gaming.Input;
using Windows.UI.Input.Preview.Injection;

// A temporary system gamepad created with Windows' built-in input injection
// (no driver required), fed from the bridge's DualSense HID input reports.
// The bridge keeps presenting its DualSense to the game the whole time; this
// only gives Game Bar an Xbox-style gamepad to navigate with.
sealed class VirtualGamepad : IDisposable
{
    private const int SonyVendorId = 0x054C;
    private static readonly int[] DualSenseProductIds = [0x0CE6, 0x0DF2];
    private const byte DualSenseUsbInputReportId = 0x01;
    private const int DualSenseUsbInputReportMinLength = 11;
    private const int MinGamepadInputReportLength = 64;
    private const uint GenericDesktopGamePadUsage = 0x00010005;
    private const uint GenericDesktopJoystickUsage = 0x00010004;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    private InputInjector? injector;
    private FileStream? stream;
    private CancellationTokenSource? readCancellation;
    private Thread? readerThread;
    private volatile bool running;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    public bool Connect(out string detail)
    {
        if (running)
        {
            detail = "already-connected";
            return true;
        }

        var candidates = FindBridgeDualSenseGamepads();
        if (candidates.Count == 0)
        {
            // Only the DualSense and DualSense Edge personas expose this
            // report layout. The DS4 persona falls back to a persona switch.
            detail = "no-bridge-dualsense-interface";
            return false;
        }

        FileStream? opened = null;
        HidDevice? device = null;
        var openErrors = new List<string>();
        foreach (var candidate in candidates)
        {
            if (TryOpenForRead(candidate, out opened, out var error))
            {
                device = candidate;
                break;
            }
            openErrors.Add(error);
        }
        if (opened is null || device is null)
        {
            detail = $"hid-open-failed {string.Join("; ", openErrors)}";
            return false;
        }

        try
        {
            injector = InputInjector.TryCreate();
            injector?.InitializeGamepadInjection();
        }
        catch (Exception error)
        {
            injector = null;
            opened.Dispose();
            detail = $"input-injection-failed {error.GetType().Name}: {error.Message}";
            return false;
        }
        if (injector is null)
        {
            opened.Dispose();
            detail = "input-injection-unavailable";
            return false;
        }

        stream = opened;
        readCancellation = new CancellationTokenSource();
        running = true;
        var reportLength = Math.Max(SafeMaxInputReportLength(device), MinGamepadInputReportLength);
        var token = readCancellation.Token;
        readerThread = new Thread(() => ReadLoop(opened, reportLength, token))
        {
            IsBackground = true,
            Name = "VirtualGamepadReader"
        };
        readerThread.Start();
        detail = $"pid=0x{device.ProductID:X4}";
        return true;
    }

    public void Disconnect()
    {
        if (!running && injector is null)
        {
            return;
        }

        running = false;
        readCancellation?.Cancel();
        try
        {
            stream?.Dispose();
        }
        catch
        {
        }
        stream = null;
        readerThread?.Join(1000);
        readerThread = null;
        readCancellation?.Dispose();
        readCancellation = null;
        ReleaseInjector();
    }

    public void Dispose()
    {
        Disconnect();
    }

    private void ReleaseInjector()
    {
        var current = injector;
        injector = null;
        if (current is null)
        {
            return;
        }
        try
        {
            current.InjectGamepadInput(new InjectedInputGamepadInfo());
            current.UninitializeGamepadInjection();
        }
        catch
        {
        }
    }

    private void ReadLoop(FileStream source, int reportLength, CancellationToken token)
    {
        var buffer = new byte[reportLength];
        while (running)
        {
            int count;
            try
            {
                count = source.ReadAsync(buffer, 0, buffer.Length, token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                if (running)
                {
                    Console.Error.WriteLine($"status: virtual-gamepad-input-stopped {error.GetType().Name}: {error.Message}");
                }
                break;
            }

            if (count < DualSenseUsbInputReportMinLength || buffer[0] != DualSenseUsbInputReportId)
            {
                continue;
            }

            try
            {
                injector?.InjectGamepadInput(MapDualSenseReport(buffer));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"status: virtual-gamepad-inject-failed {error.GetType().Name}: {error.Message}");
                break;
            }
        }
    }

    // USB DualSense input report 0x01: [1..4] sticks, [5..6] triggers,
    // [8] d-pad hat + face buttons, [9] shoulders/sticks/create/options.
    internal static InjectedInputGamepadInfo MapDualSenseReport(byte[] report)
    {
        var buttons = GamepadButtons.None;
        var face = report[8];
        var misc = report[9];

        if ((face & 0x10) != 0) buttons |= GamepadButtons.X;
        if ((face & 0x20) != 0) buttons |= GamepadButtons.A;
        if ((face & 0x40) != 0) buttons |= GamepadButtons.B;
        if ((face & 0x80) != 0) buttons |= GamepadButtons.Y;

        buttons |= (face & 0x0F) switch
        {
            0 => GamepadButtons.DPadUp,
            1 => GamepadButtons.DPadUp | GamepadButtons.DPadRight,
            2 => GamepadButtons.DPadRight,
            3 => GamepadButtons.DPadDown | GamepadButtons.DPadRight,
            4 => GamepadButtons.DPadDown,
            5 => GamepadButtons.DPadDown | GamepadButtons.DPadLeft,
            6 => GamepadButtons.DPadLeft,
            7 => GamepadButtons.DPadUp | GamepadButtons.DPadLeft,
            _ => GamepadButtons.None
        };

        if ((misc & 0x01) != 0) buttons |= GamepadButtons.LeftShoulder;
        if ((misc & 0x02) != 0) buttons |= GamepadButtons.RightShoulder;
        if ((misc & 0x10) != 0) buttons |= GamepadButtons.View;
        if ((misc & 0x20) != 0) buttons |= GamepadButtons.Menu;
        if ((misc & 0x40) != 0) buttons |= GamepadButtons.LeftThumbstick;
        if ((misc & 0x80) != 0) buttons |= GamepadButtons.RightThumbstick;

        return new InjectedInputGamepadInfo
        {
            Buttons = buttons,
            LeftThumbstickX = StickAxis(report[1]),
            LeftThumbstickY = -StickAxis(report[2]),
            RightThumbstickX = StickAxis(report[3]),
            RightThumbstickY = -StickAxis(report[4]),
            LeftTrigger = report[5] / 255.0,
            RightTrigger = report[6] / 255.0
        };
    }

    private static double StickAxis(byte value)
    {
        return Math.Clamp((value - 128) / 127.0, -1.0, 1.0);
    }

    // Opens the gamepad for reading only, sharing read and write, so a game,
    // Steam, or the companion holding the same interface does not block it.
    private static bool TryOpenForRead(HidDevice device, out FileStream? stream, out string error)
    {
        stream = null;
        var handle = CreateFile(
            device.DevicePath,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            error = $"win32={code} ({new Win32Exception(code).Message})";
            return false;
        }

        try
        {
            // bufferSize 0 passes reads straight to the device, as HID requires.
            stream = new FileStream(handle, FileAccess.Read, 0, isAsync: true);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            handle.Dispose();
            error = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static List<HidDevice> FindBridgeDualSenseGamepads()
    {
        var bridgeContainers = EndpointManager.TargetBridgeContainer is Guid target
            ? new HashSet<Guid> { target }
            : BridgeDeviceIdentity.GetBridgeContainerIds();

        var devices = new List<HidDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices(SonyVendorId))
        {
            if (!DualSenseProductIds.Contains(device.ProductID))
            {
                continue;
            }
            // The bridge also exposes a keyboard with the same IDs, which
            // Windows does not let other processes open, and a DualSense
            // connected to the PC directly shares the IDs too.
            if (!IsGamepadInterface(device))
            {
                continue;
            }
            if (BridgeDeviceIdentity.TryGetContainerIdForInterfacePath(device.DevicePath, out var container)
                && bridgeContainers.Contains(container))
            {
                devices.Add(device);
            }
        }
        return devices;
    }

    private static bool IsGamepadInterface(HidDevice device)
    {
        if (SafeMaxInputReportLength(device) < MinGamepadInputReportLength)
        {
            return false;
        }
        try
        {
            return device.GetReportDescriptor().DeviceItems.Any((item) => item.Usages.GetAllValues().Any(
                (usage) => usage == GenericDesktopGamePadUsage || usage == GenericDesktopJoystickUsage));
        }
        catch
        {
            // The report length already rules out the keyboard interface.
            return true;
        }
    }

    private static int SafeMaxInputReportLength(HidDevice device)
    {
        try
        {
            return device.GetMaxInputReportLength();
        }
        catch
        {
            return 0;
        }
    }
}
