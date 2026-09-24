using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using AnyPC.Core.Abstractions;
using static AnyPC.Windows.Native;

namespace AnyPC.Windows.Platform;

/// <summary>
/// Captures a monitor with GDI BitBlt, draws the mouse cursor, scales it down and encodes JPEG.
/// Frames whose pixels did not change since the previous capture are skipped.
/// </summary>
internal sealed class GdiScreenSource : IScreenSource, IDisposable
{
    readonly object _lock = new();
    readonly ImageCodecInfo _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    readonly MemoryStream _encodeBuffer = new(512 * 1024);
    Bitmap? _full;
    Bitmap? _scaled;
    ulong _lastHash;
    int _lastQuality;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var list = new List<MonitorInfo>(screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var name = $"Display {i + 1}" + (s.Primary ? " (main)" : "");
            list.Add(new MonitorInfo(i, name, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary));
        }
        return list;
    }

    public EncodedFrame? Capture(int monitor, int maxWidth, int quality, bool force)
    {
        lock (_lock)
        {
            var monitors = GetMonitors();
            if (monitors.Count == 0) return null;
            var m = monitors[Math.Clamp(monitor, 0, monitors.Count - 1)];

            _full = Ensure(_full, m.Width, m.Height);
            int cursorX = -1, cursorY = -1;
            using (var g = Graphics.FromImage(_full))
            {
                var hdcDest = g.GetHdc();
                var hdcSrc = GetDC(IntPtr.Zero);
                try
                {
                    // Fails on the secure desktop (lock screen, UAC prompt).
                    if (!BitBlt(hdcDest, 0, 0, m.Width, m.Height, hdcSrc, m.X, m.Y, SRCCOPY)) return null;
                    DrawCursor(hdcDest, m, out cursorX, out cursorY);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, hdcSrc);
                    g.ReleaseHdc(hdcDest);
                }
            }

            Bitmap output = _full;
            double scale = 1;
            if (m.Width > maxWidth)
            {
                scale = (double)maxWidth / m.Width;
                int tw = maxWidth & ~1, th = Math.Max(2, (int)Math.Round(m.Height * scale) & ~1);
                _scaled = Ensure(_scaled, tw, th);
                using var g = Graphics.FromImage(_scaled);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                g.DrawImage(_full, new Rectangle(0, 0, tw, th), 0, 0, m.Width, m.Height, GraphicsUnit.Pixel);
                output = _scaled;
            }

            var hash = Hash(output);
            if (!force && hash == _lastHash && quality == _lastQuality) return null;
            _lastHash = hash;
            _lastQuality = quality;

            _encodeBuffer.SetLength(0);
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                output.Save(_encodeBuffer, _jpegCodec, ep);
            }

            return new EncodedFrame(_encodeBuffer.ToArray(), output.Width, output.Height,
                cursorX < 0 ? -1 : (int)(cursorX * scale), cursorY < 0 ? -1 : (int)(cursorY * scale));
        }
    }

    static Bitmap Ensure(Bitmap? bmp, int w, int h)
    {
        if (bmp is not null && bmp.Width == w && bmp.Height == h) return bmp;
        bmp?.Dispose();
        return new Bitmap(w, h, PixelFormat.Format32bppRgb);
    }

    static void DrawCursor(IntPtr hdc, MonitorInfo m, out int x, out int y)
    {
        x = y = -1;
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0) return;
        int rx = ci.ptScreenPos.X - m.X, ry = ci.ptScreenPos.Y - m.Y;
        if (rx < 0 || ry < 0 || rx >= m.Width || ry >= m.Height) return;
        x = rx;
        y = ry;
        if (!GetIconInfo(ci.hCursor, out var ii)) return;
        try
        {
            DrawIconEx(hdc, rx - ii.xHotspot, ry - ii.yHotspot, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    static unsafe ulong Hash(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var span = new ReadOnlySpan<byte>((void*)data.Scan0, Math.Abs(data.Stride) * data.Height);
            return XxHash3.HashToUInt64(span);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _full?.Dispose();
        _scaled?.Dispose();
        _encodeBuffer.Dispose();
    }
}
