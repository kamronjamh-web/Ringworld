using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Save;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Tools;

/// <summary>Exercises real managed save readers without a renderer or user save slot.</summary>
public partial class WorldLoadingSaveContract : Node
{
    public bool Complete { get; private set; }
    public Godot.Collections.Dictionary Result { get; private set; } = new();

    public override void _Ready() => RunFixture();

    public Godot.Collections.Dictionary VerifyLoadingFocus(Node explorerNode)
    {
        if (explorerNode is not RingVoxelExplorerController explorer)
            return new() { ["pass"] = false, ["reason"] = "Explorer fixture is unavailable." };
        RingCanonicalPosition original = explorer.CanonicalPosition;
        Vector3 velocity = explorer.CanonicalVelocity;
        RingCanonicalPosition? checkpoint = explorer.UndergroundCheckpoint;
        try
        {
            // A saved excavation can be far below the unedited surface. Initial
            // streaming must preserve this height until its edit journal exists.
            RingCanonicalPosition excavation = original with { VerticalMeters = -1000000.125 };
            explorer.RestoreWorldLoadingFocus(excavation);
            if (explorer.CanonicalPosition != excavation || explorer.CanonicalVelocity != velocity
                || explorer.UndergroundCheckpoint != checkpoint)
                throw new InvalidOperationException("Loading focus performed early recovery or changed saved motion/checkpoint state.");
            return new() { ["pass"] = true, ["checks"] = 3 };
        }
        catch (Exception exception) { return new() { ["pass"] = false, ["reason"] = exception.Message }; }
        finally { explorer.RestoreWorldLoadingFocus(original); }
    }

    private async void RunFixture()
    {
        string directory = ProjectSettings.GlobalizePath($"res://.godot/world_loading_save_contract_{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "world_state.json");
        string smoothPath = Path.Combine(directory, "smooth_terrain_edits_v1.json");
        string constructionPath = Path.Combine(directory, "smooth_construction_v1.json");
        int checks = 0;
        void Require(bool condition, string reason) { checks++; if (!condition) throw new InvalidOperationException(reason); }
        const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        MethodInfo reader = typeof(RingWorldSaveManager).GetMethod("ReadWorldDocument", PrivateStatic)!;
        MethodInfo smoothReader = typeof(RingWorldSaveManager).GetMethod("ReadSmoothDocuments", PrivateStatic)!;
        object? Value(object result, string name) => result.GetType().GetProperty(name)!.GetValue(result);
        async Task<object> Read(CancellationToken token = default) =>
            (await Task.Run(() => reader.Invoke(null, new object[] { path, token })))!;
        async Task<object> ReadSmooth() =>
            (await Task.Run(() => smoothReader.Invoke(null, new object[] { smoothPath, constructionPath, CancellationToken.None })))!;
        RingWorldSaveManager? manager = null;
        try
        {
            Directory.CreateDirectory(directory);
            object empty = await Read();
            Require(Value(empty, "Document") == null && (string)Value(empty, "Error")! == "", "new slot did not remain empty");
            const string valid = "{\"FormatVersion\":3,\"Player\":{\"AroundMeters\":1234567.25,\"VerticalMeters\":87.5,\"AcrossMeters\":42.125}}";
            File.WriteAllText(path, valid);
            object parsed = await Read();
            object player = Value(Value(parsed, "Document")!, "Player")!;
            Require((string)Value(parsed, "Error")! == "" && (double)Value(player, "AroundMeters")! == 1234567.25
                && (double)Value(player, "AcrossMeters")! == 42.125, "saved canonical precision changed during worker parsing");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            object cancelled = await Read(cancellation.Token);
            Require(Value(cancelled, "Document") == null && (string)Value(cancelled, "Error")! != "", "cancelled reader exposed a restorable document");
            Require(File.ReadAllText(path) == valid, "reader/cancellation modified source save");
            File.WriteAllText(path, "{");
            object malformed = await Read();
            Require(Value(malformed, "Document") == null && (string)Value(malformed, "Error")! != "", "malformed JSON was accepted");
            Require(File.ReadAllText(path) == "{", "malformed save was rewritten");
            File.WriteAllText(path, "null");
            object nullDocument = await Read();
            Require((string)Value(nullDocument, "Error")! != "", "null JSON was treated as a new slot");
            File.WriteAllText(smoothPath, "{\"FormatVersion\":1,\"Commands\":[]}");
            File.WriteAllText(constructionPath, "[]");
            object smooth = await ReadSmooth();
            Require(Value(smooth, "Journal") != null && Value(smooth, "Construction") != null
                && (string)Value(smooth, "Error")! == "", "smooth worker lost managed journal data");
            File.WriteAllText(constructionPath, "null");
            object badConstruction = await ReadSmooth();
            Require((string)Value(badConstruction, "Error")! != "", "null construction journal was accepted");
            manager = new RingWorldSaveManager();
            Require(!manager.UseStagedStartup && !manager.IsWorldLoadComplete, "default startup contract changed");
            manager.UseStagedStartup = true;
            typeof(RingWorldSaveManager).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, true);
            Require(manager.IsWorldLoadComplete, "successful load was not externally visible");
            manager.CancelWorldLoad();
            Require(manager.IsWorldLoadFailed && !manager.IsWorldLoadComplete && manager.SaveWorld() == Error.Failed,
                "cancellation after restoration allowed autosave during entry teardown");
            manager._ExitTree();
            Require(File.ReadAllText(path) == "null", "cancelled teardown modified a save");
            Require(manager.GetLoadingStatus()["stage"].AsString() == "cancelled", "cancelled status was lost");
            Result = new() { ["pass"] = true, ["checks"] = checks };
        }
        catch (Exception exception) { Result = new() { ["pass"] = false, ["checks"] = checks, ["reason"] = exception.ToString() }; }
        finally
        {
            manager?.Free();
            foreach (string file in new[] { path, smoothPath, constructionPath }) if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(directory)) Directory.Delete(directory);
            Complete = true;
        }
    }
}
