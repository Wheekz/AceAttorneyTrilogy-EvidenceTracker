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
    [BepInPlugin("wheekz.pwaat.qol", "PWAAT QoL", "0.2.1")]
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

        internal static readonly Color Dim = new Color(0.45f, 0.45f, 0.45f, 0.55f);
        internal static readonly Color Gold = new Color(1f, 0.85f, 0.35f, 1f);

        private static void Postfix(recordListCtrl __instance, int in_id, int in_no, bool is_sub)
        {
            var list = is_sub ? SubList(__instance) : DispList(__instance);
            if (list == null || in_no < 0 || in_no >= list.Count) return;
            var mark = Ctx.Evaluate(in_id, __instance.record_type, out _);
            list[in_no].icon_.color = mark == Mark.Seen ? Dim : mark == Mark.New ? Gold : Color.white;
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
            if (mark == Mark.Seen) label.text += " <color=#9a9a9a>" + Labels.Get(mark) + "</color>";
            else if (mark == Mark.New) label.text += " <color=#d08a00>" + Labels.Get(mark) + "</color>";
            if (Plugin.Verbose.Value && Ctx.PresentingInInvestigation)
                Plugin.Log.LogInfo($"[curseur] objet={__instance.note_id} type={__instance.record_type} -> réponse {resp} ({mark})");
        }
    }
}
