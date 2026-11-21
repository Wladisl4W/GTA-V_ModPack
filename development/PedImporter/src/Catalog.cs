using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ModPack.PedImporter
{
    public sealed class PedModel
    {
        public bool Selected { get; set; }
        public string Name { get; set; }
        public int Files { get; set; }
        public bool Streamed { get; set; }
        public string Status { get; set; }
        public string FavoriteStatus { get; set; }
        public bool Valid { get; set; }
    }

    public static class Catalog
    {
        private static readonly string[] Required = { ".ydd", ".ytd", ".yft", ".ymt" };
        public static List<PedModel> Scan(string directory)
        {
            var result = new List<PedModel>();
            var all = Directory.GetFiles(directory);
            foreach (var group in all.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Any(f => Required.Contains(Path.GetExtension(f).ToLowerInvariant())))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var missing = Required.Where(ext => !group.Any(f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase))).ToArray();
                bool streamed = Directory.GetDirectories(directory).Any(d => Path.GetFileName(d).Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                bool valid = missing.Length == 0 && group.All(f => new FileInfo(f).Length > 0);
                result.Add(new PedModel { Name = group.Key, Files = group.Count(), Streamed = streamed,
                    Valid = valid, Selected = valid, Status = missing.Length > 0 ? "Не хватает: " + string.Join(", ", missing) : valid ? "Готов" : "Пустой файл",
                    FavoriteStatus = valid ? "Не проверено" : "Недоступен" });
            }
            return result;
        }

        public static uint Hash(string name)
        {
            uint hash = 0;
            unchecked
            {
                foreach (char c in name.ToLowerInvariant()) { hash += c; hash += hash << 10; hash ^= hash >> 6; }
                hash += hash << 3; hash ^= hash >> 11; hash += hash << 15;
            }
            return hash;
        }

        public static HashSet<uint> ReadFavoriteHashes(string gameDirectory)
        {
            string path = FavoritePath(gameDirectory);
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new IOException("menyooStuff not found.");
            var document = File.Exists(path) ? XDocument.Load(path) : new XDocument(new XElement("FavouritePeds"));
            if (document.Root == null || document.Root.Name != "FavouritePeds") throw new InvalidDataException("Invalid FavouritePeds.xml");
            var hashes = new HashSet<uint>();
            foreach (var ped in document.Root.Elements("Ped"))
            {
                string text = (string)ped.Attribute("hash");
                uint parsed;
                if (text != null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out parsed)) hashes.Add(parsed);
                else throw new InvalidDataException("Unrecognized existing ped hash; favorites left unchanged.");
            }
            return hashes;
        }

        public static void UpdateFavoriteStatuses(string gameDirectory, IEnumerable<PedModel> models)
        {
            HashSet<uint> hashes = ReadFavoriteHashes(gameDirectory);
            foreach (var model in models)
                model.FavoriteStatus = !model.Valid ? "Недоступен" :
                    hashes.Contains(Hash(model.Name)) ? "Уже в избранном" : "Будет добавлен";
        }

        private static string FavoritePath(string gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
                throw new DirectoryNotFoundException("Выберите существующую папку GTA V.");
            return Path.Combine(gameDirectory, "menyooStuff", "FavouritePeds.xml");
        }

        public static int AddFavorites(string gameDirectory, IEnumerable<PedModel> models)
        {
            string path = FavoritePath(gameDirectory);
            HashSet<uint> hashes = ReadFavoriteHashes(gameDirectory);
            var document = File.Exists(path) ? XDocument.Load(path) : new XDocument(new XElement("FavouritePeds"));
            int count = 0;
            foreach (var model in models.Where(m => m.Selected && m.Valid))
                if (hashes.Add(Hash(model.Name))) { document.Root.Add(new XElement("Ped", new XAttribute("hash", "0x" + Hash(model.Name).ToString("x8")), new XAttribute("customName", model.Name))); count++; }
            if (count == 0) return 0;
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                document.Save(temp);
                var checkedDocument = XDocument.Load(temp);
                if (checkedDocument.Root == null || checkedDocument.Root.Name != "FavouritePeds" ||
                    checkedDocument.Root.Elements("Ped").Count() != document.Root.Elements("Ped").Count())
                    throw new InvalidDataException("Проверка нового FavouritePeds.xml не прошла.");
                if (File.Exists(path))
                    File.Replace(temp, path, path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "." + Guid.NewGuid().ToString("N") + ".bak");
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return count;
        }
    }
}
