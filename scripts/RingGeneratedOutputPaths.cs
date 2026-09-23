using System;
using System.IO;
using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Resolves disposable diagnostics beneath <c>user://generated</c>.
/// This is deliberately separate from saves, terrain caches, and settings, so
/// the complete generated subtree can be removed without affecting gameplay.
/// </summary>
public static class RingGeneratedOutputPaths
{
    public const string UserRoot = "user://generated";

    public static string Directory(string category)
    {
        ValidateRelativePath(category, nameof(category));
        string directory = ProjectSettings.GlobalizePath($"{UserRoot}/{category}");
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    public static string File(string category, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentException("Generated output file names must be plain file names.", nameof(fileName));
        return Path.Combine(Directory(category), fileName);
    }

    private static void ValidateRelativePath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains(':'))
            throw new ArgumentException("Generated output categories must be relative paths below user://generated.", parameterName);
    }
}
