using UnityEngine;

namespace ScrollWheelGuard
{
    /// <summary>
    /// Reference usage. Delete this file (or leave the KSPAddon attribute commented out) in a
    /// production build -- it exists to show the two integration styles.
    /// </summary>
    // [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ExampleWindow : MonoBehaviour
    {
        private Rect windowRect = new Rect(200f, 200f, 360f, 420f);
        private Vector2 scroll = Vector2.zero;
        private bool visible = true;
        private int guardHandle;

        private void Start()
        {
            // Style 1 (preferred): register once, the rect is polled live every frame.
            guardHandle = ScrollGuard.Register(() => windowRect, () => visible);
        }

        private void OnDestroy()
        {
            ScrollGuard.Unregister(guardHandle);
        }

        private void OnGUI()
        {
            if (!visible) return;

            windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Example");

            // Style 2: if you would rather not manage a handle, drop the Register/Unregister pair
            // and call this instead, every frame you draw:
            //
            //     ScrollGuard.PushRect(windowRect);

            // Optional: stop the wheel event leaking to any IMGUI drawn behind this window.
            // Event.current.mousePosition is already GUI space at this level.
            if (Event.current.type == EventType.ScrollWheel && windowRect.Contains(Event.current.mousePosition))
            {
                Event.current.Use();
            }
        }

        private void DrawWindow(int id)
        {
            // This scroll view still works while the guard is active: IMGUI scroll views consume
            // Event.current.delta from the native event queue, not Input.GetAxis, so blocking the
            // axis does not touch them.
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < 60; i++) GUILayout.Label("Row " + i);
            GUILayout.EndScrollView();

            GUILayout.Label(ScrollGuard.IsBlocking ? "Wheel blocked" : "Wheel passing through");

            GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 20f));
        }
    }
}
