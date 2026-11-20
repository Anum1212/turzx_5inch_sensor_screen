using System.Drawing.Imaging;
using System.Globalization;

namespace TurzxPanel;

/// <summary>Finds TURZX 5" screens (USB 1D6B:0106, shown as a COM port).</summary>
public sealed class TurzxFactory : IScreenDriverFactory
{
    public string Brand => "TURZX";
    public string? FindPort() => TurzxDevice.FindPort();
    public bool PortExists(string port) => TurzxDevice.PortExists(port);
    public IScreenDriver Create() => new TurzxDriver();
}

/// <summary>
/// TURZX 5" (800x480). The screen has no compressed picture mode that works over this link, so every picture is a
/// full raw BGRA frame (1.5 MB, roughly 1 s on the wire). That suits a dashboard that changes about once a second.
/// The wire protocol itself is in <see cref="TurzxDevice"/>; this class adapts it to <see cref="IScreenDriver"/>.
/// </summary>
public sealed class TurzxDriver : IScreenDriver
{
    const int ScreenWidth = 800, ScreenHeight = 480;

    readonly TurzxDevice _device = new();
    byte[]? _stream;      // the latest picture, already cut into packets
    bool _pending;        // true until that picture has been written once

    public ScreenInfo Open(string port)
    {
        var report = _device.Open(port);
        // The backlight command is accepted while pictures stream, so no software dimming is needed.
        return new ScreenInfo(
            "TURZX " + report.Model, report.RomVersion.ToString("0.00", CultureInfo.InvariantCulture),
            ScreenWidth, ScreenHeight, SupportsBrightness: true, SupportsRestart: true, DimInSoftware: false);
    }

    public void BeginStreaming(int brightness) => _device.SetBrightness(brightness);

    public void SetFrame(Bitmap frame, bool flip180)
    {
        // The panel is wired landscape. A vertical theme (480x800) is turned 90 degrees; the flip option reverses that.
        bool landscape = frame.Width == ScreenWidth && frame.Height == ScreenHeight;
        var turn = landscape
            ? (flip180 ? RotateFlipType.Rotate180FlipNone : RotateFlipType.RotateNoneFlipNone)
            : (flip180 ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
        if (turn != RotateFlipType.RotateNoneFlipNone) frame.RotateFlip(turn);

        _stream = TurzxDevice.BuildImageStream(ToBgra32(frame));
        _pending = true;
    }

    public void Send()
    {
        // A picture takes most of a second to send and the screen keeps showing it, so each one is sent once.
        if (!_pending || _stream == null) return;
        _pending = false;
        _device.WriteStream(_stream);
        _device.WaitForReply();
    }

    public void SetBrightness(int percent) => _device.SetBrightness(percent);

    public void Restart() => _device.SendCommand(TurzxDevice.CmdReboot);

    public void Dispose() => _device.Dispose();

    /// <summary>Raw 32-bit pixels (blue, green, red, alpha=255), row by row. The screen reads exactly 4 bytes per pixel.</summary>
    static byte[] ToBgra32(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            var dst = new byte[rowBytes * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, dst, y * rowBytes, rowBytes);
            return dst;
        }
        finally { bmp.UnlockBits(data); }
    }
}
