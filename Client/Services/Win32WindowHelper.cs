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
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        internal const ushort VK_BACK = 0x08;
        internal const ushort VK_RETURN = 0x0D;
        internal const ushort VK_CONTROL = 0x11;
        internal const ushort VK_ESCAPE = 0x1B;
        internal const ushort VK_HOME = 0x24;
        internal const ushort VK_A = 0x41;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly nint HWND_TOPMOST = new(-1);
        private static readonly nint HWND_NOTOPMOST = new(-2);
        private const int WHEEL_DELTA = 120;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const nuint MK_LBUTTON = 0x0001;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenElevation = 20;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION
        {
            public uint TokenIsElevated;
        }

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

        [DllImport("user32.dll")]
        internal static extern bool IsZoomed(nint hWnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern nint WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll")]
        private static extern nint GetAncestor(nint hwnd, uint gaFlags);

        private const uint GA_ROOT = 2;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, out TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);

        internal static bool GetVisibleWindowRect(nint hWnd, out RECT rect)
        {
            if (IsWindow(hWnd) && DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0 && rect.Width > 0 && rect.Height > 0)
            {
                return true;
            }
            return GetWindowRect(hWnd, out rect);
        }

        private const int ERROR_ACCESS_DENIED = 5;

        internal static bool IsTargetProcessElevatedMismatch(nint targetHwnd)
        {
            return GetPrivilegeMismatchDetail(targetHwnd) is not null;
        }

        internal static string? GetPrivilegeMismatchDetail(nint targetHwnd)
        {
            try
            {
                if (!IsWindow(targetHwnd)) return null;
                _ = GetWindowThreadProcessId(targetHwnd, out uint targetPid);
                if (targetPid == 0) return null;

                bool selfElevated = TryCheckProcessElevated((uint)Environment.ProcessId, out bool selfDenied);
                if (selfElevated) return null;

                bool targetElevated = TryCheckProcessElevated(targetPid, out bool targetDenied);
                if (targetElevated || targetDenied)
                {
                    return $"检测到微信 (PID {targetPid}) 正以「管理员权限」运行，而当前工作台为普通权限（{(targetDenied ? "OpenProcessToken=ACCESS_DENIED" : "TokenIsElevated=1")}）。" +
                           "Windows 安全机制 (UIPI) 会允许鼠标移动到微信按钮上触发悬停高亮，但会静默拦截所有模拟点击。\n" +
                           "解决方法：① 彻底退出微信后重新普通打开微信；或 ② 右键「以管理员身份运行」本工作台后再试。";
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryCheckProcessElevated(uint pid, out bool accessDenied)
        {
            accessDenied = false;
            nint proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == nint.Zero)
            {
                if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
                {
                    accessDenied = true;
                }
                return false;
            }
            try
            {
                if (!OpenProcessToken(proc, TOKEN_QUERY, out nint token))
                {
                    // A Medium-integrity process calling OpenProcessToken on a High-integrity (Admin) process
                    // receives ERROR_ACCESS_DENIED (5). That itself confirms the target process is elevated!
                    if (Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
                    {
                        accessDenied = true;
                    }
                    return false;
                }
                try
                {
                    if (GetTokenInformation(token, TokenElevation, out var elev, Marshal.SizeOf<TOKEN_ELEVATION>(), out _))
                    {
                        return elev.TokenIsElevated != 0;
                    }
                    return false;
                }
                finally
                {
                    _ = CloseHandle(token);
                }
            }
            finally
            {
                _ = CloseHandle(proc);
            }
        }

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
            uint targetThread = GetWindowThreadProcessId(hWnd, out uint targetPid);
            if (targetPid != 0)
            {
                _ = AllowSetForegroundWindow(targetPid);
            }

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

                // If Windows foreground lock kept another window (such as our own Client) above WeChat,
                // perform a brief TOPMOST -> NOTOPMOST Z-order bump so clicks & screenshots hit WeChat reliably.
                if (GetForegroundWindow() != hWnd)
                {
                    _ = SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    _ = SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    _ = BringWindowToTop(hWnd);
                    _ = SetForegroundWindow(hWnd);
                }

                return IsWindowVisible(hWnd) && !IsIconic(hWnd);
            }
            finally
            {
                if (attachedTarget) _ = AttachThreadInput(curThread, targetThread, false);
                if (attachedFg) _ = AttachThreadInput(curThread, fgThread, false);
            }
        }

        /// <summary>
        /// Ensures the target window is genuinely the ACTIVE foreground window (not merely on top visually).
        /// If Windows foreground lock prevented SetForegroundWindow from activating Qt/WeChat,
        /// performs one real mouse click on a safe neutral titlebar spot to activate it cleanly.
        /// </summary>
        internal static bool EnsureWindowTrulyActive(nint hWnd)
        {
            if (!IsWindow(hWnd)) return false;
            _ = ForceBringToForeground(hWnd);
            if (GetForegroundWindow() == hWnd) return true;

            Thread.Sleep(60);
            if (GetForegroundWindow() == hWnd) return true;

            if (GetVisibleWindowRect(hWnd, out var visRect) && visRect.Width > 200 && visRect.Height > 120)
            {
                uint dpi = GetDpiForWindow(hWnd);
                double scale = dpi > 0 ? dpi / 96.0 : 1.0;
                int safeX = visRect.Left + (int)Math.Round(visRect.Width * 0.62);
                int safeY = visRect.Top + (int)Math.Round(12 * scale);

                // Only click the neutral titlebar if the point actually belongs to this window's process
                if (IsPointOwnedByWindowProcess(safeX, safeY, hWnd))
                {
                    SendHardwareLeftClick(safeX, safeY, holdMs: 45);
                    Thread.Sleep(110);
                }
            }

            if (GetForegroundWindow() != hWnd)
            {
                _ = ForceBringToForeground(hWnd);
                Thread.Sleep(60);
            }
            return GetForegroundWindow() == hWnd;
        }

        internal static bool IsPointOwnedByWindowProcess(int screenX, int screenY, nint expectedHwnd)
        {
            if (!IsWindow(expectedHwnd)) return false;
            _ = GetWindowThreadProcessId(expectedHwnd, out uint expectedPid);
            if (expectedPid == 0) return false;
            nint hit = WindowFromPoint(new POINT { X = screenX, Y = screenY });
            if (hit == nint.Zero) return false;
            _ = GetWindowThreadProcessId(hit, out uint hitPid);
            return hitPid == expectedPid;
        }

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
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
            if (IsIconic(hWnd) || IsZoomed(hWnd))
            {
                _ = ShowWindow(hWnd, SW_RESTORE);
                Thread.Sleep(80);
            }
            _ = ForceBringToForeground(hWnd);
            bool ok = SetWindowPos(hWnd, nint.Zero, x, y, width, height, SWP_SHOWWINDOW);
            _ = ForceBringToForeground(hWnd);
            return ok;
        }

        /// <summary>
        /// Restores, resizes, and positions the window safely within its CURRENT monitor's work area
        /// (preventing taskbar clipping, maximized lock-out, and cross-monitor DPI jumps).
        /// </summary>
        internal static bool NormalizeWindowOnCurrentMonitor(nint hWnd, int logicalW, int logicalH, int offsetX, int offsetY)
        {
            if (!IsWindow(hWnd)) return false;
            if (IsIconic(hWnd) || IsZoomed(hWnd))
            {
                _ = ShowWindow(hWnd, SW_RESTORE);
                Thread.Sleep(110);
            }

            uint dpi = GetDpiForWindow(hWnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            int targetW = (int)Math.Round(logicalW * scale);
            int targetH = (int)Math.Round(logicalH * scale);
            int targetX = (int)Math.Round(offsetX * scale);
            int targetY = (int)Math.Round(offsetY * scale);

            nint hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (hMon != nint.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi) && mi.rcWork.Width > 300 && mi.rcWork.Height > 300)
                {
                    int maxW = Math.Max(420, mi.rcWork.Width - 24);
                    int maxH = Math.Max(360, mi.rcWork.Height - 24);
                    targetW = Math.Min(targetW, maxW);
                    targetH = Math.Min(targetH, maxH);

                    int desiredX = mi.rcWork.Left + (int)Math.Round(offsetX * scale);
                    int desiredY = mi.rcWork.Top + (int)Math.Round(offsetY * scale);
                    targetX = Math.Clamp(desiredX, mi.rcWork.Left + 4, Math.Max(mi.rcWork.Left + 4, mi.rcWork.Right - targetW - 4));
                    targetY = Math.Clamp(desiredY, mi.rcWork.Top + 4, Math.Max(mi.rcWork.Top + 4, mi.rcWork.Bottom - targetH - 4));
                }
            }

            // Compensate for invisible DWM shadow frame so the visible window aligns cleanly
            if (GetWindowRect(hWnd, out var rawRect) && GetVisibleWindowRect(hWnd, out var visRect))
            {
                int shadowL = Math.Max(0, visRect.Left - rawRect.Left);
                int shadowR = Math.Max(0, rawRect.Right - visRect.Right);
                int shadowT = Math.Max(0, visRect.Top - rawRect.Top);
                int shadowB = Math.Max(0, rawRect.Bottom - visRect.Bottom);
                if (shadowL + shadowR <= 60 && shadowT + shadowB <= 60)
                {
                    targetX -= shadowL;
                    targetY -= shadowT;
                    targetW += shadowL + shadowR;
                    targetH += shadowT + shadowB;
                }
            }

            bool ok = SetWindowPos(hWnd, nint.Zero, targetX, targetY, targetW, targetH, SWP_SHOWWINDOW);
            _ = ForceBringToForeground(hWnd);
            return ok;
        }

        private static bool TryComputeVirtualDesktopNormalized(int screenX, int screenY, out int normX, out int normY)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw > 1 && vh > 1)
            {
                normX = Math.Clamp((int)Math.Round((screenX - vx) * 65535.0 / (vw - 1)), 0, 65535);
                normY = Math.Clamp((int)Math.Round((screenY - vy) * 65535.0 / (vh - 1)), 0, 65535);
                return true;
            }
            normX = 0;
            normY = 0;
            return false;
        }

        private static void MoveCursorAbsolute(int screenX, int screenY)
        {
            if (TryComputeVirtualDesktopNormalized(screenX, screenY, out int normX, out int normY))
            {
                var move = new INPUT[1];
                move[0].type = INPUT_MOUSE;
                move[0].u.mi.dx = normX;
                move[0].u.mi.dy = normY;
                move[0].u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
                _ = SendInput(1, move, Marshal.SizeOf<INPUT>());
            }

            // Always follow up with exact physical SetCursorPos so rounding on virtual desktop never drifts even 1px
            _ = SetCursorPos(screenX, screenY);
        }

        private static bool SendHardwareLeftClick(int screenX, int screenY, int holdMs)
        {
            MoveCursorAbsolute(screenX, screenY);
            Thread.Sleep(65);

            bool hasNorm = TryComputeVirtualDesktopNormalized(screenX, screenY, out int normX, out int normY);
            uint posFlags = hasNorm ? (MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK) : 0;

            var down = new INPUT[1];
            down[0].type = INPUT_MOUSE;
            down[0].u.mi.dx = normX;
            down[0].u.mi.dy = normY;
            down[0].u.mi.dwFlags = posFlags | MOUSEEVENTF_LEFTDOWN;
            uint sentDown = SendInput(1, down, Marshal.SizeOf<INPUT>());
            int errDown = sentDown == 0 ? Marshal.GetLastWin32Error() : 0;

            Thread.Sleep(Math.Max(50, holdMs));

            var up = new INPUT[1];
            up[0].type = INPUT_MOUSE;
            up[0].u.mi.dx = normX;
            up[0].u.mi.dy = normY;
            up[0].u.mi.dwFlags = posFlags | MOUSEEVENTF_LEFTUP;
            uint sentUp = SendInput(1, up, Marshal.SizeOf<INPUT>());
            int errUp = sentUp == 0 ? Marshal.GetLastWin32Error() : 0;

            if (sentDown != 1 || sentUp != 1)
            {
                AppServices.Logs.Warn("WIN32", $"SendInput 点击异常: sentDown={sentDown}(err={errDown}), sentUp={sentUp}(err={errUp}) at ({screenX},{screenY})");
                return false;
            }
            return true;
        }

        internal static bool PostWindowLeftClickAt(nint fallbackHwnd, int screenX, int screenY, out int lastError)
        {
            lastError = 0;
            nint target = WindowFromPoint(new POINT { X = screenX, Y = screenY });
            if (target == nint.Zero || !IsWindow(target)) target = fallbackHwnd;
            if (target == nint.Zero || !IsWindow(target)) return false;

            var pt = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(target, ref pt)) return false;

            nint lParam = new(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
            bool okMove = PostMessage(target, WM_MOUSEMOVE, 0, lParam);
            if (!okMove) lastError = Marshal.GetLastWin32Error();
            Thread.Sleep(35);
            bool okDown = PostMessage(target, WM_LBUTTONDOWN, MK_LBUTTON, lParam);
            if (!okDown) lastError = Marshal.GetLastWin32Error();
            Thread.Sleep(55);
            bool okUp = PostMessage(target, WM_LBUTTONUP, 0, lParam);
            if (!okUp) lastError = Marshal.GetLastWin32Error();

            if (!okDown || !okUp)
            {
                AppServices.Logs.Warn("WIN32", $"PostMessage 点击失败: okDown={okDown}, okUp={okUp}, err={lastError} (err=5 代表权限不足/UIPI拦截)");
                return false;
            }
            return true;
        }

        internal sealed record ClickDiagnostic(
            int RequestedX,
            int RequestedY,
            int ActualCursorX,
            int ActualCursorY,
            bool ForegroundMatched,
            bool HitTargetProcess,
            string HitWindowClass,
            uint HitPid,
            uint SendDown,
            uint SendUp,
            int LastError)
        {
            public string Summary =>
                $"坐标=({RequestedX},{RequestedY}) 实落=({ActualCursorX},{ActualCursorY}) | 命中={HitWindowClass}(PID {HitPid}, 匹配={(HitTargetProcess ? "是" : "否")}) | 前景={(ForegroundMatched ? "是" : "否")} | SendInput={SendDown}/{SendUp}(err={LastError})";
        }

        internal static ClickDiagnostic LeftClickAt(int screenX, int screenY, nint expectedTargetHwnd = 0, string? debugLabel = null)
        {
            bool active = true;
            if (expectedTargetHwnd != nint.Zero && IsWindow(expectedTargetHwnd))
            {
                active = EnsureWindowTrulyActive(expectedTargetHwnd);
                if (!IsPointOwnedByWindowProcess(screenX, screenY, expectedTargetHwnd))
                {
                    nint blocker = WindowFromPoint(new POINT { X = screenX, Y = screenY });
                    string blockerCls = blocker != nint.Zero ? GetWindowClassName(GetAncestor(blocker, GA_ROOT)) : "none";
                    AppServices.Logs.Warn("WIN32", $"点击坐标 ({screenX},{screenY}) 被窗口 {blockerCls} 遮挡，重新置顶目标窗口...");
                    _ = ForceBringToForeground(expectedTargetHwnd);
                    Thread.Sleep(100);
                    active = GetForegroundWindow() == expectedTargetHwnd;
                }
            }

            MoveCursorAbsolute(screenX, screenY);
            _ = GetCursorPos(out var actualPt);

            // Inspect which window & process sits directly under the click point BEFORE flashing overlay
            nint hitHwnd = WindowFromPoint(new POINT { X = screenX, Y = screenY });
            uint hitPid = 0;
            string hitCls = "none";
            if (hitHwnd != nint.Zero)
            {
                _ = GetWindowThreadProcessId(hitHwnd, out hitPid);
                hitCls = GetWindowClassName(GetAncestor(hitHwnd, GA_ROOT));
            }
            bool hitTarget = true;
            if (expectedTargetHwnd != nint.Zero && IsWindow(expectedTargetHwnd))
            {
                _ = GetWindowThreadProcessId(expectedTargetHwnd, out uint expPid);
                hitTarget = expPid != 0 && hitPid == expPid;
            }

            // Show click-through visual target reticle right at the click point!
            ClickVisualOverlay.FlashAt(screenX, screenY, isWarning: !hitTarget || !active);

            Thread.Sleep(65);

            bool hasNorm = TryComputeVirtualDesktopNormalized(screenX, screenY, out int normX, out int normY);
            uint posFlags = hasNorm ? (MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK) : 0;

            var down = new INPUT[1];
            down[0].type = INPUT_MOUSE;
            down[0].u.mi.dx = normX;
            down[0].u.mi.dy = normY;
            down[0].u.mi.dwFlags = posFlags | MOUSEEVENTF_LEFTDOWN;
            uint sentDown = SendInput(1, down, Marshal.SizeOf<INPUT>());
            int lastErr = sentDown == 0 ? Marshal.GetLastWin32Error() : 0;

            Thread.Sleep(65);

            var up = new INPUT[1];
            up[0].type = INPUT_MOUSE;
            up[0].u.mi.dx = normX;
            up[0].u.mi.dy = normY;
            up[0].u.mi.dwFlags = posFlags | MOUSEEVENTF_LEFTUP;
            uint sentUp = SendInput(1, up, Marshal.SizeOf<INPUT>());
            if (sentUp == 0 && lastErr == 0) lastErr = Marshal.GetLastWin32Error();

            var diag = new ClickDiagnostic(
                screenX, screenY, actualPt.X, actualPt.Y,
                active, hitTarget, hitCls, hitPid,
                sentDown, sentUp, lastErr);

            string prefix = string.IsNullOrWhiteSpace(debugLabel) ? "点击" : debugLabel;
            if (sentDown == 1 && sentUp == 1 && hitTarget)
            {
                AppServices.Logs.Info("CLICK", $"{prefix} | {diag.Summary}");
            }
            else
            {
                AppServices.Logs.Warn("CLICK", $"{prefix} [异常] | {diag.Summary}");
                if ((sentDown != 1 || sentUp != 1) && expectedTargetHwnd != nint.Zero)
                {
                    _ = PostWindowLeftClickAt(expectedTargetHwnd, screenX, screenY, out _);
                }
            }

            return diag;
        }

        internal sealed record AdaptiveClickResult(bool Succeeded, int AttemptsUsed, int LastX, int LastY, int LastPostMessageError);

        private static readonly (int Dx, int Dy)[] DefaultAdaptiveOffsets =
        [
            (0, 0),
            (-18, 0),
            (20, 0),
            (0, -6),
            (0, 7)
        ];

        /// <summary>
        /// Adaptive multi-point click with live verification:
        /// Instead of blindly hammering a single pixel, explores smart DPI-scaled offsets around (baseX, baseY)
        /// (center -> left/right -> slight vertical adjustments -> direct WM_LBUTTONDOWN/UP fallback),
        /// stopping IMMEDIATELY the moment verifySucceededAsync() returns true.
        /// </summary>
        internal static async Task<AdaptiveClickResult> ClickWithAdaptiveRetryAsync(
            nint targetHwnd,
            int baseX,
            int baseY,
            double scale,
            string actionLabel,
            Func<Task<bool>> verifySucceededAsync,
            Action<string>? log = null,
            int waitAfterClickMs = 360,
            bool allowPostMessageFallback = true,
            (int Dx, int Dy)[]? customOffsetsLogical = null,
            Action<int, int>? onCursorMoved = null,
            CancellationToken ct = default)
        {
            var offsets = customOffsetsLogical is { Length: > 0 } ? customOffsetsLogical : DefaultAdaptiveOffsets;
            int lastX = baseX;
            int lastY = baseY;
            int lastPostErr = 0;

            for (int i = 0; i < offsets.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (dx, dy) = offsets[i];
                int cx = baseX + (int)Math.Round(dx * scale);
                int cy = baseY + (int)Math.Round(dy * scale);
                lastX = cx;
                lastY = cy;

                string offsetTag = dx == 0 && dy == 0 ? "中心" : $"偏移({dx:+#;-#;0},{dy:+#;-#;0})";
                var diag = LeftClickAt(cx, cy, targetHwnd, $"{actionLabel}#{i + 1}[{offsetTag}]");
                onCursorMoved?.Invoke(cx, cy);
                log?.Invoke($"🖱 [{actionLabel}] 第 {i + 1}/{offsets.Length} 次尝试 [{offsetTag}] -> {diag.Summary}");

                if (waitAfterClickMs > 0)
                {
                    await Task.Delay(waitAfterClickMs, ct);
                }

                if (await verifySucceededAsync())
                {
                    log?.Invoke($"✓ [{actionLabel}] 第 {i + 1} 次尝试成功 [{offsetTag}] ({cx},{cy})");
                    return new AdaptiveClickResult(true, i + 1, cx, cy, 0);
                }

                // On first attempt (center) or last attempt, also try direct WM_LBUTTONDOWN/UP if enabled
                if (allowPostMessageFallback && (i == 0 || i == offsets.Length - 1) && targetHwnd != nint.Zero)
                {
                    bool okPost = PostWindowLeftClickAt(targetHwnd, cx, cy, out int postErr);
                    lastPostErr = postErr;
                    log?.Invoke($"🖱 [{actionLabel}] 窗口消息补点 ({cx},{cy}) -> ok={okPost}, err={postErr}");
                    if (postErr == ERROR_ACCESS_DENIED)
                    {
                        return new AdaptiveClickResult(false, i + 1, cx, cy, postErr);
                    }
                    await Task.Delay(Math.Max(220, waitAfterClickMs - 80), ct);
                    if (await verifySucceededAsync())
                    {
                        log?.Invoke($"✓ [{actionLabel}] 窗口消息补点成功 ({cx},{cy})");
                        return new AdaptiveClickResult(true, i + 1, cx, cy, 0);
                    }
                }
            }

            return new AdaptiveClickResult(false, offsets.Length, lastX, lastY, lastPostErr);
        }

        internal static void ScrollWheelAt(int screenX, int screenY, int notches)
        {
            MoveCursorAbsolute(screenX, screenY);
            Thread.Sleep(40);
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].u.mi.dwFlags = MOUSEEVENTF_WHEEL;
            inputs[0].u.mi.mouseData = unchecked((uint)(notches * WHEEL_DELTA));
            _ = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }

        internal sealed record RailIconBounds(int CenterX, int CenterY, int Top, int Bottom, int Width, int Height);

        /// <summary>
        /// Visually detects the top icons on WeChat's left navigation rail directly from window pixels
        /// (Run 0 = Avatar, Run 1 = Messages, Run 2 = Contacts, Run 3 = Favorites...).
        /// Works across WeChat 3.x / 4.x, 100%~250% DPI, and Light / Dark themes without hardcoded offsets.
        /// </summary>
        internal static List<RailIconBounds> DetectWeChatLeftRailIcons(SoftwareBitmap bmp, double scale)
        {
            var results = new List<RailIconBounds>();
            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;
            if (w < 120 || h < 200) return results;

            byte[] pixels = AvatarImageService.CopyBgraPixels(bmp);

            // Sample solid rail background color near the left inner edge (y in 25%..75%)
            int sampleX = Math.Clamp((int)Math.Round(5 * scale), 2, Math.Max(2, w / 20));
            var bSamples = new List<byte>(64);
            var gSamples = new List<byte>(64);
            var rSamples = new List<byte>(64);
            int yStartBg = (int)(h * 0.25);
            int yEndBg = (int)(h * 0.75);
            int stepBg = Math.Max(1, (yEndBg - yStartBg) / 48);
            for (int y = yStartBg; y < yEndBg; y += stepBg)
            {
                int idx = (y * w + sampleX) * 4;
                bSamples.Add(pixels[idx]);
                gSamples.Add(pixels[idx + 1]);
                rSamples.Add(pixels[idx + 2]);
            }
            bSamples.Sort();
            gSamples.Sort();
            rSamples.Sort();
            byte bgB = bSamples[bSamples.Count / 2];
            byte bgG = gSamples[gSamples.Count / 2];
            byte bgR = rSamples[rSamples.Count / 2];

            // Auto-detect rail right boundary where the sidebar meets the white/dark chat list column
            int minRailW = Math.Clamp((int)Math.Round(42 * scale), 30, w / 5);
            int maxRailW = Math.Clamp((int)Math.Round(76 * scale), minRailW + 10, w / 4);
            int railRight = Math.Clamp((int)Math.Round(56 * scale), minRailW, maxRailW);
            for (int x = minRailW; x <= maxRailW; x++)
            {
                int diffRows = 0;
                int checkedRows = 0;
                for (int y = yStartBg; y < yEndBg; y += stepBg)
                {
                    checkedRows++;
                    int idx = (y * w + x) * 4;
                    int diff = Math.Abs(pixels[idx] - bgB) + Math.Abs(pixels[idx + 1] - bgG) + Math.Abs(pixels[idx + 2] - bgR);
                    if (diff > 14) diffRows++;
                }
                if (checkedRows > 0 && diffRows >= checkedRows * 0.72)
                {
                    railRight = x;
                    break;
                }
            }

            int scanXMin = Math.Clamp((int)Math.Round(railRight * 0.14), 3, railRight - 4);
            int scanXMax = Math.Clamp((int)Math.Round(railRight * 0.86), scanXMin + 4, railRight - 2);
            int scanYMin = Math.Clamp((int)Math.Round(6 * scale), 2, h / 10);
            int scanYMax = Math.Min(h - 10, (int)Math.Round(420 * scale));
            int maxMergeGap = Math.Clamp((int)Math.Round(4 * scale), 2, 10);
            int minIconHeight = Math.Clamp((int)Math.Round(12 * scale), 8, 36);
            int maxIconHeight = Math.Clamp((int)Math.Round(64 * scale), 40, 160);
            int minFgPerRow = Math.Max(3, (int)((scanXMax - scanXMin + 1) * 0.10));

            int runStart = -1;
            int gap = 0;
            int runMinX = int.MaxValue;
            int runMaxX = -1;

            void FlushRun(int runEnd)
            {
                int runH = runEnd - runStart + 1;
                if (runH >= minIconHeight && runH <= maxIconHeight && runMaxX >= runMinX)
                {
                    results.Add(new RailIconBounds(
                        CenterX: (runMinX + runMaxX) / 2,
                        CenterY: (runStart + runEnd) / 2,
                        Top: runStart,
                        Bottom: runEnd,
                        Width: runMaxX - runMinX + 1,
                        Height: runH));
                }
                runStart = -1;
                gap = 0;
                runMinX = int.MaxValue;
                runMaxX = -1;
            }

            for (int y = scanYMin; y <= scanYMax; y++)
            {
                int fg = 0;
                int rowMinX = int.MaxValue;
                int rowMaxX = -1;
                int rowOff = y * w * 4;

                for (int x = scanXMin; x <= scanXMax; x++)
                {
                    int idx = rowOff + x * 4;
                    int diff = Math.Abs(pixels[idx] - bgB) + Math.Abs(pixels[idx + 1] - bgG) + Math.Abs(pixels[idx + 2] - bgR);
                    if (diff > 24)
                    {
                        fg++;
                        if (x < rowMinX) rowMinX = x;
                        if (x > rowMaxX) rowMaxX = x;
                    }
                }

                if (fg >= minFgPerRow)
                {
                    if (runStart < 0) runStart = y;
                    gap = 0;
                    if (rowMinX < runMinX) runMinX = rowMinX;
                    if (rowMaxX > runMaxX) runMaxX = rowMaxX;
                }
                else if (runStart >= 0)
                {
                    gap++;
                    if (gap > maxMergeGap)
                    {
                        FlushRun(y - gap);
                    }
                }
            }

            if (runStart >= 0)
            {
                FlushRun(scanYMax - gap);
            }

            return results;
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
