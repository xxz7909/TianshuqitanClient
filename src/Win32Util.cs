using System;
using System.Drawing;
using System.Windows.Forms;

namespace TianshuQitanLauncher
{
    // Win32 / 布局相关的共享工具方法，集中去重：
    // MainForm 与 MouseDiagnostics 原先各自实现了一份完全相同的 FormatRectangle/Quote 等逻辑。
    internal static class Win32Util
    {
        // 格式化矩形为 "左,上,宽x高"
        internal static string FormatRectangle(Rectangle rectangle)
        {
            return rectangle.Left + "," + rectangle.Top + "," + rectangle.Width + "x" + rectangle.Height;
        }

        // 为空字符串包上引号，便于日志阅读
        internal static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "'") + "\"";
        }

        // 计算控件在屏幕坐标系下的客户端矩形
        internal static Rectangle GetClientScreenRectangle(Control control)
        {
            return new Rectangle(control.PointToScreen(Point.Empty), control.ClientSize);
        }

        // 获取整个虚拟屏幕的矩形范围（多显示器下即所有屏幕拼接范围）
        internal static NativeMethods.RECT GetVirtualScreenRect()
        {
            NativeMethods.RECT rect = new NativeMethods.RECT();
            rect.Left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            rect.Top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            rect.Right = rect.Left + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            rect.Bottom = rect.Top + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            return rect;
        }

        // 格式化矩形为 "左,上,宽x高"。提供 Rectangle 与 RECT 两个重载，统一全程序矩形输出格式。
        internal static string FormatRectangle(NativeMethods.RECT rectangle)
        {
            return rectangle.Left + "," + rectangle.Top + "," + rectangle.Width + "x" + rectangle.Height;
        }

        // 将数值限制在 [min, max] 区间内（MainForm 与 LauncherConfig 原先各自内联了一份，已统一到此）
        internal static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        // 判断当前鼠标裁剪区域是否“受限”（不等于完整的虚拟屏幕范围，且非全零/空矩形）。
        // 全零或空矩形代表系统未设置裁剪，不应误判为“受限”，避免每 30ms 误触 ClipCursor。
        internal static bool IsClipRestricted(NativeMethods.RECT clip, NativeMethods.RECT virtualScreen)
        {
            if (clip.Left == 0 && clip.Top == 0 && clip.Right == 0 && clip.Bottom == 0)
            {
                return false;
            }

            if (clip.Right <= clip.Left || clip.Bottom <= clip.Top)
            {
                return false;
            }

            return clip.Left != virtualScreen.Left
                || clip.Top != virtualScreen.Top
                || clip.Right != virtualScreen.Right
                || clip.Bottom != virtualScreen.Bottom;
        }
    }
}
