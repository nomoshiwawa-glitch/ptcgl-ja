using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace PTCGLiveJaCards
{
    [BepInPlugin("jp.ptcgl.jacards", "PTCGLiveJaCards", "2.4.0")]
    public class JaCards : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static Dictionary<string, string> Map = new Dictionary<string, string>();
        internal static readonly Dictionary<string, string> MapMd5 = new Dictionary<string, string>();
        internal static string CacheDir;
        internal static string MissFile;
        internal static readonly HashSet<string> Misses = new HashSet<string>();
        internal static readonly Dictionary<string, Texture2D> JaTextures = new Dictionary<string, Texture2D>();
        internal static readonly Dictionary<string, Texture2D> FaceCache = new Dictionary<string, Texture2D>();
        internal static readonly Dictionary<string, Texture2D> ThumbCache = new Dictionary<string, Texture2D>();
        internal static readonly HashSet<string> Loading = new HashSet<string>();
        internal static readonly Dictionary<int, KeyValuePair<string, float>> Registered = new Dictionary<int, KeyValuePair<string, float>>();
        internal static int SwapCount;
        internal static int SweepSwapCount;
        internal static readonly int FaceTexId = Shader.PropertyToID("_CardColorDiffuse");
        internal static readonly Regex BundleRx = new Regex("[a-z][a-z0-9-]*_en_[0-9]{3}", RegexOptions.Compiled);
        internal static readonly HashSet<string> SeenTexNames = new HashSet<string>();
        internal static readonly HashSet<string> UiSeen = new HashSet<string>();
        internal static bool DexGate;

        private void Awake()
        {
            Log = Logger;
            string dir = Path.GetDirectoryName(Info.Location);
            CacheDir = Path.Combine(dir, "cache");
            Directory.CreateDirectory(CacheDir);
            MissFile = Path.Combine(dir, "misses.txt");
            if (File.Exists(MissFile)) foreach (var l in File.ReadAllLines(MissFile)) { var t = l.Trim(); if (t.Length > 0) Misses.Add(t); }
            string mapPath = Path.Combine(dir, "imgmap.json");
            if (File.Exists(mapPath))
            {
                string json = File.ReadAllText(mapPath);
                foreach (Match m in Regex.Matches(json, "\"([^\"]+)\"\\s*:\\s*\\{([^{}]*)\\}"))
                {
                    string body = m.Groups[2].Value;
                    var um = Regex.Match(body, "\"url\"\\s*:\\s*\"([^\"]+)\"");
                    if (!um.Success) continue;
                    Map[m.Groups[1].Value] = um.Groups[1].Value;
                    var hm = Regex.Match(body, "\"md5\"\\s*:\\s*\"([0-9a-f]{32})\"");
                    if (hm.Success) MapMd5[m.Groups[1].Value] = hm.Groups[1].Value;
                }
            }
            Log.LogInfo("JACARDS map loaded: " + Map.Count + " bundles (" + MapMd5.Count + " with md5)");
            try
            {
                var expected = new HashSet<string>();
                foreach (var kv in Map) expected.Add(CacheName(kv.Key, kv.Value));
                int swept = 0;
                foreach (var f in Directory.GetFiles(CacheDir, "*.png"))
                    if (!expected.Contains(Path.GetFileName(f))) { try { File.Delete(f); swept++; } catch { } }
                Log.LogInfo("JACARDS cache sweep: removed " + swept + " stale files, expected " + expected.Count);
            }
            catch (Exception e) { Log.LogWarning("cache sweep fail: " + e.Message); }
            var harmony = new Harmony("jp.ptcgl.jacards");
            int n = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type mm = null;
                try { mm = asm.GetType("TPCI.CardShaders.MaterialManager"); } catch { }
                if (mm == null) continue;
                foreach (var mi in mm.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (mi.Name == "UpdateMaterial" || mi.Name == "UpdateMaterialCo")
                    {
                        try { harmony.Patch(mi, prefix: new HarmonyMethod(typeof(JaCards), nameof(RegisterPrefix))); n++; } catch (Exception e) { Log.LogWarning("patch fail " + mi.Name + ": " + e.Message); }
                    }
                }
            }
            Log.LogInfo("JACARDS v2.4 patches: " + n);
            StartCoroutine(FixupLoop());
            StartCoroutine(SweepLoop());
        }

        public static void RegisterPrefix(object[] __args)
        {
            try
            {
                if (DexGate) return;
                if (__args == null || __args.Length == 0 || __args[0] == null) return;
                object req = __args[0];
                Type t = req.GetType();
                var fB = t.GetField("cardBundle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fR = t.GetField("meshRenderer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fB == null || fR == null) return;
                var bundle = fB.GetValue(req) as UnityEngine.Object;
                var rend = fR.GetValue(req) as MeshRenderer;
                if (bundle == null || rend == null) return;
                string key = bundle.name.ToLowerInvariant();
                if (!Map.ContainsKey(key)) return;
                Registered[rend.GetInstanceID()] = new KeyValuePair<string, float>(key, Time.time);
            }
            catch { }
        }

        private static bool TryGetJa(string bundle, out string url)
        {
            url = null;
            if (bundle == null) return false;
            return Map.TryGetValue(bundle, out url);
        }

        private IEnumerator FixupLoop()
        {
            var mpb = new MaterialPropertyBlock();
            while (true)
            {
                yield return new WaitForSeconds(0.25f);
                try
                {
                    if (DexGate) Registered.Clear();
                    var dead = new List<int>();
                    foreach (var kv in Registered)
                    {
                        if (Time.time - kv.Value.Value > 20f) { dead.Add(kv.Key); continue; }
                        FixupOne(kv.Key, kv.Value.Key, mpb);
                    }
                    foreach (var id in dead) Registered.Remove(id);
                }
                catch { }
            }
        }

        private static void FixupOne(int rendId, string bundle, MaterialPropertyBlock mpb)
        {
            if (DexGate) return;
            string url;
            if (!TryGetJa(bundle, out url)) return;
            var objs = Resources.FindObjectsOfTypeAll<MeshRenderer>();
            MeshRenderer rend = null;
            foreach (var o in objs) if (o.GetInstanceID() == rendId) { rend = o; break; }
            if (rend == null) return;
            rend.GetPropertyBlock(mpb);
            var cur = mpb.GetTexture(FaceTexId);
            if (cur == null) return;
            if (FaceCache.TryGetValue(bundle, out var jt) && jt == cur) return;
            string curBundle = ExtractBundle(cur.name);
            if (curBundle != null && curBundle != bundle)
            {
                // The game recycled this renderer for a different card
                // without a new registration (the detail view does this):
                // expire the stale registration instead of painting the
                // previous card's face over the new card.
                Registered[rendId] = new KeyValuePair<string, float>(bundle, -1000f);
                return;
            }
            EnsureTexture(bundle, url, tex =>
            {
                if (tex == null || rend == null || DexGate) return;
                tex = GetVariant(bundle, tex, false, 1024);
                if (tex == null) return;
                rend.GetPropertyBlock(mpb);
                var c2 = mpb.GetTexture(FaceTexId);
                if (c2 == null || c2 == tex) return;
                string c2Bundle = ExtractBundle(c2.name);
                if (c2Bundle != null && c2Bundle != bundle) return;
                mpb.SetTexture(FaceTexId, tex);
                rend.SetPropertyBlock(mpb);
                SwapCount++;
                Log.LogInfo("JACARDS SWAP #" + SwapCount + " " + bundle + " renderer=" + rend.gameObject.name);
            });
        }

        internal static string ExtractBundle(string texName)
        {
            if (string.IsNullOrEmpty(texName)) return null;
            var ms = BundleRx.Matches(texName.ToLowerInvariant());
            for (int i = ms.Count - 1; i >= 0; i--)
                if (Map.ContainsKey(ms[i].Value)) return ms[i].Value;
            return null;
        }

        private static bool DetectDex()
        {
            // The collection screen (CardDex) builds its UI from objects
            // named "CollectionHeader" / "CardDex_*". While it is open the
            // user asked for face swapping to pause entirely (temporary
            // measure against wrong cards shown in the dex).
            try
            {
                if (GameObject.Find("CollectionHeader") != null) return true;
                if (GameObject.Find("CardDex_CardCountStack") != null) return true;
            }
            catch { }
            return false;
        }

        private IEnumerator SweepLoop()
        {
            var mpb = new MaterialPropertyBlock();
            yield return new WaitForSeconds(2f);
            while (true)
            {
                try
                {
                    bool g = DetectDex();
                    if (g != DexGate) { DexGate = g; Log.LogInfo("JACARDS DEX-GATE " + (g ? "ON" : "OFF")); }
                }
                catch { }
                try
                {
                    if (!DexGate)
                    {
                    var rends = UnityEngine.Object.FindObjectsOfType<Renderer>();
                    foreach (var rend in rends)
                    {
                        if (rend == null || rend.gameObject == null) continue;
                        rend.GetPropertyBlock(mpb);
                        var cur = mpb.GetTexture(FaceTexId);
                        bool viaMpb = cur != null;
                        Material instMat = null;
                        if (cur == null)
                        {
                            var sm = rend.sharedMaterial;
                            if (sm == null || sm.shader == null || !sm.shader.name.StartsWith("TPCi/Cards3D")) continue;
                            instMat = rend.material;
                            if (instMat == null) continue;
                            cur = instMat.GetTexture(FaceTexId);
                            if (cur == null) continue;
                        }
                        if (!string.IsNullOrEmpty(cur.name) && SeenTexNames.Add("FACE:" + cur.name))
                            Log.LogInfo("JACARDS FACESEEN " + cur.name + " on " + rend.gameObject.name + (viaMpb ? " via MPB" : " via MAT"));
                        string bundle = ExtractBundle(cur.name);
                        if (bundle == null) continue;
                        string tname = cur.name.ToLowerInvariant();
                        bool isThumb = tname == bundle + "_t";
                        if (!isThumb && tname != bundle) continue;
                        int tsize = cur.width;
                        var vcache = isThumb ? ThumbCache : FaceCache;
                        if (vcache.TryGetValue(bundle, out var have) && have == cur) continue;
                        string url;
                        if (!TryGetJa(bundle, out url)) continue;
                        var rendRef = rend; string bRef = bundle; bool viaRef = viaMpb; var matRef = instMat;
                        bool thRef = isThumb; int tsRef = tsize;
                        EnsureTexture(bundle, url, tex =>
                        {
                            if (tex == null || rendRef == null || DexGate) return;
                            tex = GetVariant(bRef, tex, thRef, tsRef);
                            if (tex == null) return;
                            if (viaRef)
                            {
                                var mpb2 = new MaterialPropertyBlock();
                                rendRef.GetPropertyBlock(mpb2);
                                var c2 = mpb2.GetTexture(FaceTexId);
                                if (c2 == null || c2 == tex || ExtractBundle(c2.name) != bRef) return;
                                mpb2.SetTexture(FaceTexId, tex);
                                rendRef.SetPropertyBlock(mpb2);
                            }
                            else
                            {
                                if (matRef == null) return;
                                var c2 = matRef.GetTexture(FaceTexId);
                                if (c2 == null || c2 == tex || ExtractBundle(c2.name) != bRef) return;
                                matRef.SetTexture(FaceTexId, tex);
                            }
                            SweepSwapCount++;
                            Log.LogInfo("JACARDS SWEEP-SWAP #" + SweepSwapCount + " " + bRef + (viaRef ? " MPB" : " MAT") + (thRef ? " THUMB" : " FACE"));
                        });
                    }
                    var raws = UnityEngine.Object.FindObjectsOfType<RawImage>();
                    foreach (var ri in raws)
                    {
                        if (ri == null || ri.texture == null) continue;
                        var ct = ri.texture;
                        if (string.IsNullOrEmpty(ct.name)) continue;
                        string b2 = ExtractBundle(ct.name);
                        if (b2 == null) continue;
                        string tn2 = ct.name.ToLowerInvariant();
                        bool th2 = tn2 == b2 + "_t";
                        if (!th2 && tn2 != b2) continue;
                        var vc2 = th2 ? ThumbCache : FaceCache;
                        if (vc2.TryGetValue(b2, out var hv) && hv == ct) continue;
                        string url2;
                        if (!TryGetJa(b2, out url2)) continue;
                        var riRef = ri; string bb = b2; bool thh = th2; int tss = ct.width;
                        EnsureTexture(bb, url2, tex =>
                        {
                            if (tex == null || riRef == null || DexGate) return;
                            tex = GetVariant(bb, tex, thh, tss);
                            if (tex == null) return;
                            var c3 = riRef.texture;
                            if (c3 == null || c3 == tex || ExtractBundle(c3.name) != bb) return;
                            riRef.texture = tex;
                            SweepSwapCount++;
                            Log.LogInfo("JACARDS UI-SWAP #" + SweepSwapCount + " " + bb + (thh ? " THUMB" : " FACE"));
                        });
                    }
                    }
                }
                catch { }
                try
                {
                    var imgs = UnityEngine.Object.FindObjectsOfType<Image>();
                    foreach (var im in imgs)
                    {
                        if (im == null || im.sprite == null || im.sprite.texture == null) continue;
                        var st = im.sprite.texture;
                        if (UiSeen.Add("IMG:" + st.name + "|" + im.sprite.name))
                            Log.LogInfo("JACARDS UISEEN img tex=" + st.name + " sprite=" + im.sprite.name + " " + st.width + "x" + st.height + " go=" + im.gameObject.name);
                    }
                    var raws2 = UnityEngine.Object.FindObjectsOfType<RawImage>();
                    foreach (var ri in raws2)
                    {
                        if (ri == null || ri.texture == null || string.IsNullOrEmpty(ri.texture.name)) continue;
                        if (UiSeen.Add("RAW:" + ri.texture.name))
                            Log.LogInfo("JACARDS UISEEN raw tex=" + ri.texture.name + " " + ri.texture.width + "x" + ri.texture.height + " go=" + ri.gameObject.name);
                    }
                }
                catch { }
                yield return new WaitForSeconds(0.5f);
            }
        }


        private static byte LerpB(byte a, byte b, float t) { return (byte)Mathf.Clamp(Mathf.RoundToInt(a + (b - a) * t), 0, 255); }

        // Rebuild a downloaded card image into the game's own texture layout:
        // 1024x1024 white canvas, card stretched into x in [110.5, 913], full height
        // (measured from the game's bundle textures). Already-1024 textures pass through.
        internal static Texture2D GetVariant(string bundle, Texture2D raw, bool thumb, int size)
        {
            if (raw == null) return null;
            var cache = thumb ? ThumbCache : FaceCache;
            if (cache.TryGetValue(bundle, out var v) && v != null) return v;
            v = thumb ? ComposeThumb(raw, size) : ComposeCanvas(raw);
            if (v != null) cache[bundle] = v;
            return v;
        }

        internal static Texture2D ComposeCanvas(Texture2D src)
        {
            if (src == null) return null;
            if (src.width == 1024 && src.height == 1024) return src;
            return ComposeCore(src, 1024, 0.10791f, 0.78369f);
        }

        // Thumbnail textures (bundle + "_t", 256x256) hold the standalone card
        // stretched over the full square, so the replacement uses the same layout.
        internal static Texture2D ComposeThumb(Texture2D src, int size)
        {
            if (src == null) return null;
            if (size <= 0) size = 256;
            return ComposeCore(src, size, 0f, 1f);
        }

        internal static Texture2D ComposeCore(Texture2D src, int S, float FX0, float FW)
        {
            if (src == null) return null;
            try
            {
                var sp = src.GetPixels32();
                int sw = src.width, sh = src.height;
                int minx = sw, miny = sh, maxx = -1, maxy = -1;
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                    {
                        var c = sp[y * sw + x];
                        int sat = Math.Abs((int)c.r - c.b) + Math.Abs((int)c.r - c.g);
                        if (sat > 26 || c.r < 235)
                        { if (x < minx) minx = x; if (x > maxx) maxx = x; if (y < miny) miny = y; if (y > maxy) maxy = y; }
                    }
                if (maxx < 0) { minx = 0; miny = 0; maxx = sw - 1; maxy = sh - 1; }
                int cw = maxx - minx + 1, ch = maxy - miny + 1;
                int dx0 = Mathf.RoundToInt(FX0 * S), dw = Mathf.RoundToInt(FW * S);
                var dst = new Color32[S * S];
                var white = new Color32(255, 255, 255, 255);
                for (int i = 0; i < dst.Length; i++) dst[i] = white;
                for (int dy = 0; dy < S; dy++)
                {
                    float syf = miny + (dy + 0.5f) / S * ch - 0.5f;
                    int y0 = Mathf.Clamp(Mathf.FloorToInt(syf), miny, maxy);
                    int y1 = Mathf.Min(y0 + 1, maxy);
                    float fy = Mathf.Clamp(syf - y0, 0f, 1f);
                    int rowD = dy * S, row0 = y0 * sw, row1 = y1 * sw;
                    for (int dx = dx0; dx < dx0 + dw; dx++)
                    {
                        float sxf = minx + (dx - dx0 + 0.5f) / dw * cw - 0.5f;
                        int x0 = Mathf.Clamp(Mathf.FloorToInt(sxf), minx, maxx);
                        int x1 = Mathf.Min(x0 + 1, maxx);
                        float fx = Mathf.Clamp(sxf - x0, 0f, 1f);
                        var c00 = sp[row0 + x0]; var c10 = sp[row0 + x1];
                        var c01 = sp[row1 + x0]; var c11 = sp[row1 + x1];
                        dst[rowD + dx] = new Color32(
                            LerpB(LerpB(c00.r, c10.r, fx), LerpB(c01.r, c11.r, fx), fy),
                            LerpB(LerpB(c00.g, c10.g, fx), LerpB(c01.g, c11.g, fx), fy),
                            LerpB(LerpB(c00.b, c10.b, fx), LerpB(c01.b, c11.b, fx), fy),
                            255);
                    }
                }
                var canvas = new Texture2D(S, S, TextureFormat.RGBA32, false);
                canvas.SetPixels32(dst);
                canvas.Apply(false, false);
                return canvas;
            }
            catch { return src; }
        }

        internal static string CacheName(string bundle, string url)
        {
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(url));
                var sb = new StringBuilder();
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return bundle + "__" + sb.ToString().Substring(0, 10) + ".png";
            }
        }

        internal static void EnsureTexture(string bundle, string url, Action<Texture2D> done)
        {
            if (JaTextures.TryGetValue(bundle, out var t)) { done(t); return; }
            if (Misses.Contains(bundle)) { done(null); return; }
            if (Loading.Contains(bundle)) { done(null); return; }
            Loading.Add(bundle);
            Instance.StartCoroutine(LoadTexture(bundle, url, done));
        }

        private static JaCards Instance;
        private void Start() { Instance = this; }

        internal static string Md5Hex(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(data);
                var sb = new StringBuilder();
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        internal static string Md5FileHex(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var md5 = MD5.Create())
            {
                var h = md5.ComputeHash(fs);
                var sb = new StringBuilder();
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static IEnumerator LoadTexture(string bundle, string url, Action<Texture2D> done)
        {
            string path = Path.Combine(CacheDir, CacheName(bundle, url));
            string expected;
            MapMd5.TryGetValue(bundle, out expected);
            if (File.Exists(path) && expected != null)
            {
                bool okFile = false;
                try { okFile = Md5FileHex(path) == expected; } catch { }
                if (!okFile)
                {
                    Log.LogInfo("JACARDS cache hash mismatch, refetching " + bundle);
                    try { File.Delete(path); } catch { }
                }
            }
            if (!File.Exists(path))
            {
                using (var req = UnityWebRequest.Get(url))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Misses.Add(bundle);
                        try { File.AppendAllText(MissFile, bundle + "\n"); } catch { }
                        Loading.Remove(bundle);
                        Log.LogInfo("JACARDS no image for " + bundle);
                        done(null);
                        yield break;
                    }
                    var data = req.downloadHandler.data;
                    if (expected != null && Md5Hex(data) != expected)
                    {
                        // The served bytes are not the verified image: never
                        // cache or display them. Session-only miss so the
                        // next session retries.
                        Log.LogWarning("JACARDS HASHMISMATCH " + bundle + " expected " + expected + " got " + Md5Hex(data));
                        Misses.Add(bundle);
                        Loading.Remove(bundle);
                        done(null);
                        yield break;
                    }
                    try
                    {
                        string tmpPath = path + ".tmp";
                        File.WriteAllBytes(tmpPath, data);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmpPath, path);
                    }
                    catch { }
                }
            }
            using (var req = UnityWebRequestTexture.GetTexture("file:///" + path.Replace('\\', '/')))
            {
                yield return req.SendWebRequest();
                Loading.Remove(bundle);
                if (req.result != UnityWebRequest.Result.Success) { done(null); yield break; }
                var tex = DownloadHandlerTexture.GetContent(req);
                JaTextures[bundle] = tex;
                done(tex);
            }
        }
    }
}
