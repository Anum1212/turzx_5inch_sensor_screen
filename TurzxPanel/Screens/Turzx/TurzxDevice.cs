using System.IO.Ports;
using System.Text;
using Microsoft.Win32;

namespace TurzxPanel;

/// <summary>What the screen reports for command 0x01, e.g. "chs_5inch.dev1_rom1.88".</summary>
public sealed record TurzxReport(string Raw, string Model, double RomVersion);

/// <summary>
/// TURZX 5" screen: USB CDC serial (VID 1D6B, PID 0106), 800x480.
/// Every command is one fixed 250-byte packet:
///   [0] command  [1..2] EF 69  [3..6] length (big endian)  [7] flag  [8..9] 0  [10..] payload
/// A picture is command 200 (length = byte count) followed by the raw pixels cut into 249-byte pieces,
/// each padded to 250 bytes. Pixels are BGRA, exactly 4 bytes each (alpha 255), row by row: 800 x 480 x 4 = 1,536,000 bytes.
/// The screen answers "full_png_sucess" when it has taken a picture.
/// </summary>
public sealed class TurzxDevice : IDisposable
{
    public const int PacketSize = 250;
    public const int PieceSize = 249;

    public const byte CmdInfo = 1;
    public const byte CmdBrightness = 123;
    public const byte CmdReboot = 132;
    public const byte CmdFullImage = 200;

    const string UsbId = "VID_1D6B&PID_0106";

    readonly object _writeLock = new();
    SerialPort? _port;

    public static byte[] BuildPacket(byte cmd, int length, byte[]? payload = null)
    {
        var p = new byte[PacketSize];
        p[0] = cmd;
        p[1] = 0xEF;
        p[2] = 0x69;
        p[3] = (byte)(length >> 24);
        p[4] = (byte)(length >> 16);
        p[5] = (byte)(length >> 8);
        p[6] = (byte)length;
        if (payload != null) Array.Copy(payload, 0, p, 10, Math.Min(payload.Length, PacketSize - 10));
        return p;
    }

    /// <summary>Cuts picture bytes into padded 250-byte packets, preceded by the "full image" command packet.</summary>
    public static byte[] BuildImageStream(byte[] pixels)
    {
        int pieces = (pixels.Length + PieceSize - 1) / PieceSize;
        var stream = new byte[PacketSize * (pieces + 1)];
        Array.Copy(BuildPacket(CmdFullImage, pixels.Length), stream, PacketSize);
        for (int i = 0; i < pieces; i++)
        {
            int src = i * PieceSize;
            Array.Copy(pixels, src, stream, PacketSize * (i + 1), Math.Min(PieceSize, pixels.Length - src));
        }
        return stream;
    }

    /// <summary>Finds the COM port of a connected screen via the USB enumeration registry.</summary>
    public static string? FindPort()
    {
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb == null) return null;
        foreach (string dev in usb.GetSubKeyNames())
        {
            if (!dev.StartsWith(UsbId, StringComparison.OrdinalIgnoreCase)) continue;
            using var devKey = usb.OpenSubKey(dev);
            if (devKey == null) continue;
            foreach (string inst in devKey.GetSubKeyNames())
            {
                using var parms = devKey.OpenSubKey(inst + @"\Device Parameters");
                if (parms?.GetValue("PortName") is string name && present.Contains(name))
                    return name;
            }
        }
        return null;
    }

    public static bool PortExists(string name) =>
        SerialPort.GetPortNames().Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens the port and asks the screen who it is. Throws if it does not answer.</summary>
    public TurzxReport Open(string portName)
    {
        // Baud rate is ignored by USB CDC; DTR and RTS must be on.
        var sp = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 200,
            WriteTimeout = 5000,
            DtrEnable = true,
            RtsEnable = true,
        };
        sp.Open();
        _port = sp;
        Thread.Sleep(300);
        try { sp.DiscardInBuffer(); } catch { }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            WriteRaw(BuildPacket(CmdInfo, 1, new byte[] { 0xC5, 0xD3 }));
            string text = ReadText(1000);
            var report = TryParse(text);
            if (report != null) return report;
        }
        throw new IOException("Screen did not answer the info request");
    }

    static TurzxReport? TryParse(string text)
    {
        text = text.Trim('\0', ' ', '\r', '\n');
        if (!text.Contains("rom", StringComparison.OrdinalIgnoreCase) && !text.Contains("inch", StringComparison.OrdinalIgnoreCase))
            return null;
        string model = text.Split('.')[0];
        double rom = 0;
        int i = text.IndexOf("rom", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
            double.TryParse(text[(i + 3)..].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out rom);
        return new TurzxReport(text, model, rom);
    }

    string ReadText(int waitMs)
    {
        var sp = _port!;
        var sb = new StringBuilder();
        var buf = new byte[1024];
        long end = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < end)
        {
            Thread.Sleep(50);
            int n;
            try { n = sp.BytesToRead; } catch { break; }
            if (n <= 0) { if (sb.Length > 0) break; continue; }
            int read = sp.Read(buf, 0, Math.Min(n, buf.Length));
            sb.Append(Encoding.UTF8.GetString(buf, 0, read));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Waits for the screen's reply to a picture ("full_png_sucess"), then clears it. The screen needs time to show a
    /// picture; data sent before it has answered is mixed into the next one and the image comes out torn and tiled.
    /// </summary>
    public void WaitForReply(int timeoutMs = 3000)
    {
        var sp = _port;
        if (sp == null) return;
        long end = Environment.TickCount64 + timeoutMs;
        try
        {
            while (Environment.TickCount64 < end && sp.BytesToRead == 0) Thread.Sleep(5);
            Thread.Sleep(20); // let the rest of the reply arrive
            sp.DiscardInBuffer();
        }
        catch { }
    }

    public void SendCommand(byte cmd, int length = 1, byte[]? payload = null) =>
        WriteRaw(BuildPacket(cmd, length, payload));

    public void SetBrightness(int percent) =>
        SendCommand(CmdBrightness, 1, new[] { (byte)Math.Clamp(percent, 0, 100) });

    public void WriteRaw(byte[] data) => WriteRaw(data, 0, data.Length);

    public void WriteRaw(byte[] data, int offset, int count)
    {
        var sp = _port ?? throw new InvalidOperationException("Port is not open");
        lock (_writeLock) sp.Write(data, offset, count);
    }

    /// <summary>Writes a prepared packet stream, pausing 1 ms after every 100 packets like the vendor app does.</summary>
    public void WriteStream(byte[] stream)
    {
        // One write per packet, exactly like the vendor app. Pushing bigger blocks makes the screen lose bytes
        // (the picture comes out torn and repeated).
        int packets = stream.Length / PacketSize;
        for (int i = 0; i < packets; i++)
        {
            WriteRaw(stream, i * PacketSize, PacketSize);
            if ((i + 1) % 100 == 0) Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        var sp = _port;
        _port = null;
        if (sp == null) return;
        // Closing a USB serial port can block for ever when the screen was just unplugged or rebooted.
        // Do it on another thread and stop waiting after a few seconds.
        var closer = Task.Run(() =>
        {
            try
            {
                if (sp.IsOpen)
                {
                    long end = Environment.TickCount64 + 1500;
                    while (sp.BytesToWrite > 0 && Environment.TickCount64 < end) Thread.Sleep(10);
                    sp.Close();
                }
            }
            catch { }
            try { sp.Dispose(); } catch { }
        });
        if (!closer.Wait(4000)) Log.Write("Closing the serial port took too long; continuing without waiting");
    }
}
