using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ScrollWheelGuard
{
    /// <summary>
    /// Removes the need to register anything.
    ///
    /// GUI.Window and GUILayout.Window return the window's screen rect, and unlike
    /// UnityEngine.Input they are ordinary managed methods with IL bodies. A postfix on each
    /// overload feeds that rect straight into the guard, so every IMGUI window in the game --
    /// yours, stock, other mods' -- is covered the moment it draws, with no API calls on the
    /// caller's side.
    ///
    /// Explicit Register / PushRect still work and still make sense for anything that is not a
    /// GUI.Window: a bare GUI.Box used as a panel, a custom dropdown drawn outside its parent
    /// window, and so on.
    /// </summary>
    public static class ScrollGuardAutoWindows
    {
        internal static int Apply(Harmony harmony)
        {
            MethodInfo postfix = AccessTools.Method(typeof(ScrollGuardAutoWindows), "WindowPostfix");
            HarmonyMethod hm = new HarmonyMethod(postfix);

            int patched = 0;
            Type[] hosts = { typeof(GUI), typeof(GUILayout) };

            for (int h = 0; h < hosts.Length; h++)
            {
                MethodInfo[] methods;
                try { methods = hosts[h].GetMethods(BindingFlags.Public | BindingFlags.Static); }
                catch { continue; }

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.ReturnType != typeof(Rect)) continue;
                    if (m.Name != "Window" && m.Name != "ModalWindow") continue;
                    if (m.ContainsGenericParameters) continue;

                    bool hasBody;
                    try { hasBody = m.GetMethodBody() != null; }
                    catch { hasBody = false; }
                    if (!hasBody)
                    {
                        Debug.LogWarning("[ScrollWheelGuard] " + hosts[h].Name + "." + m.Name + " has no IL body; skipped.");
                        continue;
                    }

                    try
                    {
                        harmony.Patch(m, null, hm);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[ScrollWheelGuard] could not postfix " + hosts[h].Name + "." + m.Name + ": " + e.Message);
                    }
                }
            }

            Debug.Log("[ScrollWheelGuard] auto window detection: " + patched + " GUI.Window overload(s) hooked.");
            return patched;
        }

        private static void WindowPostfix(Rect __result)
        {
            if (!ScrollGuard.AutoDetectWindows) return;
            ScrollGuard.PushRect(__result);
        }
    }
}
