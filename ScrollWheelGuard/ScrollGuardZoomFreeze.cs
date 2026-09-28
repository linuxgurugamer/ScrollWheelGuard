using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ScrollWheelGuard
{
    /// <summary>
    /// Watches every float member on every camera controller and reports which ones change while
    /// the guard is blocking. If the wheel still zooms while no filter fires, the member that moves
    /// names the class doing the zooming, which is the one fact needed to find the unpatched read.
    ///
    /// It also acts as a fallback: members whose names look like a zoom distance are written back,
    /// so the camera holds still even when the input path is unknown. Set
    /// ScrollGuard.FreezeAllCameraFloats to pin every float instead, which stops all camera motion
    /// while the cursor is over a window.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EveryScene, false)]
    public class ScrollGuardZoomFreeze : MonoBehaviour
    {
        // Members that plausibly hold a live zoom distance.
        private static readonly string[] ZoomNames =
        {
            "distance", "dist", "camdistance", "camdist", "targetdistance",
            "zoom", "camzoom", "zoomlevel", "scroll"
        };

        // ...but never a bound, a rate, or a tuning constant.
        private static readonly string[] Reject =
        {
            "min", "max", "start", "default", "initial", "speed", "sens", "sharp",
            "lerp", "smooth", "time", "sqr", "orig", "reset"
        };

        private sealed class Target
        {
            public object Owner;
            public FieldInfo Field;
            public PropertyInfo Property;
            public string Label;
            public bool Pinnable;
            public float Snapshot;
            public bool Primed;
            public int Failures;

            public float Read()
            {
                return Field != null
                    ? (float)Field.GetValue(Owner)
                    : (float)Property.GetValue(Owner, null);
            }

            public void Write(float value)
            {
                if (Field != null) Field.SetValue(Owner, value);
                else Property.SetValue(Owner, value, null);
            }
        }

        private static ScrollGuardZoomFreeze instance;

        private readonly List<Target> targets = new List<Target>();
        private readonly HashSet<string> reported = new HashSet<string>();
        private bool duplicate;
        private bool episodeOpen;
        private float nextScan;

        private void Awake()
        {
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
            Scan();
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void Update()
        {
            if (duplicate) return;
            if (targets.Count == 0 && Time.realtimeSinceStartup >= nextScan) Scan();
            Tick();
        }

        private void LateUpdate()
        {
            if (duplicate) return;
            Tick();
        }

        private void Tick()
        {
            if (targets.Count == 0) return;

            bool blocking = ScrollGuard.Enabled && ScrollGuard.IsBlocking;

            if (!blocking)
            {
                if (episodeOpen)
                {
                    episodeOpen = false;
                    reported.Clear();
                }

                Snapshot();
                return;
            }

            episodeOpen = true;

            for (int i = targets.Count - 1; i >= 0; i--)
            {
                Target t = targets[i];
                if (!t.Primed) continue;

                try
                {
                    float current = t.Read();
                    if (current == t.Snapshot) continue;

                    if (ScrollGuard.DebugLogging && reported.Add(t.Label))
                    {
                        ScrollGuardLog.Info("[ScrollWheelGuard] CHANGED WHILE BLOCKED: " + t.Label + "  " +
                                  t.Snapshot.ToString("F4") + " -> " + current.ToString("F4") +
                                  (t.Pinnable || ScrollGuard.FreezeAllCameraFloats ? "  (pinning)" : "  (watching only)"));
                    }

                    if (t.Pinnable || ScrollGuard.FreezeAllCameraFloats) t.Write(t.Snapshot);
                    else t.Snapshot = current;
                }
                catch (Exception e)
                {
                    if (++t.Failures >= 5)
                    {
                        ScrollGuardLog.Warning("[ScrollWheelGuard] dropping watch target " + t.Label + ": " + e.Message);
                        targets.RemoveAt(i);
                    }
                }
            }
        }

        private void Snapshot()
        {
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                Target t = targets[i];
                try
                {
                    t.Snapshot = t.Read();
                    t.Primed = true;
                }
                catch
                {
                    if (++t.Failures >= 5) targets.RemoveAt(i);
                }
            }
        }

        private void Scan()
        {
            nextScan = Time.realtimeSinceStartup + 2f;
            targets.Clear();

            MonoBehaviour[] all;
            try { all = FindObjectsOfType<MonoBehaviour>(); }
            catch { return; }

            HashSet<string> typeNames = new HashSet<string>();

            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour mb = all[i];
                if (mb == null) continue;

                Type type = mb.GetType();
                if (type.Name.IndexOf("Camera", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string asm = type.Assembly.GetName().Name;
                if (asm != "Assembly-CSharp" && asm != "Assembly-CSharp-firstpass") continue;

                typeNames.Add(type.Name);
                Collect(mb, type);
            }

            if (!ScrollGuard.DebugLogging) return;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[ScrollWheelGuard] watching ").Append(targets.Count).Append(" float member(s) in ")
              .Append(HighLogic.LoadedScene).Append(" across ").Append(typeNames.Count).Append(" camera type(s):");

            foreach (string n in typeNames) sb.Append(' ').Append(n);

            if (targets.Count == 0)
            {
                sb.Append("  -- NO CAMERA CONTROLLERS FOUND, the zoom fallback cannot work here");
            }

            ScrollGuardLog.Info(sb.ToString());
        }

        private void Collect(object owner, Type type)
        {
            FieldInfo[] fields = type.GetFields(AccessTools.all);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f.IsStatic || f.IsLiteral || f.IsInitOnly) continue;
                if (f.FieldType != typeof(float)) continue;

                targets.Add(new Target
                {
                    Owner = owner,
                    Field = f,
                    Label = type.Name + "." + f.Name,
                    Pinnable = ScrollGuard.FreezeCameraZoom && LooksLikeZoom(f.Name)
                });
            }

            PropertyInfo[] props = type.GetProperties(AccessTools.all);
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo p = props[i];
                if (p.PropertyType != typeof(float)) continue;
                if (!p.CanRead) continue;
                if (p.GetIndexParameters().Length != 0) continue;

                targets.Add(new Target
                {
                    Owner = owner,
                    Property = p,
                    Label = type.Name + "." + p.Name,
                    Pinnable = ScrollGuard.FreezeCameraZoom && p.CanWrite && LooksLikeZoom(p.Name)
                });
            }
        }

        private static bool LooksLikeZoom(string name)
        {
            string lower = name.ToLowerInvariant();

            for (int i = 0; i < Reject.Length; i++)
            {
                if (lower.Contains(Reject[i])) return false;
            }

            for (int i = 0; i < ZoomNames.Length; i++)
            {
                if (lower == ZoomNames[i]) return true;
            }

            return false;
        }
    }
}
