$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$source = Get-Content -LiteralPath (Join-Path $repo 'modules\Core\src\Code\PluginStorage.cs') -Raw
Add-Type -TypeDefinition ($source + @'
public static class StorageTests {
    public static void Run(string root) {
        System.IO.Directory.CreateDirectory(root);
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Settings.json"), "original");
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Settings.json.bak"), "backup");
        string path = ModPack.Core.PluginStorage.ResolveFile(root, "Test", "Settings.json");
        if (System.IO.File.ReadAllText(path) != "original" || System.IO.File.ReadAllText(path + ".bak") != "backup") throw new System.Exception("Family migration failed");
        if (System.IO.File.Exists(System.IO.Path.Combine(root, "Settings.json"))) throw new System.Exception("Original not removed");
        if (ModPack.Core.PluginStorage.ResolveFile(root, "Test", "Settings.json") != path) throw new System.Exception("Repeat failed");
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Conflict.json"), "old");
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Test", "Conflict.json"), "new");
        path = ModPack.Core.PluginStorage.ResolveFile(root, "Test", "Conflict.json");
        if (System.IO.File.ReadAllText(path) != "new" || System.IO.Directory.GetFiles(System.IO.Path.Combine(root, "Test", "Legacy")).Length != 1) throw new System.Exception("Conflict failed");
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Same.json"), "same");
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Test", "Same.json"), "same");
        ModPack.Core.PluginStorage.ResolveFile(root, "Test", "Same.json");
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Blocked.json"), "safe");
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Blocked"), "obstacle");
        path = ModPack.Core.PluginStorage.ResolveFile(root, "Blocked", "Blocked.json");
        if (path != System.IO.Path.Combine(root, "Blocked.json") || System.IO.File.ReadAllText(path) != "safe") throw new System.Exception("Failure lost original");
        string oldPaths = System.IO.Path.Combine(root, "Paths");
        string newPaths = System.IO.Path.Combine(root, "ModdedCamera", "Paths");
        System.IO.Directory.CreateDirectory(oldPaths);
        System.IO.Directory.CreateDirectory(newPaths);
        System.IO.File.WriteAllText(System.IO.Path.Combine(oldPaths, "first.json"), "first");
        System.IO.File.WriteAllText(System.IO.Path.Combine(newPaths, "first.json"), "first");
        System.IO.File.WriteAllText(System.IO.Path.Combine(oldPaths, "second.json.bak"), "second");
        string resolved = ModPack.Core.PluginStorage.ResolveCameraPaths(root);
        if (resolved != newPaths || System.IO.File.ReadAllText(System.IO.Path.Combine(newPaths, "second.json.bak")) != "second" || System.IO.Directory.Exists(oldPaths)) throw new System.Exception("Partial camera migration failed");
        string owner = System.IO.Path.Combine(root, "RemoveDroppedPeds");
        System.IO.Directory.CreateDirectory(owner);
        System.IO.File.WriteAllText(System.IO.Path.Combine(owner, "RemoveDroppedPeds.json"), "settings");
        System.IO.File.WriteAllText(System.IO.Path.Combine(owner, "RemoveDroppedPeds.json.bak"), "backup");
        string moduleSettings = ModPack.Core.PluginStorage.ResolveModuleFile(root, "RemoveDroppedPeds", "Cleanup", "RemoveDroppedPeds.json");
        if (!moduleSettings.Contains("Cleanup") || System.IO.File.ReadAllText(moduleSettings) != "settings" ||
            System.IO.File.ReadAllText(moduleSettings + ".bak") != "backup" ||
            System.IO.File.Exists(System.IO.Path.Combine(owner, "RemoveDroppedPeds.json"))) throw new System.Exception("Module settings migration failed");
        System.IO.File.WriteAllText(System.IO.Path.Combine(owner, "RemoveDroppedPeds.log"), "events");
        string moduleLog = ModPack.Core.PluginStorage.ResolveModuleFile(root, "RemoveDroppedPeds", "Cleanup", "RemoveDroppedPeds.log");
        if (!moduleLog.Contains("Logs") || System.IO.File.ReadAllText(moduleLog) != "events") throw new System.Exception("Module log migration failed");
        System.IO.Directory.CreateDirectory(oldPaths);
        System.IO.File.WriteAllText(System.IO.Path.Combine(oldPaths, "saved.json"), "path");
        string modulePaths = ModPack.Core.PluginStorage.ResolveModuleCameraPaths(root);
        if (System.IO.File.ReadAllText(System.IO.Path.Combine(modulePaths, "saved.json")) != "path" ||
            System.IO.File.Exists(System.IO.Path.Combine(oldPaths, "saved.json"))) throw new System.Exception("Module camera paths migration failed");
    }
}
'@)
$root = Join-Path $env:TEMP ('ModPackStorage-' + [guid]::NewGuid().ToString('N'))
[StorageTests]::Run($root)
Write-Host 'PASS: settings, backup, conflicts, identical files, repeated resolution and failed write.'
