using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Loading;

namespace RingworldTechDemo.Vegetation;

public enum ImportedTreeCatalogLoadMode { Automatic, Source, PreparedRequired }

public static partial class ImportedTreeCatalog
{
    public const string PreparedResourcePath = "res://resources/trees/prepared/lolipop_catalog.res";
    public static ImportedTreeCatalogLoadMode LoadMode { get; private set; }
    public static string LoadedFrom { get; private set; } = "not_loaded";
    public static string FallbackReason { get; private set; } = "";
    public static int SourceSceneInstantiationCount { get; private set; }
    public static int SourceLodBakeCount { get; private set; }
    private static string _preparedPath = PreparedResourcePath;
    private static Task<PreparedTreeSourceIdentity>? _sourceIdentity;
    private static bool _unsafeSourceFallback;

    /// <summary>Tool-only selection before the first load; deliberately absent from gameplay settings.</summary>
    public static void ConfigureForDiagnostics(ImportedTreeCatalogLoadMode mode, string? preparedPath = null)
    {
        if (_variants != null) throw new InvalidOperationException("Configure tree diagnostics before loading; use a fresh process.");
        LoadMode = mode;
        _preparedPath = preparedPath ?? PreparedResourcePath;
    }

    private static Task<PreparedTreeSourceIdentity>? DevelopmentIdentity()
    {
        if (!OS.HasFeature("editor")) return null;
        if (_sourceIdentity == null)
        {
            string root = ProjectSettings.GlobalizePath("res://");
            _sourceIdentity = Task.Run(() => PreparedImportedTreeCatalogIdentity.ReadSourceIdentity(root));
        }
        return _sourceIdentity;
    }

    private static bool TryLoadPrepared(out IReadOnlyList<ImportedTreeVariant>? variants)
    {
        variants = null;
        if (!ResourceLoader.Exists(_preparedPath)) { FallbackReason = "prepared resource missing; source import freshness is not verified"; return false; }
        try
        {
            string? source = DevelopmentIdentity()?.GetAwaiter().GetResult().Fingerprint;
            var resource = ResourceLoader.Load<PreparedImportedTreeCatalog>(_preparedPath)
                ?? throw new InvalidOperationException("prepared resource could not be loaded");
            variants = HydratePrepared(resource, source);
            LoadedFrom = "prepared";
            return true;
        }
        catch (Exception exception) { FallbackReason = exception.Message; return false; }
    }

    private static IReadOnlyList<ImportedTreeVariant> HydratePrepared(PreparedImportedTreeCatalog resource, string? source)
    {
        _unsafeSourceFallback = source != null && resource.SourceFingerprint != source;
        var variants = PreparedImportedTreeCatalogValidation.Hydrate(resource, PreparedImportedTreeCatalogIdentity.CurrentEngineIdentity,
            PreparedImportedTreeCatalogIdentity.ImplementationIdentity, source);
        FallbackReason = "";
        return variants;
    }

    private static IEnumerable<bool> PreparePreparedCooperatively()
    {
        if (!ResourceLoader.Exists(_preparedPath)) { FallbackReason = "prepared resource missing; source import freshness is not verified"; yield break; }
        using var claims = new RingThreadedResourceClaims();
        Task<PreparedTreeSourceIdentity>? identity = DevelopmentIdentity();
        Error error = claims.Request(_preparedPath);
        if (error != Error.Ok) { FallbackReason = "prepared request failed: " + error; yield break; }
        while (true)
        {
            ResourceLoader.ThreadLoadStatus status = ResourceLoader.LoadThreadedGetStatus(_preparedPath);
            if (status == ResourceLoader.ThreadLoadStatus.Loaded) break;
            if (status is ResourceLoader.ThreadLoadStatus.Failed or ResourceLoader.ThreadLoadStatus.InvalidResource)
            {
                FallbackReason = "prepared request failed: " + status;
                yield break;
            }
            yield return true;
        }
        while (identity != null && !identity.IsCompleted) yield return true;
        IReadOnlyList<ImportedTreeVariant>? prepared = null;
        try
        {
            var resource = claims.Take(_preparedPath) as PreparedImportedTreeCatalog
                ?? throw new InvalidOperationException("prepared resource has wrong type");
            prepared = HydratePrepared(resource, identity?.GetAwaiter().GetResult().Fingerprint);
        }
        catch (Exception exception) { FallbackReason = exception.Message; }
        if (prepared == null) yield break;
        _variants ??= prepared;
        LoadedFrom = "prepared";
    }

    private static void RequireSourceFallback()
    {
        if (LoadMode == ImportedTreeCatalogLoadMode.PreparedRequired)
            throw new InvalidOperationException("Prepared tree catalog required: " + FallbackReason);
        if (_unsafeSourceFallback)
            throw new InvalidOperationException("Tree authoring inputs changed. Run tools/prepare_tree_assets.ps1 to force scene reimport and rebuild; cached source imports cannot establish fresh geometry.");
        if (LoadMode == ImportedTreeCatalogLoadMode.Source) FallbackReason = "explicit source diagnostics; source import freshness is not verified";
        GD.PushWarning("Imported trees use source preparation: " + FallbackReason);
    }
}
