using System;
using System.Globalization;
using System.IO;
using Godot;

namespace RingworldTechDemo.Terrain.Generation;

public static class WorldGenerationSettingsFiles
{
    /// <summary>Use the generation's sibling settings file, seeding it once from the base file.</summary>
    public static string Resolve(string userPath, int fromGeneration)
    {
        if (WorldGeneration.Current.Generation < fromGeneration) return userPath;

        string stem = Path.ChangeExtension(userPath, null);
        string generation = fromGeneration.ToString("D2", CultureInfo.InvariantCulture);
        string versionedPath = $"{stem}_g{generation}.tres";
        string source = ProjectSettings.GlobalizePath(userPath);
        string destination = ProjectSettings.GlobalizePath(versionedPath);
        if (!File.Exists(source) || File.Exists(destination)) return versionedPath;

        // Copy to a sibling before publishing the filename. A failed copy must not leave a partial settings file.
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // A concurrent resolve already installed the file.
        }
        finally
        {
            File.Delete(temporary);
        }
        return versionedPath;
    }
}
