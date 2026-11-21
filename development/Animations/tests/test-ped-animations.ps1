param([string]$Config)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent) -Parent) -Parent
if (!$Config) { $Config = Join-Path $repo 'modpack.local.json' }
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$source = Get-Content -LiteralPath (Join-Path $repo 'modules/ReloaderPlugins/Plugins/Animations/Code/AnimationCatalog.cs') -Raw
$tests = @'
public static class PedAnimationTests
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new System.Exception(message);
    }
    public static string Run(string root)
    {
        var catalog = ModPack.Animations.AnimationCatalog.ReadCatalog(new System.IO.StringReader(
            "# comment\ndict_a clip_a\n\tdict_b\tclip_b \ndict_a clip_a\nbroken\nthree separate words\n"));
        Require(catalog.Count == 2, "Malformed lines or duplicate catalog entries were not filtered.");
        Require(catalog[0].Dictionary == "dict_a" && catalog[1].Clip == "clip_b", "Catalog order changed.");
        var favorites = ModPack.Animations.AnimationCatalog.ReadFavorites(new System.IO.StringReader(
            "<PedAnims><Anim dict='d&amp;x' name='c' displayName='Friendly'/><Anim dict='d&amp;x' name='c'/>" +
            "<Anim dict='other' name='second'/><Anim dict='' name='invalid'/><Anim dict='missing'/></PedAnims>"));
        Require(favorites.Count == 2 && favorites[0].Dictionary == "d&x" && favorites[0].Title == "Friendly",
            "Favorites attributes, escaping or duplicates failed.");
        Require(ModPack.Animations.AnimationCatalog.ReadFavorites(new System.IO.StringReader("<PedAnims/>" )).Count == 0,
            "Empty favorites must be allowed.");
        foreach (string invalid in new[] { "<PedAnims>", "<Wrong/>", "", "<!DOCTYPE PedAnims [<!ENTITY x 'x'>]><PedAnims/>" })
        {
            bool rejected = false;
            try { ModPack.Animations.AnimationCatalog.ReadFavorites(new System.IO.StringReader(invalid)); }
            catch (System.Xml.XmlException) { rejected = true; }
            catch (System.IO.InvalidDataException) { rejected = true; }
            Require(rejected, "Invalid XML or DTD was accepted.");
        }
        Require(ModPack.Animations.AnimationCatalog.Search(catalog, "DICT_B").Count == 1, "Dictionary search failed.");
        Require(ModPack.Animations.AnimationCatalog.Search(catalog, "clip_a").Count == 1, "Clip search failed.");
        Require(ModPack.Animations.AnimationCatalog.Search(catalog, "  ").Count == 2, "Search reset failed.");
        Require(ModPack.Animations.AnimationCatalog.Search(catalog, "absent").Count == 0, "Empty results failed.");
        var many = new System.Collections.Generic.List<ModPack.Animations.AnimationEntry>();
        for (int i = 0; i < 121; i++) many.Add(new ModPack.Animations.AnimationEntry("d", "c" + i, null));
        Require(ModPack.Animations.AnimationCatalog.Page(many, 0).Count == 50, "First page failed.");
        Require(ModPack.Animations.AnimationCatalog.Page(many, 1)[0].Clip == "c50", "Page boundary failed.");
        Require(ModPack.Animations.AnimationCatalog.Page(many, 2).Count == 21, "Last page failed.");
        Require(ModPack.Animations.AnimationCatalog.Page(many, 3).Count == 0, "Out-of-range page failed.");
        Require(ModPack.Animations.AnimationCatalog.Label(new string('x', 80)).Length == 40, "Long label overflow.");
        Require(!ModPack.Animations.AnimationCatalog.Label("~r~test").Contains("~"), "Formatting token was not removed.");
        var presets = ModPack.Animations.AnimationCatalog.Presets();
        Require(presets.Count == 32, "Named Menyoo presets are incomplete.");
        Require(presets.Exists(e => e.Clip == "arms_waving") && presets.Exists(e => e.Clip == "idle_2_hands_up"),
            "Requested wave/hands-up presets are missing.");
        var keys = new System.Collections.Generic.HashSet<string>();
        foreach (var entry in presets) Require(keys.Add(entry.Key), "Duplicate preset.");
        int count;
        using (var reader = new System.IO.StreamReader(System.IO.Path.Combine(root, "PedAnimList.txt")))
        {
            var real = ModPack.Animations.AnimationCatalog.ReadCatalog(reader);
            count = real.Count;
            Require(count > 50, "Installed Menyoo catalog was not loaded.");
            Require(ModPack.Animations.AnimationCatalog.Search(real, "arms_waving").Count > 0, "Real catalog search failed.");
            var installedKeys = new System.Collections.Generic.HashSet<string>();
            foreach (var entry in real) installedKeys.Add(entry.Key);
            foreach (var entry in presets) Require(installedKeys.Contains(entry.Key), "Preset missing from installed catalog: " + entry.Title);
        }
        int favoriteCount;
        using (var reader = new System.IO.StreamReader(System.IO.Path.Combine(root, "FavouriteAnims.xml")))
            favoriteCount = ModPack.Animations.AnimationCatalog.ReadFavorites(reader).Count;
        return "Parser, deduplication, XML safety, search, pagination and preset tests passed. Installed catalog: " +
            count + "; favorites: " + favoriteCount + ".";
    }
}
'@
Add-Type -TypeDefinition ($source + $tests) -ReferencedAssemblies System.Core,System.Xml
$menyoo = Join-Path $settings.GameDirectory 'menyooStuff'
$paths = @((Join-Path $menyoo 'PedAnimList.txt'), (Join-Path $menyoo 'FavouriteAnims.xml'))
$before = @($paths | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })
$watch = [Diagnostics.Stopwatch]::StartNew()
Write-Host ([PedAnimationTests]::Run($menyoo))
for ($i = 0; $i -lt $paths.Count; $i++) {
    if ((Get-FileHash -LiteralPath $paths[$i]).Hash -ne $before[$i]) { throw 'A Menyoo file was modified.' }
}
Write-Host ("Completed in {0:N2}s; Menyoo files unchanged." -f $watch.Elapsed.TotalSeconds)
