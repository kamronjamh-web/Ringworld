using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Pda;
using FileAccess = System.IO.FileAccess;

namespace RingworldTechDemo.Tools;

public partial class PdaConversationStoreTest : RefCounted
{
    public int Run()
    {
        string fixture = ProjectSettings.GlobalizePath("res://.godot/pda_memory/store-fixtures");
        try
        {
            string allowedRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot/pda_memory")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(fixture).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Conversation fixtures must remain under this task's .godot directory.");
            Task.Run(() => RunAsync(fixture)).GetAwaiter().GetResult();
            GD.Print("PDA_CONVERSATION_STORE=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError("PDA_CONVERSATION_STORE=FAIL " + exception);
            return 1;
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }

    private static async Task RunAsync(string fixture)
    {
        if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        Directory.CreateDirectory(fixture);
        string root = Path.Combine(fixture, "transcripts");
        string worldA = CreateWorld(fixture, "world-a", 1, 100);
        string worldB = CreateWorld(fixture, "world-b", 1, 100);
        const string godotMetadata = "{\"format_version\":1.0,\"slot\":1.0,\"created_unix\":100.0,\"name\":\"Godot fixture\"}";
        string metadataPathA = Path.Combine(worldA, "world.json");
        await File.WriteAllTextAsync(metadataPathA, godotMetadata);
        var store = new PdaConversationStore(root);
        var scopeA = await store.BindAsync(worldA);
        Check(await File.ReadAllTextAsync(metadataPathA) == godotMetadata, "Godot decimal metadata preserved");
        WriteMetadata(worldA, 1, 100, "Integer fixture", 100);
        Check(scopeA == await store.BindAsync(worldA), "integer and Godot decimal metadata share identity");
        const string exponentMetadata = "{\"format_version\":1e0,\"slot\":1.0e0,\"created_unix\":1e2}";
        await File.WriteAllTextAsync(metadataPathA, exponentMetadata);
        Check(scopeA == await store.BindAsync(worldA), "integral exponent metadata shares identity");
        Check(await File.ReadAllTextAsync(metadataPathA) == exponentMetadata, "exponent metadata preserved");
        Check(scopeA == await new PdaConversationStore(root).BindAsync(worldA), "same slot identity stable across instances");
        Check(!Directory.Exists(root), "bind creates only world identity, no transcript directory");
        var turn = Turn("first");
        await store.AppendAsync(scopeA, turn);
        var reopened = new PdaConversationStore(root);
        Check((await reopened.LoadAsync(scopeA)).SequenceEqual(new[] { turn }), "round trip across store instances");
        var scopeB = await store.BindAsync(worldB);
        Check(scopeA.Id != scopeB.Id && (await store.LoadAsync(scopeB)).Count == 0, "different worlds isolated");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new PdaConversationStore(root).AppendAsync(scopeB, Turn("parallel-" + i))));
        Check((await store.LoadAsync(scopeB)).Count == 8, "concurrent store instances retain all appends");
        await store.ClearAsync(scopeB);
        await store.AppendAsync(scopeA, Turn("late"));
        Check((await store.LoadAsync(scopeA)).Count == 2 && (await store.LoadAsync(scopeB)).Count == 0, "late append uses captured scope");

        WriteMetadata(worldA, 1, 100, "Renamed", 999);
        Check(scopeA == await store.BindAsync(worldA), "mutable metadata does not change identity");
        File.Copy(Path.Combine(worldA, PdaConversationStore.IdentityFileName), Path.Combine(worldB, PdaConversationStore.IdentityFileName), true);
        var copied = await store.BindAsync(worldB);
        Check(copied.Id != scopeA.Id && copied.OwnerFingerprint != scopeA.OwnerFingerprint, "copied identity gets new namespace");
        Directory.Delete(worldA, true);
        CreateWorld(fixture, "world-a", 1, 100);
        var recreated = await store.BindAsync(worldA);
        Check(recreated.Id != scopeA.Id && (await store.LoadAsync(recreated)).Count == 0, "same-second delete/recreate gets new namespace");
        Check((await store.LoadAsync(scopeA)).Count == 2, "old captured scope remains isolated");

        for (int i = 0; i < PdaConversationStore.MaximumTurns + 3; i++) await store.AppendAsync(recreated, Turn("bound-" + i));
        var bounded = await store.LoadAsync(recreated);
        Check(bounded.Count == PdaConversationStore.MaximumTurns && bounded[0].Id == "bound-3", "oldest turns evicted");
        foreach (var invalid in new[]
        {
            turn with { Question = new string('q', 513) },
            turn with { Answer = new string('a', 1401) },
            turn with { ModelKey = new string('m', 97) },
            turn with { RecordedAtUtc = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1)) },
            turn with { SnapshotVersion = -1 },
            turn with { AnswerKind = (PdaAnswerKind)99 }
        }) await MustReject(() => store.AppendAsync(recreated, invalid), "invalid turn");
        Check((await store.LoadAsync(recreated)).Count == PdaConversationStore.MaximumTurns, "rejected turns preserve transcript");
        await store.ClearAsync(recreated);
        Check((await store.LoadAsync(recreated)).Count == 0 && !File.Exists(recreated.StoragePath), "clear removes transcript");
        Check(recreated == await store.BindAsync(worldA), "clear preserves world identity");

        await store.AppendAsync(recreated, turn with { Question = new string('q', 512), Answer = new string('a', 1400), ModelKey = new string('m', 96) });
        Check((await store.LoadAsync(recreated)).Count == 1, "maximum field sizes accepted");
        if (OperatingSystem.IsWindows()) await CheckLockedDestinationAsync(store, recreated);
        string valid = await File.ReadAllTextAsync(recreated.StoragePath);
        foreach (string malformed in new[] { "{", "null", valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":99"), new string('x', PdaConversationStore.MaximumDocumentBytes + 1) })
        {
            await File.WriteAllTextAsync(recreated.StoragePath, malformed);
            await MustReject(() => store.LoadAsync(recreated), "bad document load");
            await MustReject(() => store.AppendAsync(recreated, turn), "bad document append");
            await MustReject(() => store.ClearAsync(recreated), "bad document clear");
            Check(await File.ReadAllTextAsync(recreated.StoragePath) == malformed, "bad document preserved byte for byte");
        }
        await File.WriteAllTextAsync(recreated.StoragePath, valid);
        string identityPath = Path.Combine(worldA, PdaConversationStore.IdentityFileName);
        foreach (string identity in new[] { "{", "{\"schemaVersion\":99,\"id\":\"" + recreated.Id + "\",\"ownerFingerprint\":\"" + recreated.OwnerFingerprint + "\"}" })
        {
            await File.WriteAllTextAsync(identityPath, identity);
            await MustReject(() => store.BindAsync(worldA), "bad identity bind");
            Check(await File.ReadAllTextAsync(identityPath) == identity, "bad identity preserved");
        }
        string missing = Path.Combine(fixture, "missing-world");
        await MustReject(() => store.BindAsync(missing), "missing world");
        Check(!Directory.Exists(missing), "missing world never created");
        string invalidWorld = Path.Combine(fixture, "invalid-world");
        Directory.CreateDirectory(invalidWorld);
        await File.WriteAllTextAsync(Path.Combine(invalidWorld, "world.json"), "{}");
        await MustReject(() => store.BindAsync(invalidWorld), "invalid metadata");
        Check(!File.Exists(Path.Combine(invalidWorld, PdaConversationStore.IdentityFileName)), "invalid world never gains identity");
        foreach (string invalidMetadata in new[]
        {
            "{\"format_version\":1.5,\"slot\":1,\"created_unix\":100}",
            "{\"format_version\":1,\"slot\":1.5,\"created_unix\":100}",
            "{\"format_version\":1,\"slot\":1,\"created_unix\":100.5}",
            "{\"format_version\":1,\"slot\":1,\"created_unix\":100.000000000000000000000000000001}",
            "{\"format_version\":2.0,\"slot\":1.0,\"created_unix\":100.0}",
            "{\"format_version\":\"1\",\"slot\":1,\"created_unix\":100}",
            "{\"format_version\":1,\"slot\":2147483648.0,\"created_unix\":100}",
            "{\"format_version\":1,\"slot\":1,\"created_unix\":9223372036854775808.0}",
            "{\"format_version\":1,\"slot\":1,\"created_unix\":1e999}"
        })
        {
            string invalidPath = Path.Combine(invalidWorld, "world.json");
            await File.WriteAllTextAsync(invalidPath, invalidMetadata);
            await MustReject(() => store.BindAsync(invalidWorld), "invalid numeric or future world metadata");
            Check(await File.ReadAllTextAsync(invalidPath) == invalidMetadata, "invalid world metadata preserved");
            Check(!File.Exists(Path.Combine(invalidWorld, PdaConversationStore.IdentityFileName)), "invalid numeric world never gains identity");
        }
        await MustReject(() => store.LoadAsync(scopeA with { StoragePath = Path.Combine(fixture, "escape.json") }), "scope outside root");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await MustReject(() => store.AppendAsync(scopeA, turn, cancelled.Token), "cancelled append");
        Check((await store.LoadAsync(scopeA)).Count == 2, "cancelled append preserved transcript");
    }

    private static PdaConversationTurn Turn(string id) => new(id, DateTimeOffset.FromUnixTimeSeconds(1000), "Question?", "Answer.", "fixture-model", 7, DateTimeOffset.FromUnixTimeSeconds(999), PdaAnswerKind.Model);

    private static async Task CheckLockedDestinationAsync(PdaConversationStore store, PdaConversationScope scope)
    {
        byte[] original = await File.ReadAllBytesAsync(scope.StoragePath);
        int originalCount = (await store.LoadAsync(scope)).Count;
        string directory = Path.GetDirectoryName(scope.StoragePath)!;
        string temporaryPattern = Path.GetFileName(scope.StoragePath) + ".*.tmp";
        using (var destinationLock = new FileStream(scope.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var watch = Stopwatch.StartNew();
            bool sharingRejected = false;
            try { await store.AppendAsync(scope, Turn("locked-exhaustion")); }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) &&
                (exception.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021)))
            { sharingRejected = true; }
            Check(sharingRejected && watch.ElapsedMilliseconds >= 700, "destination lock exhausts bounded rename retries");
            Check((await File.ReadAllBytesAsync(scope.StoragePath)).SequenceEqual(original), "exhausted rename preserves old bytes");
            Check((await store.LoadAsync(scope)).Count == originalCount, "exhausted rename preserves old turns");
            Check(!Directory.EnumerateFiles(directory, temporaryPattern).Any(), "exhausted rename cleans owned temporary");
        }
        await store.AppendAsync(scope, Turn("after-lock-release"));
        var afterRelease = await store.LoadAsync(scope);
        Check(afterRelease.Count == originalCount + 1 && afterRelease.Count(t => t.Id == "after-lock-release") == 1,
            "released destination accepts exactly one append");

        byte[] beforeCancellation = await File.ReadAllBytesAsync(scope.StoragePath);
        using (var destinationLock = new FileStream(scope.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var cancellation = new CancellationTokenSource())
        using (var watcher = new FileSystemWatcher(directory, temporaryPattern))
        {
            var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Created += (_, change) => created.TrySetResult(change.FullPath);
            watcher.EnableRaisingEvents = true;
            Task append = store.AppendAsync(scope, Turn("locked-cancellation"), cancellation.Token);
            string temporary = await created.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // Observe the actual completed temp file instead of guessing a delay before cancellation.
            var readyDeadline = Stopwatch.StartNew();
            bool temporaryClosed = false;
            while (!temporaryClosed && !append.IsCompleted && readyDeadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                try
                {
                    using var completedTemporary = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.None);
                    temporaryClosed = completedTemporary.Length > 0;
                }
                catch (IOException) { await Task.Yield(); }
            }
            cancellation.Cancel();
            bool wasCancelled = false;
            try { await append; }
            catch (OperationCanceledException) { wasCancelled = true; }
            Check(temporaryClosed && wasCancelled, "cancellation interrupts rename retry after completed temporary");
            Check((await File.ReadAllBytesAsync(scope.StoragePath)).SequenceEqual(beforeCancellation), "cancelled rename preserves old bytes");
            Check((await store.LoadAsync(scope)).Count == originalCount + 1, "cancelled rename preserves old turns");
            Check(!Directory.EnumerateFiles(directory, temporaryPattern).Any(), "cancelled rename cleans owned temporary");
        }
    }

    private static string CreateWorld(string fixture, string name, int slot, long created)
    {
        string world = Path.Combine(fixture, name);
        Directory.CreateDirectory(world);
        WriteMetadata(world, slot, created, name, created);
        return world;
    }

    private static void WriteMetadata(string world, int slot, long created, string name, long played)
        => File.WriteAllText(Path.Combine(world, "world.json"), JsonSerializer.Serialize(new { format_version = 1, slot, created_unix = created, name, last_played_unix = played, generation_settings = new { changed = played } }));

    private static async Task MustReject(Func<Task> operation, string label)
    {
        try { await operation(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected rejection: " + label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
