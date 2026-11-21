using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace ModPack.Animations
{
    internal sealed class AnimationEntry
    {
        public readonly string Dictionary;
        public readonly string Clip;
        public readonly string Title;
        public string Key { get { return Dictionary + "\u001f" + Clip; } }
        public AnimationEntry(string dictionary, string clip, string title)
        {
            Dictionary = dictionary;
            Clip = clip;
            Title = string.IsNullOrWhiteSpace(title) ? clip : title;
        }
    }

    internal static class AnimationCatalog
    {
        public const int PageSize = 50;
        public static List<AnimationEntry> ReadCatalog(TextReader reader)
        {
            var result = new List<AnimationEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[0].StartsWith("#", StringComparison.Ordinal)) continue;
                var entry = new AnimationEntry(parts[0], parts[1], null);
                if (keys.Add(entry.Key)) result.Add(entry);
            }
            return result;
        }
        public static List<AnimationEntry> ReadFavorites(TextReader reader)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var xml = XmlReader.Create(reader, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(xml);
            if (document.DocumentElement == null || document.DocumentElement.Name != "PedAnims")
                throw new InvalidDataException("Expected PedAnims root.");
            var result = new List<AnimationEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode node in document.SelectNodes("/PedAnims/Anim"))
            {
                string dictionary = Attribute(node, "dict").Trim();
                string clip = Attribute(node, "name").Trim();
                if (dictionary.Length == 0 || clip.Length == 0) continue;
                var entry = new AnimationEntry(dictionary, clip, Attribute(node, "displayName"));
                if (keys.Add(entry.Key)) result.Add(entry);
            }
            return result;
        }
        private static string Attribute(XmlNode node, string name)
        {
            XmlAttribute value = node.Attributes[name];
            return value == null ? "" : value.Value;
        }
        public static List<AnimationEntry> Search(IEnumerable<AnimationEntry> entries, string query)
        {
            string value = (query ?? "").Trim();
            return entries.Where(e => value.Length == 0 ||
                e.Dictionary.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0 ||
                e.Clip.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        public static List<AnimationEntry> Page(IList<AnimationEntry> entries, int page)
        {
            return entries.Skip(Math.Max(0, page) * PageSize).Take(PageSize).ToList();
        }
        public static string Label(string value)
        {
            string clean = (value ?? "").Replace("~", "").Replace("\r", " ").Replace("\n", " ");
            return clean.Length <= 40 ? clean : clean.Substring(0, 37) + "...";
        }
        // GTA dictionary/clip identifiers for the named Menyoo animation presets.
        public static List<AnimationEntry> Presets()
        {
            return new List<AnimationEntry> {
                new AnimationEntry("random@car_thief@victimpoints_ig_3", "arms_waving", "Махать руками"),
                new AnimationEntry("random@arrests", "idle_2_hands_up", "Руки вверх"),
                new AnimationEntry("random@arrests@busted", "idle_a", "Сдаться на коленях"),
                new AnimationEntry("mini@strip_club@pole_dance@pole_dance3", "pd_dance_03", "Танец у шеста"),
                new AnimationEntry("missfbi3_sniping", "dance_m_default", "Уличный танец"),
                new AnimationEntry("ragdoll@human", "on_fire", "Горит"),
                new AnimationEntry("ragdoll@human", "electrocute", "Удар током"),
                new AnimationEntry("mini@strip_club@private_dance@part1", "priv_dance_p1", "Приватный танец"),
                new AnimationEntry("rcmpaparazzo_2", "shag_loop_poppy", "The Rear Abundance"),
                new AnimationEntry("rcmpaparazzo_2", "shag_loop_a", "The Invisible Man"),
                new AnimationEntry("amb@world_human_push_ups@male@base", "base", "Отжимания"),
                new AnimationEntry("amb@world_human_sit_ups@male@base", "base", "Качать пресс"),
                new AnimationEntry("mini@prostitutes@sexnorm_veh", "bj_loop_prostitute", "Give BJ to Driver"),
                new AnimationEntry("mini@prostitutes@sexnorm_veh", "sex_loop_prostitute", "Pleasure Driver"),
                new AnimationEntry("special_ped@mime@monologue_8@monologue_8a", "08_ig_1_wall_ba_0", "Мим: стена"),
                new AnimationEntry("special_ped@mime@monologue_7@monologue_7a", "11_ig_1_run_aw_0", "Мим: бег"),
                new AnimationEntry("switch@franklin@throw_cup", "throw_cup_loop", "Бросить стакан"),
                new AnimationEntry("timetable@gardener@smoking_joint", "idle_cough", "Кашлять от дыма"),
                new AnimationEntry("friends@laf@ig_1@base", "base", "Отдых с друзьями"),
                new AnimationEntry("timetable@ron@they_think_were_stupid", "they_think_were_stupid", "They Think We Dumb"),
                new AnimationEntry("gestures@m@standing@fat", "gesture_come_here_hard", "Иди сюда"),
                new AnimationEntry("gestures@m@standing@fat", "gesture_no_way", "Ни за что"),
                new AnimationEntry("random@bicycle_thief@ask_help", "my_dads_going_to_kill_me", "They're Gonna Kill Me"),
                new AnimationEntry("random@bicycle_thief@ask_help", "please_man_you_gotta_help_me", "Просить помощи"),
                new AnimationEntry("savecouch@", "t_sleep_loop_couch", "Спать на диване"),
                new AnimationEntry("savem_default@", "m_sleep_r_loop", "Спать на боку"),
                new AnimationEntry("timetable@tracy@sleep@", "idle_c", "Спать"),
                new AnimationEntry("rcmcollect_paperleadinout@", "meditiate_idle", "Медитация"),
                new AnimationEntry("switch@trevor@jerking_off", "trev_jerking_off_loop", "Fap"),
                new AnimationEntry("special_ped@jessie@michael_1@michael_1b", "jessie_ig_2_yeahyeahyeah_1", "Yeah Yeah Yeah"),
                new AnimationEntry("switch@franklin@on_laptop", "001927_01_fras_v2_4_on_laptop_idle", "За ноутбуком"),
                new AnimationEntry("mp_sleep", "bind_pose_180", "Руки в стороны")
            };
        }
    }
}
