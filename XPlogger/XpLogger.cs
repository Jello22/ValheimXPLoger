using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;

namespace XPLogger
{
    [BepInPlugin("your.id.valheim.xplogger", "XP Logger (PTB-safe)", "1.0.3")]
    public class XpLogger : BaseUnityPlugin
    {
        internal static ManualLogSource L;

        public void Awake()
        {
            L = Logger;
            new Harmony(Info.Metadata.GUID).PatchAll(typeof(XpLogger).Assembly);
            L.LogInfo("XP Logger loaded (PTB-safe).");
        }
    }

    [HarmonyPatch]
    public static class Patch_Skills_AddExperienceOrRaiseSkill
    {
        static Type T_Player, T_Localization;
        static FieldInfo F_Player_Local;
        static MethodInfo M_Player_GetCurrentWeapon;
        static PropertyInfo P_Localization_Instance;
        static MethodInfo M_Localization_Localize;

        static MethodBase TargetMethod()
        {
            // Pick the game assembly explicitly (PTB uses assembly_valheim)
            var asm =
                AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name.Equals("assembly_valheim", StringComparison.OrdinalIgnoreCase)) ??
                AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase));

            if (asm == null) throw new Exception("Valheim game assembly not found (assembly_valheim/Assembly-CSharp).");

            // Try namespaced then legacy type names
            var tSkills = asm.GetType("Valheim.Skills", false) ?? asm.GetType("Skills", false);
            if (tSkills == null) throw new Exception("Type 'Skills' not found in game assembly.");

            // Prefer AddExperience(SkillType, float), else RaiseSkill(SkillType, float)
            var methods = tSkills.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo m = methods.FirstOrDefault(mi =>
                mi.Name == "AddExperience" &&
                mi.GetParameters().Length == 2 &&
                mi.GetParameters()[1].ParameterType == typeof(float));
            if (m == null)
            {
                m = methods.FirstOrDefault(mi =>
                    mi.Name == "RaiseSkill" &&
                    mi.GetParameters().Length == 2 &&
                    mi.GetParameters()[1].ParameterType == typeof(float));
            }

            if (m == null) throw new Exception("Could not locate Skills.AddExperience/RaiseSkill(float).");

            // Log exactly what we hooked
            XpLogger.L.LogInfo("Hooking: " + m.DeclaringType.FullName + "." + m.Name + "(" +
                               string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name)) + ")");
            return m;
        }

        // PREFIX: logs factor and weapon name
        [HarmonyPrefix]
        static void Prefix(object __instance, object skillType, [HarmonyArgument(1)] ref float amount)
        {
            string weapon = "none";
            try
            {
                var asm = __instance.GetType().Assembly;

                if (T_Player == null)
                    T_Player = asm.GetType("Valheim.Player", false) ?? asm.GetType("Player", false);

                if (T_Player != null)
                {
                    if (F_Player_Local == null)
                        F_Player_Local = T_Player.GetField("m_localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                        ?? T_Player.GetField("s_localPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                    var player = F_Player_Local != null ? F_Player_Local.GetValue(null) : null;
                    if (player != null)
                    {
                        if (M_Player_GetCurrentWeapon == null)
                            M_Player_GetCurrentWeapon = T_Player.GetMethod("GetCurrentWeapon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

                        var item = M_Player_GetCurrentWeapon != null ? M_Player_GetCurrentWeapon.Invoke(player, null) : null;
                        if (item != null)
                        {
                            var fShared = item.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var shared = fShared != null ? fShared.GetValue(item) : null;
                            if (shared != null)
                            {
                                var fName = shared.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var raw = fName != null ? fName.GetValue(shared) as string : null;
                                weapon = Localize(asm, raw ?? "none");
                            }
                        }
                    }
                }
            }
            catch { /* best-effort only */ }

            XpLogger.L.LogInfo($"XP/Fctr +{amount:F3} → {skillType} via {weapon}");
        }

        // POSTFIX: adds level, XP to next level, and hits remaining at this factor
        [HarmonyPostfix]
        static void Postfix(object __instance, object skillType, [HarmonyArgument(1)] float amount)
        {
            try
            {
                var tSkills = __instance.GetType();
                var mGetSkillLevel = tSkills.GetMethod("GetSkillLevel",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mGetSkillLevel == null) return;

                // Level AFTER this hit (float)
                var after = (float)mGetSkillLevel.Invoke(__instance, new object[] { skillType });

                int whole = (int)Math.Floor(after);
                float frac = after - whole;

                // In Valheim, "factor" == XP increment toward the next whole level.
                float xpToNext = Math.Max(0f, 1.0f - frac);
                float hitsLeft = (amount > 0f) ? (xpToNext / amount) : float.PositiveInfinity;

                string msg =
                    $"[{skillType}] factor={amount:F3}  XP+={amount:F3}  level {after:F3}  " +
                    $"to next: {xpToNext:F3} XP  (~{hitsLeft:F1} hits @ {amount:F3})";

                XpLogger.L.LogInfo(msg);

                // Optional: also print into the in-game F5 console (if enabled with -console)
                var asm = __instance.GetType().Assembly;
                var tConsole = asm.GetType("Valheim.Console", false) ?? asm.GetType("Console", false);
                if (tConsole != null)
                {
                    var pInst = tConsole.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                             ?? tConsole.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var inst = pInst != null ? pInst.GetValue(null, null) : null;
                    var mPrint = tConsole.GetMethod("Print", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                                    null, new[] { typeof(string) }, null);
                    if (inst != null && mPrint != null)
                        mPrint.Invoke(inst, new object[] { msg });
                }
            }
            catch { /* best-effort */ }
        }

        static string Localize(Assembly asm, string token)
        {
            try
            {
                if (T_Localization == null)
                    T_Localization = asm.GetType("Valheim.Localization", false) ?? asm.GetType("Localization", false);
                if (T_Localization == null) return token;

                if (P_Localization_Instance == null)
                    P_Localization_Instance = T_Localization.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                               ?? T_Localization.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var inst = P_Localization_Instance != null ? P_Localization_Instance.GetValue(null, null) : null;
                if (inst == null) return token;

                if (M_Localization_Localize == null)
                    M_Localization_Localize = T_Localization.GetMethod("Localize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                var s = M_Localization_Localize != null ? M_Localization_Localize.Invoke(inst, new object[] { token }) as string : null;
                return string.IsNullOrWhiteSpace(s) ? token : s;
            }
            catch { return token; }
        }
    }
}
