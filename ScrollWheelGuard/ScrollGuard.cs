using System;
using System.Collections.Generic;
using UnityEngine;

namespace ScrollWheelGuard
{
    /// <summary>
    /// Public API. Register the screen rects of your IMGUI windows here and the mouse wheel
    /// will be swallowed (camera zoom, map zoom, editor zoom) whenever the cursor is inside one.
    ///
    /// Coordinates are GUI space: origin top-left, same space as the Rect you hand to
    /// GUILayout.Window / GUI.Window. No conversion needed on your side.
    ///
    /// Everything here is safe to call from OnGUI, Update, or anywhere else.
    /// </summary>
    public static class ScrollGuard
    {
        public const string LockId = "ScrollWheelGuard";

        /// <summary>Master switch. Set false to disable all blocking without unpatching.</summary>
        public static bool Enabled = true;

        /// <summary>
        /// Block wheel input whenever the OS cursor is outside the complete KSP window.
        /// On Windows the native top-level window rectangle is used, including title bar and borders.
        /// Other platforms fall back to Unity's rendered screen bounds.
        /// </summary>
        public static bool BlockOutsideGameWindow = true;

        /// <summary>Also raise a KSP control lock while blocking. Belt and braces; Harmony does the real work.</summary>
        public static bool UseInputLocks = true;

        /// <summary>Which control types the input lock covers while blocking.</summary>
        public static ControlTypes LockMask = ControlTypes.CAMERACONTROLS;

        /// <summary>
        /// Block whenever any IMGUI control owns the mouse (window drag, resize grip, slider drag),
        /// even if the cursor has slipped outside the window rect. Off by default because it is global:
        /// it also fires for stock and third-party IMGUI.
        /// </summary>
        public static bool BlockWhileHotControl = false;

        /// <summary>
        /// Treat every IMGUI window as a blocker automatically, with no Register or PushRect call
        /// needed anywhere. See ScrollGuardAutoWindows.
        /// </summary>
        public static bool AutoDetectWindows = true;

        /// <summary>Ignore auto-detected rects smaller than this in either dimension.</summary>
        public static float MinAutoRectSize = 8f;

        /// <summary>Log every transition in and out of the blocking state. Use this to tell a failed
        /// hit test apart from a failed patch: no log lines means the cursor is not being detected
        /// over a registered window, and the patches are not even being consulted.</summary>
        public static bool DebugLogging = true;

        /// <summary>
        /// Fallback: hold the camera's zoom distance while blocking, for scenes whose wheel read the
        /// IL scan cannot locate. Works regardless of the input path. See ScrollGuardZoomFreeze.
        /// </summary>
        public static bool FreezeCameraZoom = true;

        /// <summary>
        /// Pin every float on every camera controller while blocking, not just the ones whose
        /// names look like a zoom distance. Stops all camera motion while the cursor is over a
        /// window. Blunt, but it works when the zoom lives in a member with an unexpected name.
        /// </summary>
        public static bool FreezeAllCameraFloats = false;

        /// <summary>Extra pixels added around every registered rect. Useful if you draw resize grips on the border.</summary>
        public static float Padding = 0f;

        private sealed class Entry
        {
            public int Handle;
            public Func<Rect> Rect;
            public Func<bool> Visible;
            public int Failures;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<Rect> FrameRects = new List<Rect>();

        // -1000 rather than int.MinValue so the "frameCount - x" comparisons can never overflow.
        private static int frameRectsFrame = -1000;
        private static int forceFrame = -1000;
        private static int cacheFrame = -1000;
        private static bool cacheValue;
        private static int nextHandle = 1;

        // ------------------------------------------------------------------
        // Registration
        // ------------------------------------------------------------------

        /// <summary>
        /// Register a window. The rect is polled fresh every frame, so it tracks dragging and
        /// resizing with no latency. Optionally supply a visibility predicate.
        /// Returns a handle; pass it to Unregister in OnDestroy.
        /// </summary>
        public static int Register(Func<Rect> rectProvider, Func<bool> isVisible = null)
        {
            if (rectProvider == null) throw new ArgumentNullException("rectProvider");

            Entry e = new Entry { Handle = nextHandle++, Rect = rectProvider, Visible = isVisible };
            Entries.Add(e);
            Invalidate();
            return e.Handle;
        }

        public static void Unregister(int handle)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (Entries[i].Handle == handle) Entries.RemoveAt(i);
            }
            Invalidate();
        }

        /// <summary>
        /// One-shot alternative to Register: call from OnGUI every frame you draw the window.
        /// The rect stays live for this frame and the next (OnGUI runs after Update, so the
        /// carry-over frame covers cameras that already sampled input this frame).
        /// </summary>
        public static void PushRect(Rect screenRect)
        {
            if (screenRect.width < MinAutoRectSize || screenRect.height < MinAutoRectSize) return;

            int f = Time.frameCount;
            if (frameRectsFrame != f)
            {
                FrameRects.Clear();
                frameRectsFrame = f;
            }
            // GUI.Window fires once per IMGUI event, so the same rect arrives repeatedly
            // within a single frame.
            for (int i = 0; i < FrameRects.Count; i++)
            {
                if (FrameRects[i] == screenRect) return;
            }

            FrameRects.Add(screenRect);
            Invalidate();
        }

        /// <summary>Unconditionally swallow the wheel for this frame and the next, regardless of rects.</summary>
        public static void BlockNow()
        {
            forceFrame = Time.frameCount;
            Invalidate();
        }

        // ------------------------------------------------------------------
        // Query
        // ------------------------------------------------------------------

        /// <summary>
        /// True if the wheel should be eaten right now. Evaluated lazily and cached per frame,
        /// so it does not matter whether the camera's Update runs before or after ours.
        /// </summary>
        public static bool IsBlocking { get { return Evaluate(); } }

        /// <summary>Windows registered through Register.</summary>
        public static int RegisteredWindows { get { return Entries.Count; } }

        /// <summary>Rects pushed (explicitly or by auto-detection) on this frame or the last one.</summary>
        public static int LiveRects
        {
            get { return Time.frameCount - frameRectsFrame <= 1 ? FrameRects.Count : 0; }
        }

        public static void Invalidate()
        {
            cacheFrame = -1000;
        }

        internal static void ResetTransient()
        {
            FrameRects.Clear();
            frameRectsFrame = -1000;
            forceFrame = -1000;
            Invalidate();
        }

        private static bool Evaluate()
        {
            int f = Time.frameCount;
            if (cacheFrame == f) return cacheValue;

            bool result = false;

            if (Enabled)
            {
                if (f - forceFrame <= 1)
                {
                    result = true;
                }
                else if (BlockWhileHotControl && GUIUtility.hotControl != 0)
                {
                    result = true;
                }
                else
                {
                    bool insideGameWindow = GameWindowCursor.IsInsideKspWindow();

                    // When requested, moving the pointer completely outside KSP enters the same
                    // blocking state used while hovering a guarded IMGUI window.  The existing
                    // Harmony filters therefore return zero for wheel reads before the editor,
                    // flight or map camera can act on them.
                    if (!insideGameWindow && BlockOutsideGameWindow)
                    {
                        result = true;
                    }
                    else if (insideGameWindow)
                    {
                        Vector3 mp = Input.mousePosition;

                        // Input.mousePosition is bottom-left origin, IMGUI rects are top-left origin.
                        result = HitAny(new Vector2(mp.x, Screen.height - mp.y));
                    }
                }
            }

            cacheFrame = f;
            cacheValue = result;
            return result;
        }

        private static bool HitAny(Vector2 guiPoint)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                Entry e = Entries[i];
                try
                {
                    if (e.Visible != null && !e.Visible()) continue;
                    if (Hit(e.Rect(), guiPoint)) return true;
                }
                catch (Exception ex)
                {
                    if (++e.Failures >= 5)
                    {
                        Debug.LogError("[ScrollWheelGuard] Dropping registration " + e.Handle +
                                       " after repeated provider errors: " + ex);
                        Entries.RemoveAt(i);
                    }
                }
            }

            if (Time.frameCount - frameRectsFrame <= 1)
            {
                for (int i = 0; i < FrameRects.Count; i++)
                {
                    if (Hit(FrameRects[i], guiPoint)) return true;
                }
            }

            return false;
        }

        private static bool Hit(Rect r, Vector2 p)
        {
            if (r.width <= 0f || r.height <= 0f) return false;

            if (Padding != 0f)
            {
                r.xMin -= Padding;
                r.xMax += Padding;
                r.yMin -= Padding;
                r.yMax += Padding;
            }

            return r.Contains(p);
        }
    }
}
