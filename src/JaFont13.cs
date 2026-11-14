using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace PTCGLiveJaFont
{
    [BepInPlugin("jp.ptcglive.jafont", "PTCGLiveJaFont", "1.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static TMP_FontAsset JpFont;
        internal static Material JpMaterial;
        internal static BepInEx.Logging.ManualLogSource Log;
        private static bool _failed;
        private static bool _applying;
        private static bool _errLogged;
        private Harmony _harmony;

        void Awake()
        {
            Log = Logger;
            _harmony = new Harmony("jp.ptcglive.jafont");
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("PTCGLiveJaFont 1.3 loaded");
            StartCoroutine(SilenceZhFontLoader());
        }

        // The zh mod's TextMeshPro prefixes apply their bundled font on every
        // dirty callback, but only when LoadFontAssets() returns true.
        // Forcing it to false disables their font assertion (and only that:
        // localization and card patches are separate classes, untouched).
        public static bool SilenceZhFontLoad(ref bool __result)
        {
            __result = false;
            return false;
        }

        private IEnumerator SilenceZhFontLoader()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    var zhType = AccessTools.TypeByName("PTCGLiveZhMod.Patches.TextMeshProPatcher");
                    if (zhType != null)
                    {
                        var m = AccessTools.Method(zhType, "LoadFontAssets");
                        if (m != null)
                        {
                            _harmony.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(SilenceZhFontLoad)));
                            Log.LogInfo("Zh font loader silenced (attempt " + attempt + ")");
                            yield break;
                        }
                    }
                }
                catch (Exception e) { Log.LogWarning("silencer attempt failed: " + e.Message); }
                yield return new WaitForSeconds(0.5f);
            }
            Log.LogWarning("Zh TextMeshProPatcher not found; silencer not installed");
        }

        static TMP_FontAsset TryFamily(string fam)
        {
            try
            {
                var f = TMP_FontAsset.CreateFontAsset(fam, "Regular", 90);
                Log.LogInfo("CreateFontAsset family '" + fam + "' -> " + (f == null ? "null" : "OK"));
                return f;
            }
            catch (Exception e)
            {
                Log.LogWarning("CreateFontAsset family '" + fam + "' threw: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        internal static bool EnsureFont()
        {
            if (JpFont != null) return true;
            if (_failed) return false;
            string[] families = { "Yu Gothic", "Meiryo", "MS Gothic", "Noto Sans JP" };
            TMP_FontAsset font = null;
            string used = null;
            foreach (var fam in families)
            {
                font = TryFamily(fam);
                if (font != null) { used = fam; break; }
            }
            if (font == null)
            {
                Log.LogWarning("All family CreateFontAsset routes returned null");
                _failed = true;
                return false;
            }
            JpFont = font;
            JpFont.name = "JaFont_" + used;
            JpFont.hideFlags = HideFlags.DontSave;
            if (JpFont.material != null) JpFont.material.hideFlags = HideFlags.DontSave;
            JpMaterial = JpFont.material;
            Log.LogInfo("Japanese font ready: " + used + " mode=" + JpFont.atlasPopulationMode);
            Prewarm();
            try
            {
                var fb = TMP_Settings.fallbackFontAssets;
                if (fb != null && !fb.Contains(JpFont))
                {
                    fb.Add(JpFont);
                    Log.LogInfo("Registered into TMP_Settings.fallbackFontAssets (count=" + fb.Count + ")");
                }
            }
            catch (Exception e) { Log.LogWarning("fallback registration failed: " + e.Message); }
            return true;
        }

        static void Prewarm()
        {
            try
            {
                var chars = new HashSet<char>();
                string modDir = Path.Combine(Paths.PluginPath, "PTCGLiveJaMod");
                string[] files =
                {
                    Path.Combine(modDir, "text", "all.txt"),
                    Path.Combine(modDir, "databases", "names.txt"),
                    Path.Combine(modDir, "databases", "attks-name.txt"),
                    Path.Combine(modDir, "databases", "attks-text.txt"),
                };
                foreach (var fp in files)
                {
                    if (!File.Exists(fp)) continue;
                    foreach (char c in File.ReadAllText(fp, Encoding.UTF8)) chars.Add(c);
                }
                for (char c = 'ぁ'; c <= 'ゖ'; c++) chars.Add(c);
                for (char c = 'ァ'; c <= 'ヶ'; c++) chars.Add(c);
                for (char c = '！'; c <= '～'; c++) chars.Add(c);
                var sb = new StringBuilder();
                foreach (var c in chars) if (c >= 0x20) sb.Append(c);
                string all = sb.ToString();
                int missingTotal = 0;
                for (int i = 0; i < all.Length; i += 400)
                {
                    string chunk = all.Substring(i, Math.Min(400, all.Length - i));
                    string missing;
                    JpFont.TryAddCharacters(chunk, out missing, true);
                    if (!string.IsNullOrEmpty(missing)) missingTotal += missing.Length;
                }
                if (JpFont.atlasTextures != null)
                    foreach (var t in JpFont.atlasTextures) if (t != null) t.hideFlags = HideFlags.DontSave;
                Log.LogInfo("Prewarm done: requested=" + all.Length + " missing=" + missingTotal + " atlases=" + (JpFont.atlasTextures == null ? 0 : JpFont.atlasTextures.Length));
            }
            catch (Exception e) { Log.LogWarning("Prewarm failed: " + e.Message); }
        }

        internal static void Apply(TMP_Text text)
        {
            if (text == null) return;
            if (_applying) return;
            _applying = true;
            try
            {
                if (!EnsureFont()) return;
                if (text.font != JpFont)
                {
                    text.font = JpFont;
                    text.fontSharedMaterial = JpMaterial;
                }
                else if (text.fontSharedMaterial != JpMaterial)
                {
                    text.fontSharedMaterial = JpMaterial;
                }
            }
            catch (Exception e)
            {
                if (!_errLogged) { _errLogged = true; Log.LogWarning("Apply exception (first only): " + e.GetType().Name + ": " + e.Message); }
            }
            finally { _applying = false; }
        }
    }

    [HarmonyPatch(typeof(TextMeshProUGUI), "SetLayoutDirty")]
    static class PatchUguiLayout
    {
        [HarmonyPriority(Priority.Last)]
        static void Prefix(TextMeshProUGUI __instance) { Plugin.Apply(__instance); }
    }

    [HarmonyPatch(typeof(TextMeshProUGUI), "SetMaterialDirty")]
    static class PatchUguiMaterial
    {
        [HarmonyPriority(Priority.Last)]
        static void Prefix(TextMeshProUGUI __instance) { Plugin.Apply(__instance); }
    }

    [HarmonyPatch(typeof(TextMeshPro), "SetLayoutDirty")]
    static class PatchTmpLayout
    {
        [HarmonyPriority(Priority.Last)]
        static void Prefix(TextMeshPro __instance) { Plugin.Apply(__instance); }
    }

    [HarmonyPatch(typeof(TextMeshPro), "SetMaterialDirty")]
    static class PatchTmpMaterial
    {
        [HarmonyPriority(Priority.Last)]
        static void Prefix(TextMeshPro __instance) { Plugin.Apply(__instance); }
    }
}
