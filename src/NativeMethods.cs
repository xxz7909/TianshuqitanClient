using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TianshuQitanLauncher
{
    // 封装本程序用到的 Win32 API（P/Invoke），目前均为 user32.dll 相关
    internal static class NativeMethods
    {
        // 热键消息
        public const int WM_HOTKEY = 0x0312;
        // 虚拟屏幕度量指标常量
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        // 让当前进程感知系统 DPI
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        // 注册全局热键（即使其他窗口或 Flash 抢占键盘焦点也能触发）
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        // 注销全局热键
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // 将鼠标光标限制在指定矩形内；传入 IntPtr.Zero 表示解除限制
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClipCursor(IntPtr lpRect);

        // 获取当前鼠标裁剪（限制）区域
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClipCursor(out RECT lpRect);

        // 获取鼠标当前屏幕坐标
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        // 获取指定坐标处的窗口句柄
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        // 获取当前前台（拥有焦点）窗口句柄
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        // 获取窗口类名
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        // 获取窗口标题文本
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        // 获取窗口在屏幕上的矩形区域
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // 获取系统度量指标（例如虚拟屏幕范围）
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        // 点坐标结构
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;

            public override string ToString()
            {
                return X + "," + Y;
            }
        }

        // 矩形结构（左、上、右、下）
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width
            {
                get { return Right - Left; }
            }

            public int Height
            {
                get { return Bottom - Top; }
            }
        }
    }
}
