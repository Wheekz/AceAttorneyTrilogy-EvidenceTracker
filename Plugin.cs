using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace PWAAT_QoL
{
    /// <summary>
    /// Enquête : marque dans le dossier les preuves/profils déjà présentés au personnage en face.
    ///  - même réponse que la dernière fois  -> icône estompée + « (déjà présenté) »
    ///  - réponse différente (nouveau dialogue débloqué) -> icône dorée + « (nouvelle réaction !) »
    /// La réponse est calculée sans la déclencher via GSMain_Status.tantei_show_check (fonction pure).
    /// </summary>
    [BepInPlugin("wheekz.pwaat.qol", "PWAAT QoL", "0.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyboardShortcut> ResetKey;
        internal static PresentHistory History;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("Enquête", "MarquerPresentations", true, "Marque les preuves/profils déjà présentés au personnage courant.");
            Verbose = Config.Bind("Debug", "Journal", false, "Journalise l'état du jeu à l'ouverture du dossier, à chaque présentation, sauvegarde et chargement.");
            ResetKey = Config.Bind("Enquête", "ToucheRemiseAZero", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
                "Efface l'historique des présentations du chapitre en cours (pour la partie en cours).");
            History = new PresentHistory(Path.Combine(Paths.ConfigPath, "pwaat_qol"));
            new Harmony("wheekz.pwaat.qol").PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("Chargé.");
        }

        private void Update()
        {
            if (ResetKey.Value.IsDown())
            {
                int n = History.ResetChapter(Ctx.ChapterPrefix());
                Log.LogInfo($"Historique du chapitre effacé ({n} entrées).");
            }
        }
    }

    internal enum Mark { None, Seen, New }

    /// <summary>État courant du jeu utile au mod.</summary>
    internal static class Ctx
    {
        /// <summary>Vrai quand le dossier est ouvert pour présenter quelque chose à un personnage en enquête.</summary>
        /// Évalué en direct : playRecord() appelle stopRecord() à l'ouverture, un indicateur posé avant serait perdu.
        internal static bool PresentingInInvestigation => GSStatic.global_work_.r.no_3 == 2 && Character > 0;
        internal static bool Previewing;

        internal static int Character => AnimationSystem.Instance != null ? AnimationSystem.Instance.IdlingCharacterMasked : -1;

        internal static string ChapterPrefix()
        {
            var g = GSStatic.global_work_;
            return $"{g.title}|{g.scenario}|";
        }

        /// Pas de lieu dans la clé : un perso qui change d'endroit garde son historique,
        /// et une réponse différente ailleurs s'affiche en « nouveau » (cas réel : Maya, GS1 ch.2).
        internal static string Key(int item, int kind) => $"{ChapterPrefix()}{Character}|{kind}|{item}";

        internal static Mark Evaluate(int noteId, int kind, out uint response)
        {
            response = 0;
            if (!Plugin.Enabled.Value || !PresentingInInvestigation || Character <= 0) return Mark.None;
            try
            {
                Previewing = true;
                response = GSMain_Status.tantei_show_check(GSStatic.global_work_, (uint)noteId, (byte)kind);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Aperçu impossible : " + e.Message);
                return Mark.None;
            }
            finally { Previewing = false; }
            var seen = Plugin.History.Get(Key(noteId, kind));
            if (seen == null || seen.Count == 0) return Mark.None;
            return seen.Contains(response) ? Mark.Seen : Mark.New;
        }
    }

    /// <summary>
    /// Historique lié à la partie : tenu en mémoire pendant le jeu, écrit à côté de l'emplacement
    /// lors d'une sauvegarde, relu au chargement, vidé à l'écran titre (nouvelle partie = vierge).
    /// Clé -> réponses (identifiants de message) déjà vues.
    /// </summary>
    internal class PresentHistory
    {
        private readonly string _dir;
        private Dictionary<string, HashSet<uint>> _data = new Dictionary<string, HashSet<uint>>();
        public int Count => _data.Count;

        public PresentHistory(string dir)
        {
            _dir = dir;
            Directory.CreateDirectory(dir);
        }

        private string SlotFile(int slot) => Path.Combine(_dir, $"slot_{slot}.txt");

        public HashSet<uint> Get(string key) => _data.TryGetValue(key, out var s) ? s : null;

        public void Add(string key, uint message)
        {
            if (!_data.TryGetValue(key, out var set)) _data[key] = set = new HashSet<uint>();
            set.Add(message);
        }

        public void Clear() => _data = new Dictionary<string, HashSet<uint>>();

        public int ResetChapter(string prefix)
        {
            var keys = new List<string>();
            foreach (var k in _data.Keys) if (k.StartsWith(prefix)) keys.Add(k);
            foreach (var k in keys) _data.Remove(k);
            return keys.Count;
        }

        public void SaveSlot(int slot)
        {
            var lines = new List<string>();
            foreach (var kv in _data)
            {
                var msgs = new List<string>();
                foreach (var m in kv.Value) msgs.Add(m.ToString());
                lines.Add(kv.Key + "=" + string.Join(",", msgs.ToArray()));
            }
            File.WriteAllLines(SlotFile(slot), lines.ToArray());
        }

        public void LoadSlot(int slot)
        {
            Clear();
            string path = SlotFile(slot);
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.LastIndexOf('=');
                if (eq <= 0) continue;
                var set = new HashSet<uint>();
                foreach (var m in line.Substring(eq + 1).Split(','))
                    if (uint.TryParse(m, out var v)) set.Add(v);
                _data[line.Substring(0, eq)] = set;
            }
        }
    }

    /// <summary>Marques affichées, dans la langue du jeu.</summary>
    internal static class Labels
    {
        private static readonly Dictionary<Language, string[]> Texts = new Dictionary<Language, string[]>
        {
            { Language.JAPAN,   new[] { "(提示済み)",     "(新しい反応！)" } },
            { Language.USA,     new[] { "(shown)",        "(new!)" } },
            { Language.FRANCE,  new[] { "(déjà vu)",      "(nouveau !)" } },
            { Language.GERMAN,  new[] { "(gezeigt)",      "(neu!)" } },
            { Language.KOREA,   new[] { "(제시함)",        "(새 반응!)" } },
            { Language.CHINA_S, new[] { "(已出示)",       "(新反应！)" } },
            { Language.CHINA_T, new[] { "(已出示)",       "(新反應！)" } },
            { Language.Pt_BR,   new[] { "(já mostrado)",  "(novo!)" } },
            { Language.ES_419,  new[] { "(ya mostrado)",  "(¡nuevo!)" } },
        };

        internal static string Get(Mark mark)
        {
            if (!Texts.TryGetValue(GSStatic.global_work_.language, out var t)) t = Texts[Language.USA];
            return mark == Mark.Seen ? t[0] : t[1];
        }
    }

    // ---------- Historique lié aux sauvegardes ----------

    [HarmonyPatch(typeof(SaveControl), nameof(SaveControl.SaveGameDataRequest))]
    internal static class Patch_Save
    {
        private static void Postfix(int slot)
        {
            Plugin.History.SaveSlot(slot);
            if (Plugin.Verbose.Value) Plugin.Log.LogInfo($"[sauvegarde] emplacement {slot} : {Plugin.History.Count} entrées");
        }
    }

    [HarmonyPatch(typeof(SaveControl), nameof(SaveControl.LoadGameData))]
    internal static class Patch_Load
    {
        private static void Postfix(int slot, bool __result)
        {
            if (!__result) return;
            Plugin.History.LoadSlot(slot);
            if (Plugin.Verbose.Value) Plugin.Log.LogInfo($"[chargement] emplacement {slot} : {Plugin.History.Count} entrées");
        }
    }

    [HarmonyPatch(typeof(mainTitleCtrl), nameof(mainTitleCtrl.Play))]
    internal static class Patch_Title
    {
        private static void Postfix()
        {
            Plugin.History.Clear();
            if (Plugin.Verbose.Value) Plugin.Log.LogInfo("[écran titre] historique vidé");
        }
    }

    // ---------- Détection du mode « présenter en enquête » ----------

    [HarmonyPatch(typeof(recordListCtrl), nameof(recordListCtrl.noteOpen))]
    internal static class Patch_NoteOpen
    {
        private static void Prefix(int mode)
        {
            var g = GSStatic.global_work_;
            var sw = advCtrl.instance != null ? advCtrl.instance.sub_window_ : null;
            if (Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"[dossier ouvert] mode={mode} r.no_0..3={g.r.no_0},{g.r.no_1},{g.r.no_2},{g.r.no_3} " +
                                   $"req={(sw != null ? sw.req_.ToString() : "?")} perso={Ctx.Character} titre={g.title} chap={g.scenario} " +
                                   $"lieu={g.Room} -> enquête/présentation={Ctx.PresentingInInvestigation}");
        }
    }

    // ---------- Enregistrement des présentations réelles ----------

    [HarmonyPatch(typeof(GSMain_Status), nameof(GSMain_Status.tantei_show_check))]
    internal static class Patch_ShowCheck
    {
        private static void Postfix(uint item, byte itorhum, uint __result)
        {
            if (Ctx.Previewing) return;
            string key = Ctx.Key((int)item, itorhum);
            var before = Plugin.History.Get(key);
            string verdict = before == null ? "première fois" : (before.Contains(__result) ? "déjà vue" : "NOUVELLE réponse");
            Plugin.History.Add(key, __result);
            if (Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"[présentation] {key} -> message {__result} ({verdict})");
        }
    }

    // ---------- Affichage ----------

    [HarmonyPatch(typeof(recordListCtrl), "entryRecord")]
    internal static class Patch_EntryRecord
    {
        private static readonly AccessTools.FieldRef<recordListCtrl, List<recordListCtrl.RecordIcon>> DispList =
            AccessTools.FieldRefAccess<recordListCtrl, List<recordListCtrl.RecordIcon>>("record_disp_list_");
        private static readonly AccessTools.FieldRef<recordListCtrl, List<recordListCtrl.RecordIcon>> SubList =
            AccessTools.FieldRefAccess<recordListCtrl, List<recordListCtrl.RecordIcon>>("record_sub_list_");

        private static void Postfix(recordListCtrl __instance, int in_id, int in_no, bool is_sub)
        {
            var list = is_sub ? SubList(__instance) : DispList(__instance);
            if (list == null || in_no < 0 || in_no >= list.Count) return;
            var mark = Ctx.Evaluate(in_id, __instance.record_type, out _);
            list[in_no].icon_.color = Color.white; // annule la teinte des versions précédentes
            Badges.Show(list[in_no].icon_, mark);
        }
    }

    /// <summary>Pastilles superposées au coin haut-droit des icônes : coche verte (déjà vu) / étoile (nouveau).</summary>
    internal static class Badges
    {
        private const string Name = "QoLBadge";
        private const float SizeRatio = 0.42f;
        private static Sprite _check, _star;

        internal static void Show(AssetBundleSprite icon, Mark mark)
        {
            var host = icon.obj;
            if (host == null) return;
            var existing = host.transform.Find(Name);
            if (mark == Mark.None)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }
            var sprite = mark == Mark.Seen ? (_check ?? (_check = Draw(DrawCheck))) : (_star ?? (_star = Draw(DrawStar)));
            try
            {
                if (icon.sprite_renderer_ != null) ShowOnRenderer(icon.sprite_renderer_, existing, sprite);
                else if (icon.image_ != null) ShowOnImage(icon.image_, existing, sprite);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Pastille : " + e.Message); }
        }

        private static void ShowOnRenderer(SpriteRenderer host, Transform existing, Sprite sprite)
        {
            if (host.sprite == null) { if (existing != null) existing.gameObject.SetActive(false); return; }
            GameObject go;
            SpriteRenderer sr;
            if (existing == null)
            {
                go = new GameObject(Name);
                go.layer = host.gameObject.layer;
                go.transform.SetParent(host.transform, false);
                sr = go.AddComponent<SpriteRenderer>();
            }
            else { go = existing.gameObject; sr = go.GetComponent<SpriteRenderer>(); }
            sr.sprite = sprite;
            sr.sortingLayerID = host.sortingLayerID;
            sr.sortingOrder = host.sortingOrder + 10;
            sr.maskInteraction = host.maskInteraction;
            // Taille/position en coordonnées locales de l'icône (le sprite de pastille fait 1 unité).
            var b = host.sprite.bounds;
            float size = Mathf.Min(b.size.x, b.size.y) * SizeRatio;
            go.transform.localScale = new Vector3(size, size, 1f);
            go.transform.localPosition = new Vector3(b.max.x - size * 0.4f, b.max.y - size * 0.4f, -0.01f);
            go.SetActive(true);
        }

        private static void ShowOnImage(Image host, Transform existing, Sprite sprite)
        {
            GameObject go;
            Image img;
            if (existing == null)
            {
                go = new GameObject(Name, typeof(RectTransform));
                go.layer = host.gameObject.layer;
                go.transform.SetParent(host.transform, false);
                img = go.AddComponent<Image>();
                img.raycastTarget = false;
            }
            else { go = existing.gameObject; img = go.GetComponent<Image>(); }
            img.sprite = sprite;
            var parent = host.rectTransform.rect;
            float size = Mathf.Min(parent.width, parent.height) * SizeRatio;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = new Vector2(-size * 0.4f, -size * 0.4f);
            go.transform.SetAsLastSibling();
            go.SetActive(true);
        }

        // ---------- Dessin procédural (64 px, bords adoucis) ----------

        private const int Px = 64;

        private static Sprite Draw(Func<float, float, Color> shader)
        {
            var tex = new Texture2D(Px, Px, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[Px * Px];
            for (int y = 0; y < Px; y++)
                for (int x = 0; x < Px; x++)
                    pixels[y * Px + x] = shader((x + 0.5f) / Px, (y + 0.5f) / Px);
            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, Px, Px), new Vector2(0.5f, 0.5f), Px);
        }

        private static float Cover(float signedDist) => Mathf.Clamp01(0.5f - signedDist * Px); // 1 px d'adoucissement

        private static Color Over(Color top, float a, Color under)
        {
            float outA = a + under.a * (1 - a);
            if (outA <= 0f) return new Color(0, 0, 0, 0);
            Color c = (top * a + under * under.a * (1 - a)) / outA;
            c.a = outA;
            return c;
        }

        private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        private static Color DrawCheck(float x, float y)
        {
            var p = new Vector2(x, y);
            float d = (p - new Vector2(0.5f, 0.5f)).magnitude;
            var c = new Color(0, 0, 0, 0);
            c = Over(new Color(1f, 1f, 1f), Cover(d - 0.48f), c);                          // liseré blanc
            c = Over(new Color(0.16f, 0.66f, 0.29f), Cover(d - 0.42f), c);                 // disque vert
            float stroke = Mathf.Min(SegDist(p, new Vector2(0.27f, 0.51f), new Vector2(0.44f, 0.34f)),
                                     SegDist(p, new Vector2(0.44f, 0.34f), new Vector2(0.74f, 0.67f)));
            c = Over(Color.white, Cover(stroke - 0.065f), c);                                // coche
            return c;
        }

        private static readonly Vector2[] StarPts = BuildStar();

        private static Vector2[] BuildStar()
        {
            var pts = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float r = i % 2 == 0 ? 0.47f : 0.21f;
                float ang = Mathf.PI / 2f + i * Mathf.PI / 5f;
                pts[i] = new Vector2(0.5f + r * Mathf.Cos(ang), 0.47f + r * Mathf.Sin(ang));
            }
            return pts;
        }

        private static Color DrawStar(float x, float y)
        {
            var p = new Vector2(x, y);
            bool inside = false;
            float edge = float.MaxValue;
            for (int i = 0, j = StarPts.Length - 1; i < StarPts.Length; j = i++)
            {
                var a = StarPts[i]; var b = StarPts[j];
                if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                edge = Mathf.Min(edge, SegDist(p, a, b));
            }
            float sd = inside ? -edge : edge;
            var c = new Color(0, 0, 0, 0);
            c = Over(new Color(0.45f, 0.24f, 0.02f), Cover(sd - 0.035f), c);               // contour brun
            c = Over(new Color(1f, 0.80f, 0.15f), Cover(sd + 0.01f), c);                   // étoile dorée
            return c;
        }
    }

    [HarmonyPatch(typeof(recordListCtrl), "cursorRecord")]
    internal static class Patch_CursorRecord
    {
        private static readonly AccessTools.FieldRef<recordListCtrl, Text> IconName =
            AccessTools.FieldRefAccess<recordListCtrl, Text>("icon_name_");

        private static HorizontalWrapMode? _originalWrap;

        private static void Postfix(recordListCtrl __instance)
        {
            var label = IconName(__instance);
            if (label == null) return;
            if (_originalWrap == null) _originalWrap = label.horizontalOverflow;
            var mark = Ctx.Evaluate(__instance.note_id, __instance.record_type, out uint resp);
            // Une seule ligne quand on ajoute une marque : sinon le suffixe passe à la ligne et il est masqué.
            label.horizontalOverflow = mark == Mark.None ? _originalWrap.Value : HorizontalWrapMode.Overflow;
            // Couleurs foncées : lisibles sur le bandeau jaune du nom.
            if (mark == Mark.Seen) label.text += " <color=#2f6e3a>" + Labels.Get(mark) + "</color>";
            else if (mark == Mark.New) label.text += " <color=#a3301b>" + Labels.Get(mark) + "</color>";
            if (Plugin.Verbose.Value && Ctx.PresentingInInvestigation)
                Plugin.Log.LogInfo($"[curseur] objet={__instance.note_id} type={__instance.record_type} -> réponse {resp} ({mark})");
        }
    }
}
