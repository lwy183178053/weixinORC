using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    internal static class Win32WindowHelper
    {
        internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
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

        internal const int SW_HIDE = 0;
        internal const int SW_SHOW = 5;
        internal const int SW_MINIMIZE = 6;
        internal const int SW_RESTORE = 9;

        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const uint BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;
        private const int SRCCOPY = 0x00CC0020;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR = 0x00000008;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public nint hwndOwner;
            public nint hInstance;
            public string lpstrFilter;
            public string? lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public nint lpstrFile;
            public int nMaxFile;
            public nint lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public nint lCustData;
            public nint lpfnHook;
            public string? lpTemplateName;
            public nint pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileName(ref OPENFILENAME lpofn);

        internal static string? PickImageFilePath(nint ownerHwnd)
        {
            const int maxChars = 4096;
            nint fileBuffer = Marshal.AllocHGlobal(maxChars * sizeof(char));
            try
            {
                // Zero-initialize first character so GetOpenFileName starts with an empty filename.
                Marshal.WriteInt16(fileBuffer, 0);
                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                    hwndOwner = ownerHwnd,
                    lpstrFilter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp)\0*.png;*.jpg;*.jpeg;*.bmp\0所有文件 (*.*)\0*.*\0\0",
                    nFilterIndex = 1,
                    lpstrFile = fileBuffer,
                    nMaxFile = maxChars,
                    lpstrTitle = "选择用于 OCR 测试的截图或图片",
                    Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                };

                return GetOpenFileName(ref ofn) ? Marshal.PtrToStringUni(fileBuffer) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuffer);
            }
        }

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(nint hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

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
        private static extern nint CreateDIBSection(
            nint hdc,
            ref BITMAPINFO pbmi,
            uint usage,
            out nint ppvBits,
            nint hSection,
            uint offset);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            nint hdc,
            int x,
            int y,
            int cx,
            int cy,
            nint hdcSrc,
            int x1,
            int y1,
            int rop);

        internal static string GetWindowTitleText(nint hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(length + 1);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        internal static string GetWindowClassName(nint hWnd)
        {
            var sb = new StringBuilder(256);
            int len = GetClassName(hWnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString(0, len) : string.Empty;
        }

        internal static SoftwareBitmap CaptureWindowToSoftwareBitmap(nint hWnd)
        {
            if (!IsWindow(hWnd))
            {
                throw new InvalidOperationException("目标窗口句柄已失效，请刷新窗口列表。");
            }

            if (IsIconic(hWnd))
            {
                throw new InvalidOperationException("目标窗口当前处于最小化状态，请先点击“显示窗口”后再执行截图识别。");
            }

            if (!GetWindowRect(hWnd, out RECT rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                throw new InvalidOperationException("无法获取目标窗口的有效尺寸，请确认窗口已正常显示。");
            }

            int width = rect.Width;
            int height = rect.Height;

            nint screenDc = GetDC(nint.Zero);
            if (screenDc == nint.Zero)
            {
                throw new InvalidOperationException("无法获取屏幕设备上下文 (DC)。");
            }

            nint memDc = nint.Zero;
            nint dib = nint.Zero;
            nint oldObj = nint.Zero;

            try
            {
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == nint.Zero)
                {
                    throw new InvalidOperationException("无法创建兼容设备上下文。");
                }

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // Top-down DIB
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB
                    }
                };

                dib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out nint bitsPtr, nint.Zero, 0);
                if (dib == nint.Zero || bitsPtr == nint.Zero)
                {
                    throw new InvalidOperationException("无法分配窗口截图位图缓冲。");
                }

                oldObj = SelectObject(memDc, dib);

                bool captured = PrintWindow(hWnd, memDc, PW_RENDERFULLCONTENT);
                int byteCount = checked(width * height * 4);
                byte[] pixels = new byte[byteCount];

                if (captured)
                {
                    Marshal.Copy(bitsPtr, pixels, 0, byteCount);
                }

                // Fallback to screen BitBlt if PrintWindow failed or returned an all-zero buffer.
                if (!captured || IsAllZero(pixels))
                {
                    _ = BitBlt(memDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SRCCOPY);
                    Marshal.Copy(bitsPtr, pixels, 0, byteCount);
                }

                // Ensure opaque alpha so WinUI preview and OCR receive clean BGRA8 pixels.
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }

                return SoftwareBitmap.CreateCopyFromBuffer(
                    pixels.AsBuffer(),
                    BitmapPixelFormat.Bgra8,
                    width,
                    height,
                    BitmapAlphaMode.Premultiplied);
            }
            finally
            {
                if (oldObj != nint.Zero && memDc != nint.Zero)
                {
                    _ = SelectObject(memDc, oldObj);
                }

                if (dib != nint.Zero)
                {
                    _ = DeleteObject(dib);
                }

                if (memDc != nint.Zero)
                {
                    _ = DeleteDC(memDc);
                }

                _ = ReleaseDC(nint.Zero, screenDc);
            }
        }

        private static bool IsAllZero(byte[] data)
        {
            // Sample across the image to avoid scanning every byte when non-empty.
            int step = Math.Max(4, data.Length / 4096);
            for (int i = 0; i < data.Length; i += step)
            {
                if (data[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
