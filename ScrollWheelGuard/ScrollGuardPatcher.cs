using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;
using Debug = UnityEngine.Debug;

namespace ScrollWheelGuard
{
    /// <summary>
    /// Applies the patches once at load.
    ///
    /// UnityEngine.Input.GetAxis is an extern with no IL body, so Harmony cannot prefix it. Its
    /// callers are ordinary managed methods though, so the call sites get rewritten instead: the
    /// call operand is swapped for a shim of identical signature that consults the guard. Because
    /// the shim receives the axis name as an argument, this works even when the caller builds the
    /// name at runtime rather than using a string literal.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ScrollGuardPatcher : MonoBehaviour
    {
        public const string HarmonyId = "scrollwheelguard";

        /// <summary>Log a dump of KSP's wheel-related API at startup.</summary>
        public static bool DumpDiagnostics = true;

        /// <summary>Scan every method in Assembly-CSharp rather than only hinted types.</summary>
        public static bool ForceDeepScan = true;

        /// <summary>
        /// Scan every loaded assembly rather than only Assembly-CSharp. On by default: the editor's
        /// zoom turned out not to touch GameSettings.AXIS_MOUSEWHEEL at all, and uGUI reads the
        /// wheel from UnityEngine.UI, which the Assembly-CSharp-only scan never examined.
        /// </summary>
        public static bool ScanAllAssemblies = true;

        /// <summary>Types scanned when ForceDeepScan is off. Matched against Type.FullName.</summary>
        public static string[] TypeHints = { "Camera", "MapView", "Planetarium", "Zoom", "Input", "Editor", "VAB", "SPH" };

        private static readonly HashSet<MethodBase> AlreadyPatched = new HashSet<MethodBase>();
        private static readonly string NewLine = Environment.NewLine;
        private static bool applied;

        private void Start()
        {
            try
            {
                ReportLoadedCopies();

                if (applied)
                {
                    Debug.LogWarning("[ScrollWheelGuard] patcher ran twice in one assembly; ignoring the second pass.");
                    return;
                }
                applied = true;

                try
                {
                    if (Harmony.HasAnyPatches(HarmonyId))
                    {
                        Debug.LogError("[ScrollWheelGuard] patches for id '" + HarmonyId + "' already exist, so a SECOND COPY of " +
                                       "ScrollWheelGuard.dll is loaded. Two copies means two separate sets of statics: " +
                                       "whatever you register with one copy is invisible to the other's patches. " +
                                       "Delete the duplicate, then restart. Skipping this pass.");
                        return;
                    }
                }
                catch (Exception) { /* older Harmony without HasAnyPatches */ }

                ScrollGuardPatches.Resolve();
                if (DumpDiagnostics) ScrollGuardPatches.Dump();

                Harmony harmony = new Harmony(HarmonyId);

                int direct = PatchUnityInputDirect(harmony);
                int windows = ScrollGuardAutoWindows.Apply(harmony);
                int readers = PatchAxisReaders(harmony);
                int candidates = PatchCallSites(harmony, ForceDeepScan);

                Debug.Log("[ScrollWheelGuard] ready: " + direct + " Input member(s) prefixed directly, " +
                          readers + " axis reader(s) prefixed, " +
                          candidates + " candidate method(s) transpiled, " +
                          ScrollGuardPatches.FilteredReads + " wheel read(s) rewritten, " +
                          windows + " window overload(s) hooked.");

                if (ScrollGuardPatches.UnmatchedCandidates > 0)
                {
                    Debug.LogWarning("[ScrollWheelGuard] " + ScrollGuardPatches.UnmatchedCandidates +
                                     " method(s) touch the wheel but use a read shape this build does not " +
                                     "recognise; each was logged by name above.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[ScrollWheelGuard] setup failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // Prefix UnityEngine.Input members that actually have IL
        // ------------------------------------------------------------------

        /// <summary>
        /// Input.GetAxis and GetAxisRaw are externs and cannot be patched, but the
        /// mouseScrollDelta getter is ordinary managed code. Prefixing it covers every reader in
        /// every assembly at once -- uGUI's input module included -- with no scanning involved.
        /// </summary>
        private static int PatchUnityInputDirect(Harmony harmony)
        {
            MethodInfo m = ScrollGuardPatches.InputScrollDelta;
            if (m == null)
            {
                Debug.LogWarning("[ScrollWheelGuard] Input.mouseScrollDelta getter not found.");
                return 0;
            }

            bool hasBody;
            try { hasBody = m.GetMethodBody() != null; }
            catch { hasBody = false; }

            if (!hasBody)
            {
                Debug.LogWarning("[ScrollWheelGuard] Input.mouseScrollDelta is extern here; falling back to call-site rewriting.");
                return 0;
            }

            try
            {
                harmony.Patch(m, new HarmonyMethod(AccessTools.Method(typeof(ScrollGuardPatches), "ScrollDeltaPrefix")));
                Debug.Log("[ScrollWheelGuard] prefixed Input.mouseScrollDelta directly; this covers every reader in every assembly.");
                return 1;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ScrollWheelGuard] could not prefix Input.mouseScrollDelta: " + e.Message);
                return 0;
            }
        }

        // ------------------------------------------------------------------
        // Prefix the axis binding's own readers
        // ------------------------------------------------------------------
        // ------------------------------------------------------------------

        private static int PatchAxisReaders(Harmony harmony)
        {
            HarmonyMethod prefix = new HarmonyMethod(AccessTools.Method(typeof(ScrollGuardPatches), "AxisReaderPrefix"));

            int n = 0;
            foreach (MethodInfo m in ScrollGuardPatches.WheelAxisReaders())
            {
                try
                {
                    harmony.Patch(m, prefix);
                    Debug.Log("[ScrollWheelGuard] prefixed " + ScrollGuardPatches.Describe(m));
                    n++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ScrollWheelGuard] could not prefix " + ScrollGuardPatches.Describe(m) + ": " + e.Message);
                }
            }

            if (n == 0) Debug.LogWarning("[ScrollWheelGuard] no parameterless float readers found on the mouse wheel binding.");
            return n;
        }

        // ------------------------------------------------------------------
        // Rewrite call sites
        // ------------------------------------------------------------------

        private static int PatchCallSites(Harmony harmony, bool deep)
        {
            Stopwatch sw = Stopwatch.StartNew();
            HarmonyMethod transpiler = new HarmonyMethod(AccessTools.Method(typeof(ScrollGuardPatches), "WheelFilterTranspiler"));

            List<MethodInfo> targets = new List<MethodInfo>();
            int scanned = 0;

            foreach (Assembly asm in ScanTargets())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type == null || type.ContainsGenericParameters) continue;
                    if (!deep && !Hinted(type)) continue;

                    MethodInfo[] methods;
                    try { methods = type.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly); }
                    catch { continue; }

                    for (int i = 0; i < methods.Length; i++)
                    {
                        MethodInfo m = methods[i];
                        if (m.IsAbstract || m.ContainsGenericParameters) continue;
                        if (AlreadyPatched.Contains(m)) continue;
                        scanned++;
                        if (ScrollGuardPatches.ReferencesWheel(m)) targets.Add(m);
                    }
                }
            }

            int patched = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                try
                {
                    harmony.Patch(targets[i], null, null, transpiler);
                    AlreadyPatched.Add(targets[i]);
                    patched++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ScrollWheelGuard] transpile failed on " + ScrollGuardPatches.Describe(targets[i]) + ": " + e.Message);
                }
            }

            Debug.Log("[ScrollWheelGuard] " + (deep ? "deep" : "targeted") + " scan: " + scanned + " method(s) examined, " +
                      targets.Count + " candidate(s), " + patched + " transpiled, " + sw.ElapsedMilliseconds + " ms.");
            return patched;
        }

        private static bool Hinted(Type type)
        {
            string name = type.FullName;
            if (string.IsNullOrEmpty(name)) return false;

            for (int i = 0; i < TypeHints.Length; i++)
            {
                if (name.Contains(TypeHints[i])) return true;
            }
            return false;
        }

        private static IEnumerable<Assembly> ScanTargets()
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            Assembly self = typeof(ScrollGuardPatcher).Assembly;

            for (int i = 0; i < all.Length; i++)
            {
                string n = all[i].GetName().Name;

                if (n == "Assembly-CSharp" || n == "Assembly-CSharp-firstpass")
                {
                    yield return all[i];
                    continue;
                }

                if (!ScanAllAssemblies || all[i] == self) continue;

                // The BCL never reads the mouse; Unity's own assemblies very much do, and
                // UnityEngine.UI is where uGUI turns a scroll into an IScrollHandler call.
                if (n.StartsWith("System") || n.StartsWith("mscorlib") || n.StartsWith("Mono.") ||
                    n.StartsWith("0Harmony") || n.StartsWith("netstandard") || n.StartsWith("Boo.") ||
                    n.StartsWith("I18N")) continue;

                yield return all[i];
            }
        }

        /// <summary>
        /// Logs every loaded copy of this assembly. More than one is a hard error: each copy gets
        /// its own statics, so registrations and patches end up in different worlds.
        /// </summary>
        private static void ReportLoadedCopies()
        {
            string self = typeof(ScrollGuardPatcher).Assembly.GetName().Name;
            List<string> copies = new List<string>();

            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].GetName().Name != self) continue;

                string where;
                try { where = all[i].Location; }
                catch { where = "<location unavailable>"; }
                copies.Add(string.IsNullOrEmpty(where) ? "<dynamic>" : where);
            }

            if (copies.Count <= 1) return;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[ScrollWheelGuard] ").Append(self).Append(" is loaded ").Append(copies.Count).Append(" times:");
            for (int i = 0; i < copies.Count; i++) sb.Append(NewLine).Append("    ").Append(copies[i]);
            sb.Append(NewLine).Append("    Remove all but one and restart KSP.");
            Debug.LogError(sb.ToString());
        }
    }

    /// <summary>
    /// Patch bodies and reflection plumbing. Public because rewritten IL calls into it.
    /// </summary>
    public static class ScrollGuardPatches
    {
        internal const string WheelAxisName = "Mouse ScrollWheel";

        internal static FieldInfo WheelField;
        internal static Type WheelBindingType;
        internal static MethodInfo InputGetAxis;
        internal static MethodInfo InputGetAxisRaw;
        internal static MethodInfo InputScrollDelta;

        /// <summary>Individual wheel reads that were rewritten or filtered.</summary>
        public static int FilteredReads;

        /// <summary>Methods that reference the wheel but whose read shape was not recognised.</summary>
        public static int UnmatchedCandidates;

        /// <summary>Times a guard has fired while blocking. Zero in a scene that still zooms means that scene is uncovered.</summary>
        public static int BlockedCalls;

        private static MethodInfo guardedGetAxis;
        private static MethodInfo guardedGetAxisRaw;
        private static MethodInfo guardedScrollDelta;
        private static MethodInfo filterFloat;

        private static readonly List<object> WheelInstances = new List<object>();
        private static readonly List<string> WheelAxisNames = new List<string>();
        private static object wheelRoot;

        internal static void Resolve()
        {
            WheelField = AccessTools.Field(typeof(GameSettings), "AXIS_MOUSEWHEEL");
            if (WheelField != null && !WheelField.FieldType.IsPrimitive && WheelField.FieldType != typeof(string))
            {
                WheelBindingType = WheelField.FieldType;
            }

            InputGetAxis = AccessTools.Method(typeof(Input), "GetAxis", new[] { typeof(string) });
            InputGetAxisRaw = AccessTools.Method(typeof(Input), "GetAxisRaw", new[] { typeof(string) });

            PropertyInfo delta = AccessTools.Property(typeof(Input), "mouseScrollDelta");
            InputScrollDelta = delta != null ? delta.GetGetMethod() : null;

            guardedGetAxis = AccessTools.Method(typeof(ScrollGuardPatches), "GuardedGetAxis");
            guardedGetAxisRaw = AccessTools.Method(typeof(ScrollGuardPatches), "GuardedGetAxisRaw");
            guardedScrollDelta = AccessTools.Method(typeof(ScrollGuardPatches), "GuardedScrollDelta");
            filterFloat = AccessTools.Method(typeof(ScrollGuardPatches), "FilterFloat");

            RefreshWheelInstances();
        }

        // ------------------------------------------------------------------
        // Which objects and axis names belong to the mouse wheel
        // ------------------------------------------------------------------

        /// <summary>
        /// The binding object plus every reference-typed field hanging off it (primary, secondary
        /// and friends), so a read that goes through a sub-object is recognised too. Also harvests
        /// any string fields as candidate axis names, which covers a rebound wheel.
        /// </summary>
        private static void RefreshWheelInstances()
        {
            WheelInstances.Clear();
            WheelAxisNames.Clear();
            WheelAxisNames.Add(WheelAxisName);

            if (WheelField == null) return;

            object root;
            try { root = WheelField.GetValue(null); }
            catch { return; }

            wheelRoot = root;
            if (root == null) return;

            WheelInstances.Add(root);
            Harvest(root, 0);
        }

        private static void Harvest(object owner, int depth)
        {
            if (depth > 2 || owner == null) return;

            FieldInfo[] fields;
            try { fields = owner.GetType().GetFields(AccessTools.all); }
            catch { return; }

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f.IsStatic) continue;

                object v;
                try { v = f.GetValue(owner); }
                catch { continue; }
                if (v == null) continue;

                string s = v as string;
                if (s != null)
                {
                    if (s.Length > 0 && !WheelAxisNames.Contains(s)) WheelAxisNames.Add(s);
                    continue;
                }

                if (f.FieldType.IsValueType || f.FieldType.IsArray) continue;
                if (Contains(WheelInstances, v)) continue;

                WheelInstances.Add(v);
                Harvest(v, depth + 1);
            }
        }

        private static bool Contains(List<object> list, object item)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], item)) return true;
            }
            return false;
        }

        private static bool IsWheelInstance(object instance)
        {
            if (instance == null || WheelField == null) return false;

            // Settings reloads replace the binding objects, so re-harvest when the root changes.
            object root;
            try { root = WheelField.GetValue(null); }
            catch { return false; }

            if (!ReferenceEquals(root, wheelRoot)) RefreshWheelInstances();

            return Contains(WheelInstances, instance);
        }

        private static bool IsWheelAxisName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            for (int i = 0; i < WheelAxisNames.Count; i++)
            {
                if (string.Equals(name, WheelAxisNames[i], StringComparison.Ordinal)) return true;
            }

            return name.IndexOf("scrollwheel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("mouse wheel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------
        // Shims installed in place of the original calls
        // ------------------------------------------------------------------

        public static float GuardedGetAxis(string axisName)
        {
            float value = Input.GetAxis(axisName);
            if (!IsWheelAxisName(axisName) || !ScrollGuard.IsBlocking) return value;

            BlockedCalls++;
            return 0f;
        }

        public static float GuardedGetAxisRaw(string axisName)
        {
            float value = Input.GetAxisRaw(axisName);
            if (!IsWheelAxisName(axisName) || !ScrollGuard.IsBlocking) return value;

            BlockedCalls++;
            return 0f;
        }

        public static Vector2 GuardedScrollDelta()
        {
            Vector2 value = Input.mouseScrollDelta;
            if (!ScrollGuard.IsBlocking) return value;

            BlockedCalls++;
            return Vector2.zero;
        }

        private static bool ScrollDeltaPrefix(ref Vector2 __result)
        {
            if (!ScrollGuard.IsBlocking) return true;

            BlockedCalls++;
            __result = Vector2.zero;
            return false;
        }

        /// <summary>Inserted after a binding read whose call cannot be swapped (instance method).</summary>
        public static float FilterFloat(float value)
        {
            if (!ScrollGuard.IsBlocking) return value;

            BlockedCalls++;
            return 0f;
        }

        // ------------------------------------------------------------------
        // Prefix on the binding's own readers
        // ------------------------------------------------------------------

        private static bool AxisReaderPrefix(object __instance, ref float __result)
        {
            if (!ScrollGuard.IsBlocking) return true;
            if (!IsWheelInstance(__instance)) return true;

            BlockedCalls++;
            __result = 0f;
            return false;
        }

        // ------------------------------------------------------------------
        // Transpiler
        // ------------------------------------------------------------------

        private static IEnumerable<CodeInstruction> WheelFilterTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int swapped = 0;
            int inserted = 0;

            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ci = code[i];
                if (ci.opcode != OpCodes.Call && ci.opcode != OpCodes.Callvirt) continue;

                MethodInfo target = ci.operand as MethodInfo;
                if (target == null) continue;

                // Identical signatures, so the operand can simply be replaced. The shim sees the
                // axis name at runtime, which covers callers that do not use a string literal.
                if (Same(target, InputGetAxis)) { ci.opcode = OpCodes.Call; ci.operand = guardedGetAxis; swapped++; continue; }
                if (Same(target, InputGetAxisRaw)) { ci.opcode = OpCodes.Call; ci.operand = guardedGetAxisRaw; swapped++; continue; }
                if (Same(target, InputScrollDelta)) { ci.opcode = OpCodes.Call; ci.operand = guardedScrollDelta; swapped++; continue; }

                // Instance reads on the binding cannot be swapped, so filter the returned value.
                // Deliberately loose: any float-returning call shortly after the wheel binding is
                // loaded is the wheel being read, whatever type declares it.
                if (target.ReturnType != typeof(float)) continue;
                if (target.GetParameters().Length > 1) continue;
                if (!PrecededByWheelField(code, i)) continue;

                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, filterFloat));
                i++;
                inserted++;
            }

            int hits = swapped + inserted;
            if (hits > 0)
            {
                FilteredReads += hits;
                Debug.Log("[ScrollWheelGuard] covered " + hits + " wheel read(s) in " + Describe(original) +
                          " (" + swapped + " swapped, " + inserted + " filtered)");
            }
            else
            {
                UnmatchedCandidates++;
                Debug.LogWarning("[ScrollWheelGuard] UNMATCHED: " + Describe(original) +
                                 " touches the wheel but no read shape was recognised.");
            }

            return code;
        }

        private static bool PrecededByWheelField(List<CodeInstruction> code, int index)
        {
            for (int i = index - 1; i >= 0 && i >= index - 8; i--)
            {
                if (code[i].opcode == OpCodes.Ldsfld && Same(code[i].operand as FieldInfo, WheelField)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // IL scanning
        // ------------------------------------------------------------------

        internal static bool ReferencesWheel(MethodInfo method)
        {
            byte[] il;
            try
            {
                MethodBody body = method.GetMethodBody();
                if (body == null) return false;
                il = body.GetILAsByteArray();
            }
            catch { return false; }

            if (il == null || il.Length < 5) return false;

            Module module = method.Module;

            // Byte scan rather than a full IL walk: a misaligned false positive only costs a
            // transpiler pass that changes nothing.
            for (int i = 0; i + 4 < il.Length; i++)
            {
                byte op = il[i];
                if (op != 0x72 && op != 0x7E && op != 0x28 && op != 0x6F) continue;

                int token = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);

                try
                {
                    if (op == 0x72)
                    {
                        if (IsWheelAxisName(module.ResolveString(token))) return true;
                    }
                    else if (op == 0x7E)
                    {
                        if (WheelField != null && Same(module.ResolveField(token), WheelField)) return true;
                    }
                    else
                    {
                        MethodInfo m = module.ResolveMethod(token) as MethodInfo;
                        if (m == null) continue;

                        // Any Input axis call is worth rewriting: the shim decides at runtime
                        // whether the name is the wheel, so a computed name is covered too.
                        if (Same(m, InputGetAxis) || Same(m, InputGetAxisRaw) || Same(m, InputScrollDelta)) return true;
                    }
                }
                catch { }
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        internal static void Dump()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("[ScrollWheelGuard] === API dump ===");

            sb.AppendLine("Input.GetAxis body: " + BodyState(InputGetAxis));
            sb.AppendLine("Input.GetAxisRaw body: " + BodyState(InputGetAxisRaw));
            sb.AppendLine("Input.get_mouseScrollDelta body: " + BodyState(InputScrollDelta));

            if (WheelField == null)
            {
                sb.AppendLine("GameSettings.AXIS_MOUSEWHEEL: NOT FOUND");
            }
            else
            {
                object value = null;
                try { value = WheelField.GetValue(null); }
                catch (Exception e) { sb.AppendLine("  (field read threw: " + e.Message + ")"); }

                sb.AppendLine("GameSettings.AXIS_MOUSEWHEEL: declared " + WheelField.FieldType.FullName +
                              ", runtime " + (value == null ? "null" : value.GetType().FullName));

                Type t = value != null ? value.GetType() : WheelField.FieldType;

                sb.AppendLine("  methods on " + t.FullName + ":");
                MethodInfo[] methods = t.GetMethods(AccessTools.all);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.DeclaringType == typeof(object)) continue;
                    sb.AppendLine("    " + Describe(m) + " -> " + m.ReturnType.Name +
                                  (m.GetMethodBody() == null ? "  [extern]" : string.Empty));
                }

                sb.AppendLine("  fields on " + t.FullName + ":");
                FieldInfo[] fields = t.GetFields(AccessTools.all);
                for (int i = 0; i < fields.Length; i++)
                {
                    string fv = "?";
                    try { fv = value == null ? "-" : Convert.ToString(fields[i].GetValue(value)); }
                    catch { }
                    sb.AppendLine("    " + fields[i].FieldType.Name + " " + fields[i].Name + " = " + fv);
                }

                sb.Append("  wheel-owned instances: ").Append(WheelInstances.Count);
                for (int i = 0; i < WheelInstances.Count; i++)
                {
                    sb.AppendLine().Append("    ").Append(WheelInstances[i].GetType().FullName);
                }
                sb.AppendLine();

                sb.Append("  candidate axis names: ");
                for (int i = 0; i < WheelAxisNames.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('"').Append(WheelAxisNames[i]).Append('"');
                }
                sb.AppendLine();
            }

            sb.AppendLine("  GameSettings statics mentioning WHEEL/ZOOM:");
            FieldInfo[] settings = typeof(GameSettings).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < settings.Length; i++)
            {
                string n = settings[i].Name;
                if (n.IndexOf("WHEEL", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("ZOOM", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string v = "?";
                try { v = Convert.ToString(settings[i].GetValue(null)); }
                catch { }
                sb.AppendLine("    " + settings[i].FieldType.Name + " " + n + " = " + v);
            }

            Debug.Log(sb.ToString());
        }

        private static string BodyState(MethodInfo m)
        {
            if (m == null) return "method not found";
            try { return m.GetMethodBody() == null ? "none (extern, unpatchable)" : "present"; }
            catch { return "unreadable"; }
        }

        internal static IEnumerable<MethodInfo> WheelAxisReaders()
        {
            HashSet<Type> types = new HashSet<Type>();
            if (WheelBindingType != null) types.Add(WheelBindingType);
            for (int i = 0; i < WheelInstances.Count; i++) types.Add(WheelInstances[i].GetType());

            HashSet<MethodInfo> seen = new HashSet<MethodInfo>();

            foreach (Type t in types)
            {
                MethodInfo[] methods;
                try { methods = t.GetMethods(AccessTools.all); }
                catch { continue; }

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.IsStatic || m.IsAbstract) continue;
                    if (m.ReturnType != typeof(float)) continue;
                    if (m.GetParameters().Length != 0) continue;
                    if (m.ContainsGenericParameters) continue;
                    if (m.DeclaringType == typeof(object)) continue;
                    if (!seen.Add(m)) continue;

                    bool hasBody;
                    try { hasBody = m.GetMethodBody() != null; }
                    catch { hasBody = false; }
                    if (!hasBody) continue;

                    yield return m;
                }
            }
        }

        internal static string Describe(MethodBase m)
        {
            if (m == null) return "<null>";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?").Append('.').Append(m.Name).Append('(');

            ParameterInfo[] ps = m.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.Name);
            }

            return sb.Append(')').ToString();
        }

        private static bool Same(MethodInfo a, MethodInfo b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            return a.MetadataToken == b.MetadataToken && a.Module == b.Module;
        }

        private static bool Same(FieldInfo a, FieldInfo b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            return a.MetadataToken == b.MetadataToken && a.Module == b.Module;
        }
    }
}
