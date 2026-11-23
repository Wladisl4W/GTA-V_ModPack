using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace ModPack.Core
{
    internal static class PluginStorage
    {
        private static readonly Dictionary<string, string> Resolved = new Dictionary<string, string>();
        private static readonly object Sync = new object();
        private static string Root { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReloaderPlugins"); } }

        public static string FilePath(string owner, string name)
        {
            string dataRoot = MenuDataRoot(owner, name);
            return dataRoot == null ? ResolveFile(Root, owner, name) : ResolveMenuFile(Root, owner, name, dataRoot);
        }

        private static string MenuDataRoot(string owner, string name)
        {
            switch (owner)
            {
                case "ModdedCamera": return Path.Combine("Camera", "Data");
                case "RainbowPaint":
                    return name == "RainbowPaintPresets.json" || name == "RainbowPaintPassengerModels.json"
                        ? Path.Combine("Templates", "Data") : Path.Combine("VehiclePaint", "Data");
                case "SharkRider": return Path.Combine("Misc", "Data", "Wildlife");
                case "RemoveDroppedPeds": return Path.Combine("Optimization", "Data", "Cleanup");
                case "FrozenDynamic": return Path.Combine("Optimization", "Data", "PedControl");
                case "MenyooStreamer": return Path.Combine("Optimization", "Data", "Streamer");
                case "Prefabs": return Path.Combine("Prefabs", "Data");
                case "Development": return Path.Combine("_System", "Diagnostics", "Data", "Dev");
                case "Logs": return Path.Combine("_System", "Shared", "Data");
                default: return null;
            }
        }

        private static string ModuleFor(string owner)
        {
            switch (owner)
            {
                case "ModdedCamera": return "Camera";
                case "RainbowPaint": return "Paint";
                case "SharkRider": return "Wildlife";
                case "RemoveDroppedPeds": return "Cleanup";
                case "FrozenDynamic": return "PedControl";
                case "MenyooStreamer": return "Streamer";
                case "Prefabs": return "Prefabs";
                case "Development": return "Dev";
                case "Logs": return "Core";
                default: return null;
            }
        }

        internal static string ResolveMenuFile(string root, string owner, string name, string relativeDataRoot)
        {
            string category = name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? "Logs" : "Settings";
            string targetRoot = Path.Combine(root, "Plugins", relativeDataRoot);
            string target = Path.Combine(targetRoot, category, name);
            lock (Sync)
            {
                string cached;
                if (Resolved.TryGetValue(target, out cached)) return cached;
                string oldModule = ModuleFor(owner);
                var candidates = new List<string>();
                var directories = new List<string> { Path.Combine(root, owner), root };
                if (oldModule != null)
                    directories.Insert(0, Path.Combine(root, "Plugins", oldModule, "Data", category));
                try
                {
                    foreach (string directory in directories)
                    {
                        if (!Directory.Exists(directory)) continue;
                        foreach (string file in Directory.GetFiles(directory))
                        {
                            string fileName = Path.GetFileName(file);
                            if (fileName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                fileName.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                                fileName.StartsWith(Path.GetFileNameWithoutExtension(name) + ".old.", StringComparison.OrdinalIgnoreCase))
                                candidates.Add(file);
                        }
                    }
                    foreach (string file in candidates)
                        Stage(file, Path.Combine(targetRoot, category, Path.GetFileName(file)), targetRoot);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    Resolved[target] = target;
                    return target;
                }
                catch (Exception ex)
                {
                    MigrationError(root, ex);
                    string fallback = candidates.Count > 0 ? candidates[0] : target;
                    Resolved[target] = fallback;
                    return fallback;
                }
            }
        }

        internal static string ResolveModuleFile(string root, string owner, string module, string name)
        {
            string category = name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? "Logs" : "Settings";
            string dataRoot = Path.Combine(root, "Plugins", module, "Data");
            string target = Path.Combine(dataRoot, category, name);
            lock (Sync)
            {
                string result;
                if (Resolved.TryGetValue(target, out result)) return result;
                string ownedOld = Path.Combine(root, owner, name);
                string rootOld = Path.Combine(root, name);
                try
                {
                    var files = new List<string>();
                    foreach (string directory in new[] { Path.Combine(root, owner), root })
                    {
                        if (!Directory.Exists(directory)) continue;
                        foreach (string file in Directory.GetFiles(directory))
                        {
                            string candidate = Path.GetFileName(file);
                            if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                candidate.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                                candidate.StartsWith(Path.GetFileNameWithoutExtension(name) + ".old.", StringComparison.OrdinalIgnoreCase))
                                files.Add(file);
                        }
                    }
                    foreach (string file in files) Stage(file, Path.Combine(dataRoot, category, Path.GetFileName(file)), dataRoot);
                    foreach (string file in files) File.Delete(file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    result = target;
                }
                catch (Exception ex)
                {
                    MigrationError(root, ex);
                    result = File.Exists(ownedOld) ? ownedOld : rootOld;
                }
                Resolved[target] = result;
                return result;
            }
        }

        internal static string ResolveFile(string root, string owner, string name)
        {
            string target = Path.Combine(root, owner, name);
            lock (Sync)
            {
                string result;
                if (Resolved.TryGetValue(target, out result)) return result;
                string old = Path.Combine(root, name);
                try
                {
                    var files = new List<string>();
                    if (Directory.Exists(root))
                        foreach (string file in Directory.GetFiles(root))
                        {
                            string candidate = Path.GetFileName(file);
                            if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                candidate.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                                candidate.StartsWith(Path.GetFileNameWithoutExtension(name) + ".old.", StringComparison.OrdinalIgnoreCase))
                                files.Add(file);
                        }
                    // Stage the whole family before deleting any original, including backups.
                    foreach (string file in files) Stage(file, Path.Combine(root, owner, Path.GetFileName(file)), Path.Combine(root, owner));
                    foreach (string file in files) File.Delete(file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    result = target;
                }
                catch (Exception ex)
                {
                    MigrationError(root, ex);
                    result = old;
                }
                Resolved[target] = result;
                return result;
            }
        }

        public static string CameraPaths()
        {
            return ResolveModuleCameraPaths(Root);
        }

        internal static string ResolveModuleCameraPaths(string root)
        {
            string target = Path.Combine(root, "Plugins", "Camera", "Data", "Paths");
            lock (Sync)
            {
                string result;
                if (Resolved.TryGetValue(target, out result)) return result;
                string old = Path.Combine(root, "ModdedCamera", "Paths");
                try
                {
                    var files = new List<string>();
                    foreach (string directory in new[] {
                        Path.Combine(root, "Plugins", "Camera", "Data", "Saves", "Paths"),
                        old, Path.Combine(root, "Paths") })
                    {
                        if (!Directory.Exists(directory)) continue;
                        foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                        {
                            string relative = file.Substring(directory.Length + 1);
                            Stage(file, Path.Combine(target, relative), Path.Combine(root, "Plugins", "Camera", "Data"));
                            files.Add(file);
                        }
                    }
                    Directory.CreateDirectory(target);
                    result = target;
                }
                catch (Exception ex)
                {
                    MigrationError(root, ex);
                    result = Directory.Exists(old) ? old : Path.Combine(root, "Paths");
                }
                Resolved[target] = result;
                return result;
            }
        }

        internal static string ResolveCameraPaths(string root)
        {
            string old = Path.Combine(root, "Paths");
            string target = Path.Combine(root, "ModdedCamera", "Paths");
            lock (Sync)
            {
                string result;
                if (Resolved.TryGetValue(target, out result)) return result;
                try
                {
                    string[] files = Directory.Exists(old) ? Directory.GetFiles(old, "*", SearchOption.AllDirectories) : new string[0];
                    foreach (string file in files)
                        Stage(file, Path.Combine(target, file.Substring(old.Length + 1)), Path.Combine(root, "ModdedCamera"));
                    foreach (string file in files) File.Delete(file);
                    // Remove empty legacy path directories without recursive deletion.
                    if (Directory.Exists(old)) RemoveEmptyDirectories(old);
                    result = target;
                }
                catch (Exception ex)
                {
                    MigrationError(root, ex);
                    result = old;
                }
                Resolved[target] = result;
                return result;
            }
        }

        private static void Stage(string source, string target, string ownerDirectory)
        {
            if (File.Exists(target))
            {
                if (Equal(source, target)) return;
                target = Path.Combine(ownerDirectory, "Legacy", Path.GetFileName(source) + "." + Guid.NewGuid().ToString("N"));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target, false);
            if (!Equal(source, target)) throw new IOException("Migration verification failed: " + source);
        }

        private static void MigrationError(string root, Exception ex)
        {
            try
            {
                string directory = Path.Combine(root, "Plugins", "_System", "Shared", "Data", "Logs");
                Directory.CreateDirectory(directory);
                ModPack.Core.BoundedLog.Append(Path.Combine(directory, "StorageMigration.log"), DateTime.Now.ToString("o") + " " + ex + Environment.NewLine);
            }
            catch { System.Diagnostics.Trace.WriteLine(ex); }
        }

        private static void RemoveEmptyDirectories(string directory)
        {
            foreach (string child in Directory.GetDirectories(directory)) RemoveEmptyDirectories(child);
            if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory, false);
        }

        private static bool Equal(string first, string second)
        {
            using (var hash = SHA256.Create())
            using (var a = File.OpenRead(first))
            using (var b = File.OpenRead(second))
                return Convert.ToBase64String(hash.ComputeHash(a)) == Convert.ToBase64String(hash.ComputeHash(b));
        }
    }
}
