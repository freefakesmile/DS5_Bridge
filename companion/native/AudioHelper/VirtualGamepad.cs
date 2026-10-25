using HidSharp;
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
    private const int ReadTimeoutMilliseconds = 500;

    private InputInjector? injector;
    private HidStream? stream;
    private Thread? readerThread;
    private volatile bool running;

    public bool Connect(out string detail)
    {
        if (running)
        {
            detail = "already-connected";
            return true;
        }

        var device = FindBridgeDualSense();
        if (device is null)
        {
            // Only the DualSense and DualSense Edge personas expose this
            // report layout. The DS4 persona falls back to a persona switch.
            detail = "no-bridge-dualsense-interface";
            return false;
        }

        try
        {
            injector = InputInjector.TryCreate();
            if (injector is null)
            {
                detail = "input-injection-unavailable";
                return false;
            }
            injector.InitializeGamepadInjection();
        }
        catch (Exception error)
        {
            injector = null;
            detail = $"input-injection-failed {error.GetType().Name}: {error.Message}";
            return false;
        }

        if (!device.TryOpen(out var opened))
        {
            ReleaseInjector();
            detail = "hid-open-failed";
            return false;
        }

        stream = opened;
        stream.ReadTimeout = ReadTimeoutMilliseconds;
        running = true;
        var reportLength = Math.Max(device.GetMaxInputReportLength(), 64);
        readerThread = new Thread(() => ReadLoop(opened, reportLength))
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

    private void ReadLoop(HidStream source, int reportLength)
    {
        var buffer = new byte[reportLength];
        while (running)
        {
            int count;
            try
            {
                count = source.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
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

    private static HidDevice? FindBridgeDualSense()
    {
        var bridgeContainers = EndpointManager.TargetBridgeContainer is Guid target
            ? new HashSet<Guid> { target }
            : BridgeDeviceIdentity.GetBridgeContainerIds();

        foreach (var device in DeviceList.Local.GetHidDevices(SonyVendorId))
        {
            if (!DualSenseProductIds.Contains(device.ProductID))
            {
                continue;
            }
            // Only the bridge's own interface: a DualSense connected to the PC
            // directly shares the same IDs.
            if (BridgeDeviceIdentity.TryGetContainerIdForInterfacePath(device.DevicePath, out var container)
                && bridgeContainers.Contains(container))
            {
                return device;
            }
        }
        return null;
    }
}
