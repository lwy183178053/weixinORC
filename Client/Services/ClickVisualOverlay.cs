using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Client.Services
{
    /// <summary>
    /// Lightweight click-through Win32 layered overlay that flashes a crisp pulsing target reticle
    /// at every automated screen click coordinate (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE).
    /// Completely transparent to WindowFromPoint &amp; SendInput, never steals focus, and works across multi-monitor setups.
    /// </summary>
    internal static class ClickVisualOverlay
    {
        private const int OverlaySize = 128;
        private const int HalfSize = OverlaySize / 2;
        private const int AnimationMs = 850;
        private const int FrameIntervalMs = 28;

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;
        private const uint PM_REMOVE = 0x0001;
        private const string WindowClassName = "WeixinOrcClickOverlayClass";

        private sealed record FlashRequest(int ScreenX, int ScreenY, bool IsWarning, long TimestampMs);

        private static readonly ConcurrentQueue<FlashRequest> Queue = new();
        private static readonly AutoResetEvent WakeSignal = new(false);
        private static readonly object StartLock = new();
        private static bool _started;

        internal static bool IsEnabled { get; set; } = true;

        internal static void FlashAt(int screenX, int screenY, bool isWarning = false)
        {
            if (!IsEnabled) return;
            EnsureThreadStarted();
            Queue.Enqueue(new FlashRequest(screenX, screenY, isWarning, Environment.TickCount64));
            WakeSignal.Set();
        }

        private static void EnsureThreadStarted()
        {
            if (_started) return;
            lock (StartLock)
            {
                if (_started) return;
                _started = true;
                var t = new Thread(OverlayLoop)
                {
                    IsBackground = true,
                    Name = "ClickVisualOverlayThread"
                };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
        }

        private static void OverlayLoop()
        {
            nint hwnd = CreateOverlayWindow();
            if (hwnd == nint.Zero) return;

            byte[] pixels = new byte[OverlaySize * OverlaySize * 4];
            FlashRequest? active = null;

            while (true)
            {
                while (Queue.TryDequeue(out var next))
                {
                    active = next;
                }

                if (active is null)
                {
                    _ = ShowWindow(hwnd, SW_HIDE);
                    WakeSignal.WaitOne(500);
                    continue;
                }

                long elapsed = Environment.TickCount64 - active.TimestampMs;
                if (elapsed >= AnimationMs)
                {
                    active = null;
                    _ = ShowWindow(hwnd, SW_HIDE);
                    continue;
                }

                double progress = Math.Clamp(elapsed / (double)AnimationMs, 0.0, 1.0);
                RenderReticleFrame(pixels, progress, active.IsWarning);
                PresentLayeredBitmap(hwnd, active.ScreenX - HalfSize, active.ScreenY - HalfSize, pixels);
                _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);

                PumpMessages();
                WakeSignal.WaitOne(FrameIntervalMs);
            }
        }

        private static void PumpMessages()
        {
            while (PeekMessage(out var msg, nint.Zero, 0, 0, PM_REMOVE))
            {
                _ = TranslateMessage(ref msg);
                _ = DispatchMessage(ref msg);
            }
        }

        private static void RenderReticleFrame(byte[] bgra, double t, bool isWarning)
        {
            Array.Clear(bgra, 0, bgra.Length);

            // Vibrant crimson red when blocked/warning, bright emerald-teal for normal clicks
            byte colR = isWarning ? (byte)239 : (byte)16;
            byte colG = isWarning ? (byte)68 : (byte)185;
            byte colB = isWarning ? (byte)68 : (byte)129;

            double fade = t < 0.2 ? 1.0 : Math.Pow(1.0 - (t - 0.2) / 0.8, 1.35);
            double ringR = 14.0 + t * 38.0; // expanding ripple radius
            double innerR = 12.0;           // crisp steady target ring
            double cx = HalfSize - 0.5;
            double cy = HalfSize - 0.5;

            for (int y = 0; y < OverlaySize; y++)
            {
                double dy = y - cy;
                int rowOff = y * OverlaySize * 4;
                for (int x = 0; x < OverlaySize; x++)
                {
                    double dx = x - cx;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    double alpha = 0;

                    // 1. Expanding outer ripple ring
                    double dRing = Math.Abs(dist - ringR);
                    if (dRing < 3.0)
                    {
                        double a = (1.0 - dRing / 3.0) * 0.92 * fade;
                        if (a > alpha) alpha = a;
                    }

                    // 2. Soft translucent fill inside expanding ring
                    if (dist < ringR && dist > 6.0)
                    {
                        double a = 0.14 * fade;
                        if (a > alpha) alpha = a;
                    }

                    // 3. Steady inner target circle
                    double dInner = Math.Abs(dist - innerR);
                    if (dInner < 2.0)
                    {
                        double a = (1.0 - dInner / 2.0) * 0.95 * fade;
                        if (a > alpha) alpha = a;
                    }

                    // 4. Precision crosshair ticks (length 22px, gap 5px around center)
                    if ((Math.Abs(dx) < 1.2 && Math.Abs(dy) >= 5.0 && Math.Abs(dy) <= 22.0) ||
                        (Math.Abs(dy) < 1.2 && Math.Abs(dx) >= 5.0 && Math.Abs(dx) <= 22.0))
                    {
                        double a = 0.92 * fade;
                        if (a > alpha) alpha = a;
                    }

                    // 5. Crisp center dot (3.5px radius)
                    if (dist <= 3.8)
                    {
                        double a = Math.Min(1.0, (3.8 - dist) * 0.9) * fade;
                        if (a > alpha) alpha = a;
                    }

                    if (alpha <= 0.005) continue;
                    byte aByte = (byte)Math.Clamp((int)Math.Round(alpha * 255.0), 0, 255);
                    // Premultiplied BGRA required by UpdateLayeredWindow (AC_SRC_ALPHA)
                    int idx = rowOff + x * 4;
                    bgra[idx] = (byte)((colB * aByte) / 255);
                    bgra[idx + 1] = (byte)((colG * aByte) / 255);
                    bgra[idx + 2] = (byte)((colR * aByte) / 255);
                    bgra[idx + 3] = aByte;
                }
            }
        }

        private static void PresentLayeredBitmap(nint hwnd, int x, int y, byte[] bgraPixels)
        {
            nint screenDc = GetDC(nint.Zero);
            if (screenDc == nint.Zero) return;
            nint memDc = nint.Zero;
            nint hBmp = nint.Zero;
            nint oldBmp = nint.Zero;
            try
            {
                memDc = CreateCompatibleDC(screenDc);
                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = OverlaySize,
                        biHeight = -OverlaySize,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB
                    }
                };
                hBmp = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out nint bits, nint.Zero, 0);
                if (hBmp == nint.Zero || bits == nint.Zero) return;
                Marshal.Copy(bgraPixels, 0, bits, bgraPixels.Length);
                oldBmp = SelectObject(memDc, hBmp);

                var ptDst = new POINT { X = x, Y = y };
                var sz = new SIZE { cx = OverlaySize, cy = OverlaySize };
                var ptSrc = new POINT { X = 0, Y = 0 };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                _ = UpdateLayeredWindow(hwnd, screenDc, ref ptDst, ref sz, memDc, ref ptSrc, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBmp != nint.Zero && memDc != nint.Zero) _ = SelectObject(memDc, oldBmp);
                if (hBmp != nint.Zero) _ = DeleteObject(hBmp);
                if (memDc != nint.Zero) _ = DeleteDC(memDc);
                _ = ReleaseDC(nint.Zero, screenDc);
            }
        }

        private static readonly WndProcDelegate KeepAliveWndProc = DefWindowProc;

        private static nint CreateOverlayWindow()
        {
            nint hInst = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(KeepAliveWndProc),
                hInstance = hInst,
                lpszClassName = WindowClassName
            };
            _ = RegisterClassEx(ref wc);

            return CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                WindowClassName,
                string.Empty,
                WS_POPUP,
                0, 0, OverlaySize, OverlaySize,
                nint.Zero, nint.Zero, hInst, nint.Zero);
        }

        private delegate nint WndProcDelegate(nint hWnd, uint msg, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public nint hwnd;
            public uint message;
            public nuint wParam;
            public nint lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public nint hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [DllImport("user32.dll")]
        private static extern nint DefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(
            nint hWnd, nint hdcDst, ref POINT pptDst, ref SIZE psize,
            nint hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(nint hWnd, nint hDC);

        [DllImport("gdi32.dll")]
        private static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll")]
        private static extern nint SelectObject(nint hdc, nint h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(nint ho);

        [DllImport("gdi32.dll")]
        private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);
    }
}
