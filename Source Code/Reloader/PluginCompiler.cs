using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CSharp;

// Shared by the live loader and the Windows PowerShell validation command.
public static class PluginCompiler
{
    public static string[] GetSources(string directory)
    {
        return Directory.GetFiles(directory, "*.cs")
            .Where(p => !Path.GetFileName(p).StartsWith("_") ||
                Path.GetFileName(p).Equals("_PluginInterface.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string Fingerprint(string directory)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        using (var sha = SHA256.Create())
        {
            foreach (string file in GetSources(directory))
            {
                writer.Write(Path.GetFileName(file));
                byte[] bytes = File.ReadAllBytes(file);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            writer.Flush();
            return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        }
    }

    private static void AddReference(Dictionary<string, string> references, string path)
    {
        try
        {
            string name = AssemblyName.GetAssemblyName(path).Name;
            if (!references.ContainsKey(name)) references.Add(name, path);
        }
        catch (BadImageFormatException) { }
    }

    public static CompilerResults Compile(string directory, string scriptsDirectory,
        string shvdn, string lemonUi, out string fingerprint)
    {
        string[] files = GetSources(directory);
        if (files.Length == 0) throw new InvalidOperationException("No plugin sources found.");
        fingerprint = Fingerprint(directory);
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { scriptsDirectory, Path.Combine(scriptsDirectory, "scripts") })
            if (Directory.Exists(folder))
                foreach (string dll in Directory.GetFiles(folder, "*.dll")) AddReference(references, dll);
        string pluginsRoot = Path.Combine(scriptsDirectory, "ReloaderPlugins");
        if (Directory.Exists(pluginsRoot))
            foreach (string dll in Directory.GetFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories))
                AddReference(references, dll);
        AddReference(references, shvdn);
        AddReference(references, lemonUi);
        var options = new CompilerParameters {
            GenerateInMemory = true, GenerateExecutable = false, TreatWarningsAsErrors = false, WarningLevel = 4,
            TempFiles = new TempFileCollection(Path.GetTempPath(), false)
        };
        options.ReferencedAssemblies.AddRange(references.Values.ToArray());
        options.ReferencedAssemblies.AddRange(new[] { "System.dll", "System.Core.dll",
            "System.Data.dll", "System.Drawing.dll", "System.Windows.Forms.dll",
            "System.Xml.dll", "System.Web.Extensions.dll" });
        using (var provider = new CSharpCodeProvider())
        {
            CompilerResults result = provider.CompileAssemblyFromFile(options, files);
            if (fingerprint != Fingerprint(directory))
                throw new IOException("Plugin sources changed during compilation; retry.");
            return result;
        }
    }
}
