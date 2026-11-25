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

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public nuint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public nuint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        internal const int SW_SHOW = 5;
        internal const int SW_MINIMIZE = 6;
        internal const int SW_RESTORE = 9;

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        internal const ushort VK_BACK = 0x08;
        internal const ushort VK_RETURN = 0x0D;
        internal const ushort VK_CONTROL = 0x11;
        internal const ushort VK_ESCAPE = 0x1B;
        internal const ushort VK_A = 0x41;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int WHEEL_DELTA = 120;
        private const uint WM_CLOSE = 0x0010;

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

        internal static string? PickImageFilePath(nint ownerHwnd) =>
            PickFilePath(
                ownerHwnd,
                "图片文件 (*.png;*.jpg;*.jpeg;*.bmp)\0*.png;*.jpg;*.jpeg;*.bmp\0所有文件 (*.*)\0*.*\0\0",
                "选择需要识别的图片");

        internal static string? PickExecutableFilePath(nint ownerHwnd) =>
            PickFilePath(
                ownerHwnd,
                "微信程序 (Weixin.exe;WeChat.exe)\0Weixin.exe;WeChat.exe\0可执行文件 (*.exe)\0*.exe\0\0",
                "选择微信主程序 (Weixin.exe / WeChat.exe)");

        private static string? PickFilePath(nint ownerHwnd, string filter, string title)
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
                    lpstrFilter = filter,
                    nFilterIndex = 1,
                    lpstrFile = fileBuffer,
                    nMaxFile = maxChars,
                    lpstrTitle = title,
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
        internal static extern uint GetDpiForWindow(nint hwnd);

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
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        internal static bool ForceBringToForeground(nint hWnd)
        {
            if (!IsWindow(hWnd)) return false;

            if (IsIconic(hWnd))
            {
                _ = ShowWindow(hWnd, SW_RESTORE);
            }
            else if (!IsWindowVisible(hWnd))
            {
                _ = ShowWindow(hWnd, SW_SHOW);
                _ = ShowWindow(hWnd, SW_RESTORE);
            }

            nint fg = GetForegroundWindow();
            uint curThread = GetCurrentThreadId();
            uint fgThread = fg != nint.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            uint targetThread = GetWindowThreadProcessId(hWnd, out _);

            bool attachedFg = false;
            bool attachedTarget = false;
            try
            {
                if (fgThread != 0 && fgThread != curThread)
                {
                    attachedFg = AttachThreadInput(curThread, fgThread, true);
                }
                if (targetThread != 0 && targetThread != curThread)
                {
                    attachedTarget = AttachThreadInput(curThread, targetThread, true);
                }

                _ = BringWindowToTop(hWnd);
                _ = SetForegroundWindow(hWnd);
                return IsWindowVisible(hWnd) && !IsIconic(hWnd);
            }
            finally
            {
                if (attachedTarget) _ = AttachThreadInput(curThread, targetThread, false);
                if (attachedFg) _ = AttachThreadInput(curThread, fgThread, false);
            }
        }

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

        internal static void CloseWindowGracefully(nint hWnd)
        {
            if (IsWindow(hWnd))
            {
                _ = PostMessage(hWnd, WM_CLOSE, 0, 0);
            }
        }

        internal static bool IsEscapePressed() => (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

        internal static bool MoveAndResizeWindow(nint hWnd, int x, int y, int width, int height)
        {
            if (!IsWindow(hWnd)) return false;
            _ = ForceBringToForeground(hWnd);
            bool ok = SetWindowPos(hWnd, nint.Zero, x, y, width, height, SWP_SHOWWINDOW);
            _ = SetForegroundWindow(hWnd);
            return ok;
        }

        internal static void LeftClickAt(int screenX, int screenY)
        {
            _ = SetCursorPos(screenX, screenY);
            Thread.Sleep(45);
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
            inputs[1].type = INPUT_MOUSE;
            inputs[1].u.mi.dwFlags = MOUSEEVENTF_LEFTUP;
            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        internal static void ScrollWheelAt(int screenX, int screenY, int notches)
        {
            _ = SetCursorPos(screenX, screenY);
            Thread.Sleep(35);
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dwFlags = MOUSEEVENTF_WHEEL;
            inputs[0].u.mi.mouseData = unchecked((uint)(notches * WHEEL_DELTA));
            _ = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }

        internal static void SendVirtualKey(ushort vk)
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = vk;
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = vk;
            inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        internal static void SendKeyCombo(ushort modifierVk, ushort keyVk)
        {
            var inputs = new INPUT[4];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = modifierVk;
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = keyVk;
            inputs[2].type = INPUT_KEYBOARD;
            inputs[2].u.ki.wVk = keyVk;
            inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD;
            inputs[3].u.ki.wVk = modifierVk;
            inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        internal static void SendUnicodeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var inputs = new INPUT[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                inputs[i * 2].type = INPUT_KEYBOARD;
                inputs[i * 2].u.ki.wScan = ch;
                inputs[i * 2].u.ki.dwFlags = KEYEVENTF_UNICODE;

                inputs[i * 2 + 1].type = INPUT_KEYBOARD;
                inputs[i * 2 + 1].u.ki.wScan = ch;
                inputs[i * 2 + 1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            }
            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        internal static SoftwareBitmap CaptureScreenRectToSoftwareBitmap(int left, int top, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("截屏区域尺寸无效。");
            }

            return CaptureDibToSoftwareBitmap(width, height, (screenDc, memDc, bitsPtr, pixels) =>
            {
                _ = BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SRCCOPY);
                Marshal.Copy(bitsPtr, pixels, 0, pixels.Length);
            });
        }

        private static SoftwareBitmap CaptureDibToSoftwareBitmap(
            int width,
            int height,
            Action<nint, nint, nint, byte[]> capturePixels)
        {
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
                if (memDc == nint.Zero) throw new InvalidOperationException("无法创建兼容设备上下文。");

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
                if (dib == nint.Zero || bitsPtr == nint.Zero) throw new InvalidOperationException("无法分配截屏位图缓冲。");

                oldObj = SelectObject(memDc, dib);

                byte[] pixels = new byte[checked(width * height * 4)];
                capturePixels(screenDc, memDc, bitsPtr, pixels);

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
                if (oldObj != nint.Zero && memDc != nint.Zero) _ = SelectObject(memDc, oldObj);
                if (dib != nint.Zero) _ = DeleteObject(dib);
                if (memDc != nint.Zero) _ = DeleteDC(memDc);
                _ = ReleaseDC(nint.Zero, screenDc);
            }
        }

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

            if (IsIconic(hWnd) || !IsWindowVisible(hWnd))
            {
                _ = ForceBringToForeground(hWnd);
                Thread.Sleep(260);
            }

            if (IsIconic(hWnd))
            {
                throw new InvalidOperationException("目标窗口仍处于最小化状态，无法截取画面。");
            }

            if (!GetWindowRect(hWnd, out RECT rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                throw new InvalidOperationException("无法获取目标窗口的有效尺寸，请确认窗口已正常显示。");
            }

            return CaptureDibToSoftwareBitmap(rect.Width, rect.Height, (screenDc, memDc, bitsPtr, pixels) =>
            {
                bool captured = PrintWindow(hWnd, memDc, PW_RENDERFULLCONTENT);
                if (captured)
                {
                    Marshal.Copy(bitsPtr, pixels, 0, pixels.Length);
                }

                // Fallback to screen BitBlt if PrintWindow failed or returned an all-zero buffer.
                if (!captured || IsAllZero(pixels))
                {
                    _ = BitBlt(memDc, 0, 0, rect.Width, rect.Height, screenDc, rect.Left, rect.Top, SRCCOPY);
                    Marshal.Copy(bitsPtr, pixels, 0, pixels.Length);
                }
            });
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
