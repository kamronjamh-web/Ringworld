using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RingworldTechDemo.Pda;

public sealed record PdaConversationScope(string Id, string OwnerFingerprint, string StoragePath);

/// <summary>Bounded, untrusted transcript storage. Recorded answers are never verified world facts.</summary>
public sealed class PdaConversationStore
{
    public const int MaximumTurns = 64;
    public const int MaximumDocumentBytes = 1024 * 1024;
    public const string IdentityFileName = ".pda_conversation_identity.json";
    private const int SchemaVersion = 1;
    // Serialize across store instances too, including lazy identity creation.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _memoryRoot;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Identity(int SchemaVersion, string Id, string OwnerFingerprint);
    private sealed record Document(int SchemaVersion, string Id, string OwnerFingerprint, List<PdaConversationTurn> Turns);

    public PdaConversationStore(string memoryRoot)
    {
        _memoryRoot = AbsolutePath(memoryRoot);
        RejectLinks(_memoryRoot);
    }

    public async Task<PdaConversationScope> BindAsync(string worldDirectory, CancellationToken token = default)
    {
        string world = AbsolutePath(worldDirectory);
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RejectLinks(world);
            if (!Directory.Exists(world)) throw new DirectoryNotFoundException("The active world directory does not exist.");
            using JsonDocument metadata = JsonDocument.Parse(await ReadBoundedAsync(Path.Combine(world, "world.json"), token).ConfigureAwait(false));
            JsonElement root = metadata.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format_version", out var version) || !TryGetIntegralNumber(version, int.MinValue, int.MaxValue, out long format) || format != 1 ||
                !root.TryGetProperty("slot", out var slot) || !TryGetIntegralNumber(slot, 1, int.MaxValue, out long slotId) ||
                !root.TryGetProperty("created_unix", out var created) || !TryGetIntegralNumber(created, 1, long.MaxValue, out long createdUnix))
                throw new InvalidDataException("World metadata has an unsupported schema or invalid identity fields.");
            string normalized = OperatingSystem.IsWindows() ? world.ToUpperInvariant() : world;
            string owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{normalized}\n{slotId}\n{createdUnix}")));
            string identityPath = Path.Combine(world, IdentityFileName);
            Identity? identity = null;
            if (File.Exists(identityPath))
            {
                identity = Deserialize<Identity>(await ReadBoundedAsync(identityPath, token).ConfigureAwait(false));
                if (identity.SchemaVersion != SchemaVersion || !ValidId(identity.Id) || !ValidOwner(identity.OwnerFingerprint))
                    throw new InvalidDataException("PDA identity has an unsupported schema or invalid fields; original preserved.");
            }
            if (identity == null || identity.OwnerFingerprint != owner)
            {
                bool replacing = identity != null;
                identity = new Identity(SchemaVersion, Guid.NewGuid().ToString("N"), owner);
                try
                {
                    await WriteAtomicAsync(identityPath, JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions), token, replacing).ConfigureAwait(false);
                }
                catch (IOException) when (!replacing && File.Exists(identityPath))
                {
                    // Another process may have won the atomic initial creation.
                    identity = Deserialize<Identity>(await ReadBoundedAsync(identityPath, token).ConfigureAwait(false));
                    if (identity.SchemaVersion != SchemaVersion || !ValidId(identity.Id) || identity.OwnerFingerprint != owner)
                        throw new InvalidDataException("Concurrent PDA identity is invalid or belongs to another owner; original preserved.");
                }
            }
            return new PdaConversationScope(identity.Id, owner, Path.Combine(_memoryRoot, identity.Id + ".json"));
        }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<PdaConversationTurn>> LoadAsync(PdaConversationScope scope, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try { return (await LoadDocumentAsync(scope, token).ConfigureAwait(false)).Turns.ToArray(); }
        finally { Gate.Release(); }
    }

    public async Task AppendAsync(PdaConversationScope scope, PdaConversationTurn turn, CancellationToken token = default)
    {
        ValidateTurn(turn);
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Document document = await LoadDocumentAsync(scope, token).ConfigureAwait(false);
            document.Turns.Add(turn);
            if (document.Turns.Count > MaximumTurns) document.Turns.RemoveRange(0, document.Turns.Count - MaximumTurns);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("PDA transcript exceeds the storage byte limit.");
            RejectLinks(_memoryRoot);
            Directory.CreateDirectory(_memoryRoot);
            await WriteAtomicAsync(scope.StoragePath, bytes, token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task ClearAsync(PdaConversationScope scope, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Validate the existing schema before clearing; unsupported/corrupt files must survive.
            await LoadDocumentAsync(scope, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Delete(scope.StoragePath);
        }
        finally { Gate.Release(); }
    }

    private async Task<Document> LoadDocumentAsync(PdaConversationScope scope, CancellationToken token)
    {
        ValidateScope(scope);
        if (!File.Exists(scope.StoragePath)) return new Document(SchemaVersion, scope.Id, scope.OwnerFingerprint, new());
        Document document = Deserialize<Document>(await ReadBoundedAsync(scope.StoragePath, token).ConfigureAwait(false));
        if (document.SchemaVersion != SchemaVersion || document.Id != scope.Id || document.OwnerFingerprint != scope.OwnerFingerprint ||
            document.Turns == null || document.Turns.Count > MaximumTurns)
            throw new InvalidDataException("PDA transcript has an unsupported schema, owner or turn count; original preserved.");
        foreach (PdaConversationTurn turn in document.Turns) ValidateTurn(turn);
        return document;
    }

    private void ValidateScope(PdaConversationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!ValidId(scope.Id) || !ValidOwner(scope.OwnerFingerprint) ||
            !string.Equals(scope.StoragePath, Path.Combine(_memoryRoot, scope.Id + ".json"), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("PDA scope does not belong to this storage root.");
        RejectLinks(scope.StoragePath);
    }

    private static void ValidateTurn(PdaConversationTurn turn)
    {
        if (turn == null || string.IsNullOrWhiteSpace(turn.Id) || turn.Id.Length > 96 ||
            string.IsNullOrWhiteSpace(turn.Question) || turn.Question.Length > 512 ||
            string.IsNullOrWhiteSpace(turn.Answer) || turn.Answer.Length > 1400 ||
            string.IsNullOrWhiteSpace(turn.ModelKey) || turn.ModelKey.Length > 96 ||
            turn.RecordedAtUtc == default || turn.RecordedAtUtc.Offset != TimeSpan.Zero ||
            turn.SnapshotCapturedAtUtc == default || turn.SnapshotCapturedAtUtc.Offset != TimeSpan.Zero ||
            turn.SnapshotVersion < 0 || !Enum.IsDefined(turn.AnswerKind))
            throw new InvalidDataException("PDA transcript turn has invalid or oversized fields.");
    }

    private static T Deserialize<T>(byte[] bytes) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("PDA document is null."); }
        catch (JsonException exception) { throw new InvalidDataException("PDA document is malformed; original preserved.", exception); }
    }

    private static bool ValidId(string? value) => value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out _);
    private static bool ValidOwner(string? value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool TryGetIntegralNumber(JsonElement element, long minimum, long maximum, out long result)
    {
        result = 0;
        // Godot writes integral metadata values as JSON decimals (for example 1.0).
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out decimal value) ||
            value != decimal.Truncate(value) || value < minimum || value > maximum) return false;
        // Decimal parsing may round very long fractional tails; reject those rather than accepting a rounded integer.
        string text = element.GetRawText();
        int exponentIndex = text.AsSpan().IndexOfAny('e', 'E');
        int mantissaEnd = exponentIndex < 0 ? text.Length : exponentIndex;
        int exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(text.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return false;
        int decimalIndex = text.IndexOf('.');
        long fractionalPlaces = (decimalIndex < 0 ? 0L : mantissaEnd - decimalIndex - 1L) - exponent;
        for (int index = mantissaEnd - 1; index >= 0 && fractionalPlaces > 0; index--)
        {
            if (text[index] is '.' or '-') continue;
            if (text[index] != '0') return false;
            fractionalPlaces--;
        }
        result = (long)value;
        return true;
    }

    private static string AbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("An explicit absolute filesystem path is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("PDA storage does not follow symbolic links or directory junctions.");
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > MaximumDocumentBytes) throw new InvalidDataException("PDA/world document exceeds the storage byte limit.");
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaximumDocumentBytes) throw new InvalidDataException("PDA/world document exceeds the storage byte limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token, bool overwrite = true)
    {
        RejectLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            int[] retryDelaysMilliseconds = { 25, 50, 100, 200, 400 };
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(path);
                try
                {
                    File.Move(temporary, path, overwrite);
                    break;
                }
                catch (Exception exception) when (OperatingSystem.IsWindows() &&
                    (exception is IOException or UnauthorizedAccessException) &&
                    (exception.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021)))
                {
                    if (attempt >= retryDelaysMilliseconds.Length) throw;
                    await Task.Delay(retryDelaysMilliseconds[attempt], token).ConfigureAwait(false);
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
