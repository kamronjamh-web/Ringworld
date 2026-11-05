using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace RingworldTechDemo.Vegetation;

public sealed record PreparedTreeSourceIdentity(string Fingerprint, string[] Inputs);

/// <summary>Pure file IO; callers may hash authoring inputs on a worker. Exported games use the compiled identity.</summary>
public static class PreparedImportedTreeCatalogIdentity
{
    public const string MetadataKey = "Ringworld.PreparedTreeImplementation";
    public const int EngineCommitPrefixLength = 9;
    public static string ImplementationIdentity => typeof(ImportedTreeCatalog).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value ?? "";
    public static string CurrentEngineIdentity
    {
        get
        {
            var version = Godot.Engine.GetVersionInfo();
            string hash = version["hash"].AsString();
            // Deliberately a nine-character commit prefix, not a claim to record the full commit hash.
            return version["string"].AsString() + "|" + hash[..Math.Min(EngineCommitPrefixLength, hash.Length)];
        }
    }

    public static PreparedTreeSourceIdentity ReadSourceIdentity(string projectRoot)
    {
        var paths = new List<string>();
        foreach (string species in new[] { "fir", "maple", "pine" })
        {
            string folder = "resources/trees/lolipop/" + species;
            foreach (string file in new[] { "scene.bin", "scene.gltf", "scene.gltf.import" }) paths.Add(folder + "/" + file);
            string textures = Path.Combine(projectRoot, folder, "textures");
            foreach (string file in Directory.EnumerateFiles(textures, "*.png.import", SearchOption.AllDirectories))
                paths.Add(Path.GetRelativePath(projectRoot, file).Replace('\\', '/'));
        }
        paths.Sort(StringComparer.Ordinal);
        var records = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            using var stream = new FileStream(Path.Combine(projectRoot, path), FileMode.Open, System.IO.FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            records.Add(path + "=" + Convert.ToHexString(SHA256.HashData(stream)));
        }
        string canonical = string.Join("\r\n", records) + "\r\n";
        return new PreparedTreeSourceIdentity(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), records.ToArray());
    }
}
