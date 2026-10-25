using Windows.Gaming.Input;
using Xunit;

public sealed class VirtualGamepadTests
{
    private static byte[] NeutralReport()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1] = 128;
        report[2] = 128;
        report[3] = 128;
        report[4] = 128;
        report[8] = 0x08; // D-pad released.
        return report;
    }

    [Fact]
    public void NeutralReportMapsToCenteredGamepad()
    {
        var info = VirtualGamepad.MapDualSenseReport(NeutralReport());

        Assert.Equal(GamepadButtons.None, info.Buttons);
        Assert.Equal(0.0, info.LeftThumbstickX, 3);
        Assert.Equal(0.0, info.LeftThumbstickY, 3);
        Assert.Equal(0.0, info.LeftTrigger, 3);
    }

    [Fact]
    public void FaceButtonsFollowXboxPositions()
    {
        var report = NeutralReport();
        report[8] = 0x08 | 0x10 | 0x20 | 0x40 | 0x80;

        var info = VirtualGamepad.MapDualSenseReport(report);

        Assert.Equal(
            GamepadButtons.X | GamepadButtons.A | GamepadButtons.B | GamepadButtons.Y,
            info.Buttons);
    }

    [Fact]
    public void DiagonalHatAndShoulderButtonsMap()
    {
        var report = NeutralReport();
        report[8] = 0x01; // Up-right.
        report[9] = 0x01 | 0x02 | 0x10 | 0x20;

        var info = VirtualGamepad.MapDualSenseReport(report);

        Assert.Equal(
            GamepadButtons.DPadUp | GamepadButtons.DPadRight
                | GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder
                | GamepadButtons.View | GamepadButtons.Menu,
            info.Buttons);
    }

    [Fact]
    public void StickUpIsPositiveAndTriggersScale()
    {
        var report = NeutralReport();
        report[2] = 0;
        report[6] = 255;

        var info = VirtualGamepad.MapDualSenseReport(report);

        Assert.Equal(1.0, info.LeftThumbstickY, 2);
        Assert.Equal(1.0, info.RightTrigger, 3);
    }
}
