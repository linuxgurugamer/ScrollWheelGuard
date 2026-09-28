using UnityEngine;

namespace ScrollWheelGuard
{
    /// <summary>
    /// Raises and drops a KSP control lock in step with the guard state, guarantees the lock is
    /// released on scene change, and reports what it sees.
    ///
    /// The heartbeat exists so silence is never ambiguous. If no heartbeat appears, this component
    /// is not running at all. If it appears with zero rects, the guard has nothing to block over
    /// and the patches are irrelevant.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class ScrollGuardRunner : MonoBehaviour
    {
        private bool lockApplied;
        private bool lastBlocking;
        private int blockedCallsAtEntry;
        private float heartbeatAt;
        private int peakRects;

        private static ScrollGuardRunner instance;
        private bool duplicate;

        private void Awake()
        {
            // KSP can leave more than one of these alive; without this every log line
            // appears once per instance, which reads like repeated state changes.
            if (instance != null && instance != this)
            {
                duplicate = true;
                enabled = false;
                return;
            }
            instance = this;
        }

        private void Start()
        {
            if (duplicate) return;
            heartbeatAt = Time.realtimeSinceStartup + 4f;

            if (ScrollGuard.DebugLogging)
            {
                Debug.Log("[ScrollWheelGuard] runner alive in " + HighLogic.LoadedScene +
                          " (autodetect=" + ScrollGuard.AutoDetectWindows +
                          ", freeze=" + ScrollGuard.FreezeCameraZoom + ")");
            }
        }

        private void Update()
        {
            // Keep the runtime switch synchronized with the stock KSP settings page.
            ScrollGuardSettings.ApplyToRuntime();

            bool blocking = ScrollGuard.IsBlocking;

            int live = ScrollGuard.LiveRects;
            if (live > peakRects) peakRects = live;

            if (ScrollGuard.DebugLogging && heartbeatAt > 0f && Time.realtimeSinceStartup >= heartbeatAt)
            {
                heartbeatAt = 0f;
                Debug.Log("[ScrollWheelGuard] " + HighLogic.LoadedScene + " heartbeat: " +
                          ScrollGuard.RegisteredWindows + " registered window(s), " +
                          peakRects + " rect(s) seen so far, mouse at " + Input.mousePosition +
                          ", screen " + Screen.width + "x" + Screen.height +
                          (peakRects == 0 && ScrollGuard.RegisteredWindows == 0
                              ? " -- NOTHING TO BLOCK OVER: no windows registered and none auto-detected"
                              : string.Empty));
            }

            if (blocking != lastBlocking)
            {
                lastBlocking = blocking;

                if (blocking)
                {
                    blockedCallsAtEntry = ScrollGuardPatches.BlockedCalls;
                    if (ScrollGuard.DebugLogging)
                    {
                        Debug.Log("[ScrollWheelGuard] blocking = true in " + HighLogic.LoadedScene);
                    }
                }
                else if (ScrollGuard.DebugLogging)
                {
                    int fired = ScrollGuardPatches.BlockedCalls - blockedCallsAtEntry;
                    Debug.Log("[ScrollWheelGuard] blocking = false in " + HighLogic.LoadedScene +
                              "; fired " + fired + " filter(s) while blocked" +
                              (fired == 0 ? " -- THIS SCENE'S WHEEL READ IS NOT PATCHED" : string.Empty));
                }
            }

            bool wanted = blocking && ScrollGuard.Enabled && ScrollGuard.UseInputLocks;
            if (wanted == lockApplied) return;

            if (wanted)
            {
                InputLockManager.SetControlLock(ScrollGuard.LockMask, ScrollGuard.LockId);
                lockApplied = true;
            }
            else
            {
                RemoveLock();
            }
        }

        private void OnDestroy()
        {
            if (duplicate) return;
            if (instance == this) instance = null;
            RemoveLock();
            ScrollGuard.ResetTransient();
        }

        private void OnApplicationQuit()
        {
            RemoveLock();
        }

        private void RemoveLock()
        {
            if (!lockApplied) return;
            InputLockManager.RemoveControlLock(ScrollGuard.LockId);
            lockApplied = false;
        }
    }
}
