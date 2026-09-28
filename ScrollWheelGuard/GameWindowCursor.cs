using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ScrollWheelGuard
{
    /// <summary>
    /// Determines whether the OS cursor is inside the complete KSP top-level window.
    ///
    /// On Windows this uses the native window rectangle, so the title bar and window borders
    /// count as part of KSP. No UNITY_STANDALONE_WIN compiler symbol is required; the Win32
    /// calls are selected at runtime instead.
    ///
    /// Other platforms fall back to Unity's Input.mousePosition against the rendered area.
    /// </summary>
    internal static class GameWindowCursor
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        private static IntPtr cachedWindow = IntPtr.Zero;
        private static float nextWindowLookup;

        internal static bool IsInsideKspWindow()
        {
            if (Application.platform == RuntimePlatform.WindowsPlayer ||
                Application.platform == RuntimePlatform.WindowsEditor)
            {
                bool? nativeResult = IsInsideKspWindowWindows();
                if (nativeResult.HasValue) return nativeResult.Value;
            }

            return IsInsideUnityRenderArea();
        }

        private static bool? IsInsideKspWindowWindows()
        {
            try
            {
                IntPtr hWnd = GetKspWindow();
                if (hWnd == IntPtr.Zero) return null;

                POINT cursor;
                RECT window;

                if (!GetCursorPos(out cursor) || !GetWindowRect(hWnd, out window))
                    return null;

                return cursor.X >= window.Left && cursor.X < window.Right &&
                       cursor.Y >= window.Top && cursor.Y < window.Bottom;
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static IntPtr GetKspWindow()
        {
            if (cachedWindow != IntPtr.Zero && IsWindow(cachedWindow))
                return cachedWindow;

            if (Time.realtimeSinceStartup < nextWindowLookup)
                return IntPtr.Zero;

            nextWindowLookup = Time.realtimeSinceStartup + 1f;

            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    cachedWindow = process.MainWindowHandle;
                }
            }
            catch
            {
                cachedWindow = IntPtr.Zero;
            }

            return cachedWindow;
        }

        private static bool IsInsideUnityRenderArea()
        {
            Vector3 mp = Input.mousePosition;
            return mp.x >= 0f && mp.y >= 0f && mp.x < Screen.width && mp.y < Screen.height;
        }
    }
}
