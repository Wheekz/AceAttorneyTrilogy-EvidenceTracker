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
    [BepInPlugin("wheekz.pwaat.qol", "PWAAT QoL", "0.4.0")]
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
                int n = History.ResetChapter(Ctx.CasePrefix(), Chapters.Base(Chapters.StemOfScenario(GSStatic.global_work_.title, GSStatic.global_work_.scenario)));
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

        /// L'historique vaut pour toute l'affaire : d'un jour d'enquête à l'autre, on ne re-présente pas tout.
        internal static string CasePrefix()
        {
            var g = GSStatic.global_work_;
            return $"{g.title}|{Chapters.CaseId(g.title, g.scenario)}|";
        }

        /// Pas de lieu dans la clé : un perso qui change d'endroit garde son historique,
        /// et une réponse différente ailleurs s'affiche en « nouveau » (cas réel : Maya, GS1 ch.2).
        internal static string Key(int item, int kind) => $"{CasePrefix()}{Character}|{kind}|{item}";

        /// Le jeu remplace une preuve mise à jour par une autre fiche (même nom, même icône, autre identifiant) :
        /// l'historique se lit donc sur toutes les versions de la preuve.
        internal static HashSet<string> SeenReplies(int noteId, int kind)
        {
            HashSet<string> all = null;
            foreach (var id in EvidenceVersions.Of(noteId))
            {
                var seen = Plugin.History.Get(Key(id, kind));
                if (seen == null) continue;
                if (all == null) all = new HashSet<string>();
                all.UnionWith(seen);
            }
            return all;
        }

        /// Une réponse se désigne par son fichier de messages (sans la langue) et son numéro :
        /// les numéros sont identiques dans toutes les langues, et ils repartent à chaque fichier.
        internal static string ReplyId(uint message)
        {
            string stem = message >= 128 ? Chapters.Stem(GSStatic.message_work_.mdt_path) : "sys";
            return $"{stem}:{message}";
        }

        /// Vrai si la table contient une réponse propre à cet objet, faux si le personnage répond par défaut.
        /// Mêmes parcours et mêmes conditions que tantei_show_check, sans le cas « n'importe quel objet ».
        internal static bool HasDedicatedReply(uint item, int kind)
        {
            var g = GSStatic.global_work_;
            var table = GSScenario.GetTanteiShowData();
            int character = Character;
            int i = 0;
            if (g.title == TitleId.GS1)
            {
                for (; i < table.Length && table[i].end != 255; i++)
                    if (character == table[i].pl_id && g.Room == table[i].room && item == table[i].item) return true;
                return false;
            }
            for (; i < table.Length && table[i].end != 255 && table[i].room != g.Room; i++) { }
            for (; i < table.Length && table[i].room == g.Room; i++)
            {
                var row = table[i];
                if (character == row.pl_id && kind == row.end && item == row.item &&
                    (g.roomseq[g.Room] == row.roomseq || row.roomseq == 255)) return true;
            }
            return false;
        }

        /// Avec des verrous actifs sur ce personnage, le jeu ne consulte pas tantei_show_check pour le Magatama :
        /// il lance le verrou psychique. C'est donc bien une réaction à tenter.
        private static bool MagatamaOpensLock(int noteId)
        {
            return IsMagatama((byte)noteId) && GSPsylock.is_on_psylock_flag_in_room((ushort)GSStatic.global_work_.Room, (ushort)Character) >= 0;
        }

        private static readonly System.Reflection.MethodInfo IsMagatamaMethod = AccessTools.Method(typeof(GSMain_Status), "IsMagatama");

        private static bool IsMagatama(byte item) => (bool)IsMagatamaMethod.Invoke(null, new object[] { item });

        internal static Mark Evaluate(int noteId, int kind, out uint response)
        {
            response = 0;
            if (!Plugin.Enabled.Value || !PresentingInInvestigation || Character <= 0) return Mark.None;
            // Pendant le bris des verrous, le jeu compare aux réponses du verrou, pas à tantei_show_check.
            if (GSStatic.global_work_.psy_menu_active_flag != 0) return Mark.None;
            if (MagatamaOpensLock(noteId)) return Mark.New;
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
            var seen = SeenReplies(noteId, kind);
            if (seen == null || seen.Count == 0) return Mark.None;
            // Réponse par défaut : le personnage n'a rien à dire sur cet objet, même si le jeu reformule son refus.
            if (!HasDedicatedReply((uint)noteId, kind)) return Mark.Seen;
            return ReplyCoverage.IsCovered(GSStatic.global_work_.title, ReplyId(response), seen) ? Mark.Seen : Mark.New;
        }
    }

    /// <summary>Découpage du jeu : un fichier de messages par chapitre, regroupés par affaire (sc2_0, sc2_2b...).</summary>
    internal static class Chapters
    {
        /// "GS3/scenario/sc2_2b_text_f.mdt" -> "sc2_2b"
        internal static string Stem(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string name = Path.GetFileName(path);
            int cut = name.IndexOf("_text", StringComparison.Ordinal);
            return cut > 0 ? name.Substring(0, cut) : name;
        }

        /// "sc2_2b" -> "sc2_2" : un même chapitre est parfois réparti sur plusieurs fichiers.
        internal static string Base(string stem)
        {
            if (stem.Length > 1 && char.IsLetter(stem[stem.Length - 1]) && char.IsDigit(stem[stem.Length - 2]))
                return stem.Substring(0, stem.Length - 1);
            return stem;
        }

        internal static string StemOfScenario(TitleId title, int scenario)
        {
            var table = GSScenario.GetScenarioMdtPathTable(title);
            return table != null && scenario >= 0 && scenario < table.Length ? Stem(table[scenario]) : $"sc?{scenario}";
        }

        /// "sc2_2b" -> "c2"
        internal static string CaseId(TitleId title, int scenario)
        {
            string stem = StemOfScenario(title, scenario);
            int end = stem.IndexOf('_');
            string number = stem.StartsWith("sc") ? (end > 2 ? stem.Substring(2, end - 2) : stem.Substring(2)) : stem;
            return "c" + number;
        }
    }

    /// <summary>
    /// Table précalculée (ReplyCoverage.cs) : « qui a lu la réponse A n'apprend rien de neuf en lisant la réponse B ».
    /// Elle ne contient que des identifiants, donc elle vaut pour toutes les langues du jeu.
    /// </summary>
    internal static class ReplyCoverage
    {
        private static readonly Dictionary<TitleId, Dictionary<string, List<string>>> Tables =
            new Dictionary<TitleId, Dictionary<string, List<string>>>();

        private static Dictionary<string, List<string>> TableOf(TitleId title)
        {
            if (Tables.TryGetValue(title, out var table)) return table;
            table = new Dictionary<string, List<string>>();
            string data = title == TitleId.GS1 ? ReplyCoverageData.GS1 : title == TitleId.GS2 ? ReplyCoverageData.GS2 : ReplyCoverageData.GS3;
            foreach (var link in data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int sep = link.IndexOf('>');
                if (sep <= 0) continue;
                string covered = link.Substring(sep + 1);
                if (!table.TryGetValue(covered, out var sources)) table[covered] = sources = new List<string>();
                sources.Add(link.Substring(0, sep));
            }
            return Tables[title] = table;
        }

        /// Les présentations des anciennes versions du mod ne connaissent que le chapitre, pas le fichier exact :
        /// elles sont enregistrées sous la forme "sc2_2*:283".
        private static string LegacyForm(string replyId)
        {
            int sep = replyId.IndexOf(':');
            return sep <= 0 ? replyId : Chapters.Base(replyId.Substring(0, sep)) + "*" + replyId.Substring(sep);
        }

        private static bool Known(string replyId, HashSet<string> seen) => seen.Contains(replyId) || seen.Contains(LegacyForm(replyId));

        internal static bool IsCovered(TitleId title, string replyId, HashSet<string> seen)
        {
            if (Known(replyId, seen)) return true;
            if (!TableOf(title).TryGetValue(replyId, out var sources)) return false;
            foreach (var source in sources)
                if (Known(source, seen)) return true;
            return false;
        }
    }

    /// <summary>Fiches du jeu qui sont les versions d'une même preuve (même nom, même icône, même type).</summary>
    internal static class EvidenceVersions
    {
        private static readonly Dictionary<int, int[]> Cache = new Dictionary<int, int[]>();
        private static string _cacheOwner;

        internal static int[] Of(int noteId)
        {
            var g = GSStatic.global_work_;
            string owner = $"{g.title}|{g.language}";
            if (owner != _cacheOwner) { Cache.Clear(); _cacheOwner = owner; }
            if (Cache.TryGetValue(noteId, out var versions)) return versions;

            var notes = piceDataCtrl.instance.note_data;
            if (noteId < 0 || noteId >= notes.Count) return new[] { noteId };
            var note = notes[noteId];
            string name = note.name;
            var found = new List<int>();
            for (int i = 0; i < notes.Count; i++)
                if (notes[i].type == note.type && notes[i].file_id == note.file_id && notes[i].name == name)
                    found.Add(i);
            versions = found.ToArray();
            foreach (var id in versions) Cache[id] = versions;
            return versions;
        }
    }

    /// <summary>
    /// Historique lié à la partie : tenu en mémoire pendant le jeu, écrit à côté de l'emplacement
    /// lors d'une sauvegarde, relu au chargement, vidé à l'écran titre (nouvelle partie = vierge).
    /// Clé (jeu|affaire|perso|type|objet) -> réponses déjà vues (fichier:numéro).
    /// </summary>
    internal class PresentHistory
    {
        private readonly string _dir;
        private Dictionary<string, HashSet<string>> _data = new Dictionary<string, HashSet<string>>();
        public int Count => _data.Count;

        public PresentHistory(string dir)
        {
            _dir = dir;
            Directory.CreateDirectory(dir);
        }

        private string SlotFile(int slot) => Path.Combine(_dir, $"slot_{slot}.txt");

        public HashSet<string> Get(string key) => _data.TryGetValue(key, out var s) ? s : null;

        public void Add(string key, string reply)
        {
            if (!_data.TryGetValue(key, out var set)) _data[key] = set = new HashSet<string>();
            set.Add(reply);
        }

        public void Clear() => _data = new Dictionary<string, HashSet<string>>();

        /// Retire seulement ce qui a été vu dans le chapitre courant : les autres jours de l'affaire restent connus.
        public int ResetChapter(string casePrefix, string chapterBase)
        {
            int removed = 0;
            var emptied = new List<string>();
            foreach (var kv in _data)
            {
                if (!kv.Key.StartsWith(casePrefix)) continue;
                removed += kv.Value.RemoveWhere(reply => InChapter(reply, chapterBase));
                if (kv.Value.Count == 0) emptied.Add(kv.Key);
            }
            foreach (var k in emptied) _data.Remove(k);
            return removed;
        }

        private static bool InChapter(string reply, string chapterBase)
        {
            int sep = reply.IndexOf(':');
            if (sep <= 0) return false;
            return Chapters.Base(reply.Substring(0, sep).TrimEnd('*')) == chapterBase;
        }

        public void SaveSlot(int slot)
        {
            var lines = new List<string>();
            foreach (var kv in _data)
            {
                var replies = new string[kv.Value.Count];
                kv.Value.CopyTo(replies);
                lines.Add(kv.Key + "=" + string.Join(",", replies));
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
                string key = line.Substring(0, eq);
                string chapterBase = MigrateKey(ref key);
                foreach (var value in line.Substring(eq + 1).Split(','))
                {
                    if (value.Length == 0) continue;
                    Add(key, chapterBase != null ? $"{chapterBase}*:{value}" : value);
                }
            }
        }

        /// Ancien format "GS3|9|6|0|115=283" (par chapitre) -> "GS3|c2|6|0|115" + réponse "sc2_2*:283".
        /// Renvoie le chapitre d'origine, ou null si la ligne est déjà au format actuel.
        private static string MigrateKey(ref string key)
        {
            var parts = key.Split('|');
            if (parts.Length != 5 || !int.TryParse(parts[1], out int scenario)) return null;
            TitleId title;
            try { title = (TitleId)Enum.Parse(typeof(TitleId), parts[0]); }
            catch (ArgumentException) { return null; }
            parts[1] = Chapters.CaseId(title, scenario);
            key = string.Join("|", parts);
            return Chapters.Base(Chapters.StemOfScenario(title, scenario));
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
            string reply = Ctx.ReplyId(__result);
            var before = Ctx.SeenReplies((int)item, itorhum);
            string verdict = before == null ? "première fois"
                : !Ctx.HasDedicatedReply(item, itorhum) ? "réponse par défaut"
                : ReplyCoverage.IsCovered(GSStatic.global_work_.title, reply, before) ? "déjà vue" : "NOUVELLE réponse";
            Plugin.History.Add(key, reply);
            if (Plugin.Verbose.Value)
                Plugin.Log.LogInfo($"[présentation] {key} -> {reply} ({verdict})");
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
