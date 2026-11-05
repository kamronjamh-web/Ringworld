using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Vegetation;

namespace RingworldTechDemo.Tools;

/// <summary>Fresh-process tree loading diagnostics; see imported_tree_asset_profile.gd for usage.</summary>
public partial class ImportedTreeAssetProfile : RefCounted
{
    public string Result { get; private set; } = "RUNNING";
    private readonly List<object> _phases = new();
    private readonly List<double> _steps = new(4096);
    private IEnumerator<bool>? _loader;
    private string _mode = "", _output = "", _phase = "catalog";
    private bool _impostor, _begun;
    private long _started, _allocated, _stepAllocated;
    private object? _memoryBefore;

    private static ImportedTreeCatalogLoadMode ParseLoader(string loader) => loader switch
    {
        "source" => ImportedTreeCatalogLoadMode.Source,
        "prepared-required" => ImportedTreeCatalogLoadMode.PreparedRequired,
        "auto" => ImportedTreeCatalogLoadMode.Automatic,
        _ => throw new ArgumentException("loader must be source, prepared-required or auto")
    };

    /// <summary>Gameplay capture bridge; select before creating the game, without loading or inspecting meshes.</summary>
    public Godot.Collections.Dictionary ConfigureGameplayLoader(string loader)
    {
        try
        {
            ImportedTreeCatalog.ConfigureForDiagnostics(ParseLoader(loader));
            var diagnostics = GetGameplayLoaderDiagnostics();
            diagnostics["configured"] = true;
            return diagnostics;
        }
        catch (Exception exception)
        {
            return new Godot.Collections.Dictionary { ["configured"] = false, ["error"] = exception.Message };
        }
    }

    public Godot.Collections.Dictionary GetGameplayLoaderDiagnostics() => new()
    {
        ["actual_loaded_from"] = ImportedTreeCatalog.LoadedFrom,
        ["mode"] = ImportedTreeCatalog.LoadMode switch
        {
            ImportedTreeCatalogLoadMode.Source => "source",
            ImportedTreeCatalogLoadMode.PreparedRequired => "prepared-required",
            _ => "auto"
        },
        ["fallback_reason"] = ImportedTreeCatalog.FallbackReason,
        ["source_scene_instantiations"] = ImportedTreeCatalog.SourceSceneInstantiationCount,
        ["source_lod_bakes"] = ImportedTreeCatalog.SourceLodBakeCount
    };

    public void BeginProfile(string mode, string output, bool impostor, string loader)
    {
        try
        {
            if (_begun) throw new InvalidOperationException("Use a fresh process for every profile.");
            _begun = true;
            if (mode != "sync" && mode != "cooperative") throw new ArgumentException("mode must be sync or cooperative");
            _mode = mode;
            ImportedTreeCatalog.ConfigureForDiagnostics(ParseLoader(loader));
            _impostor = impostor;
            _output = ValidateOutput(output);
            StartPhase();
        }
        catch (Exception exception) { Fail(exception); }
    }

    // The wrapper calls this exactly once per frame. Timing excludes the wrapper and frame wait.
    public bool AdvanceStep()
    {
        if (Result != "RUNNING") return false;
        try
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            bool more;
            try
            {
                if (_mode == "cooperative") more = _loader!.MoveNext();
                else
                {
                    if (_phase == "catalog") _ = ImportedTreeCatalog.Load();
                    else _ = ImportedTreeImpostorCatalog.Load();
                    more = false;
                }
            }
            finally
            {
                _steps.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                _stepAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
            if (more) return true;
            double elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            long allocatedTotal = GC.GetTotalAllocatedBytes(true) - _allocated;
            object memoryAfter = MemorySnapshot();
            _loader?.Dispose();
            _loader = null;
            double[] sorted = _steps.OrderBy(x => x).ToArray();
            _phases.Add(new
            {
                phase = _phase,
                wall_elapsed_ms = elapsed,
                active_step_total_ms = _steps.Sum(),
                step_count = _steps.Count,
                step_max_ms = sorted[^1],
                step_p95_ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
                managed_allocated_all_threads_bytes = allocatedTotal,
                managed_allocated_in_steps_current_thread_bytes = _stepAllocated,
                memory_before = _memoryBefore,
                memory_after = memoryAfter
            });
            if (_phase == "catalog" && _impostor)
            {
                _phase = "impostor";
                StartPhase();
                return true;
            }
            WriteReport("PASS", null);
            Result = "PASS";
            GD.Print($"IMPORTED_TREE_ASSET_PROFILE=PASS report={_output}");
        }
        catch (Exception exception) { Fail(exception); }
        return false;
    }

    private void StartPhase()
    {
        _steps.Clear();
        _stepAllocated = 0;
        _memoryBefore = MemorySnapshot();
        _loader = _mode == "cooperative"
            ? (_phase == "catalog" ? ImportedTreeCatalog.PrepareCooperatively() : ImportedTreeImpostorCatalog.PrepareCooperatively()).GetEnumerator()
            : null;
        _allocated = GC.GetTotalAllocatedBytes(true);
        _started = Stopwatch.GetTimestamp();
    }

    private static object MemorySnapshot()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new
        {
            working_set_bytes = process.WorkingSet64,
            private_bytes = process.PrivateMemorySize64,
            peak_working_set_bytes = process.PeakWorkingSet64,
            managed_heap_bytes = GC.GetTotalMemory(false)
        };
    }

    private object Inventory()
    {
        var meshes = new HashSet<ulong>();
        var materials = new HashSet<ulong>();
        var textures = new HashSet<ulong>();
        var meshClasses = new SortedDictionary<string, int>();
        var materialClasses = new SortedDictionary<string, int>();
        var textureClasses = new SortedDictionary<string, int>();
        var texturePaths = new SortedSet<string>(StringComparer.Ordinal);
        int surfaces = 0;
        void AddResource(Resource resource, HashSet<ulong> ids, SortedDictionary<string, int> classes)
        {
            if (!ids.Add(resource.GetInstanceId())) return;
            string name = resource.GetClass().ToString();
            classes[name] = classes.GetValueOrDefault(name) + 1;
        }
        void AddMaterial(Material? material)
        {
            if (material == null || materials.Contains(material.GetInstanceId())) return;
            AddResource(material, materials, materialClasses);
            if (material is not ShaderMaterial shaderMaterial || shaderMaterial.Shader == null) return;
            foreach (Variant uniform in shaderMaterial.Shader.GetShaderUniformList())
            {
                Variant value = shaderMaterial.GetShaderParameter(uniform.AsGodotDictionary()["name"].AsString());
                if (value.VariantType != Variant.Type.Object || value.AsGodotObject() is not Texture texture) continue;
                AddResource(texture, textures, textureClasses);
                if (!string.IsNullOrEmpty(texture.ResourcePath)) texturePaths.Add(texture.ResourcePath);
            }
        }
        void AddMesh(Mesh mesh)
        {
            if (meshes.Contains(mesh.GetInstanceId())) return;
            AddResource(mesh, meshes, meshClasses);
            surfaces += mesh.GetSurfaceCount();
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++) AddMaterial(mesh.SurfaceGetMaterial(surface));
        }
        IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
        if (variants.Count != ImportedTreeCatalog.VariantCount) throw new InvalidOperationException("Incomplete catalog.");
        foreach (ImportedTreeVariant variant in variants)
            foreach (Mesh mesh in variant.Lods) AddMesh(mesh);
        object catalog = new { variant_count = variants.Count, mesh_count = meshes.Count, surface_count = surfaces,
            material_count = materials.Count, texture_count = textures.Count,
            mesh_classes = new SortedDictionary<string, int>(meshClasses),
            material_classes = new SortedDictionary<string, int>(materialClasses),
            texture_classes = new SortedDictionary<string, int>(textureClasses), texture_paths = texturePaths.ToArray() };
        if (!_impostor) return new { catalog, impostor = (object?)null };
        meshes.Clear(); materials.Clear(); textures.Clear(); meshClasses.Clear(); materialClasses.Clear(); textureClasses.Clear(); texturePaths.Clear(); surfaces = 0;
        ImportedTreeImpostorCatalog impostor = ImportedTreeImpostorCatalog.Load();
        AddMesh(impostor.Mesh);
        return new { catalog, impostor = (object)new { variant_count = impostor.VariantCount,
            vertex_count = impostor.VertexCount, mesh_count = meshes.Count, surface_count = surfaces,
            material_count = materials.Count, texture_count = textures.Count,
            mesh_classes = meshClasses, material_classes = materialClasses, texture_classes = textureClasses } };
    }

    private void WriteReport(string status, string? error)
    {
        object? inventory = status == "PASS" ? Inventory() : null;
        var report = new
        {
            schema = 1, status, error, mode = _mode, include_impostor = _impostor,
            utc = DateTime.UtcNow.ToString("O"), process_id = System.Environment.ProcessId,
            engine = Engine.GetVersionInfo()["string"].AsString(),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            catalog_version = ImportedTreeCatalog.CatalogVersion,
            catalog_loader = ImportedTreeCatalog.LoadedFrom,
            fallback_reason = ImportedTreeCatalog.FallbackReason,
            source_scene_instantiations = ImportedTreeCatalog.SourceSceneInstantiationCount,
            source_lod_bakes = ImportedTreeCatalog.SourceLodBakeCount,
            notes = "One fresh process per run. OS/import/shader caches are not flushed. Cooperative steps include polls and the final MoveNext(false). Wall time includes frame waits; active time measures loader calls only. All-thread allocation includes frame/background work and profiler overhead. Process memory is resident/private CPU memory, not VRAM. Inventory runs after timed phases. Impostor phase reuses the just-loaded catalog.",
            phases = _phases, inventory
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        File.WriteAllText(_output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string ValidateOutput(string output)
    {
        string path = Path.GetFullPath(ProjectSettings.GlobalizePath(output));
        string[] roots = { ProjectSettings.GlobalizePath("res://.godot/"), ProjectSettings.GlobalizePath("user://generated/") };
        if (!roots.Any(root => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Output must be under res://.godot/ or user://generated/.");
        if (File.Exists(path)) throw new IOException($"Refusing to overwrite existing report: {path}");
        return path;
    }

    private void Fail(Exception exception)
    {
        _loader?.Dispose();
        _loader = null;
        Result = "FAIL " + exception.Message;
        if (!string.IsNullOrEmpty(_output))
        {
            try { WriteReport("FAIL", exception.ToString()); }
            catch (Exception writeException) { GD.PushError(writeException.ToString()); }
        }
        GD.PushError($"IMPORTED_TREE_ASSET_PROFILE=FAIL {exception}");
    }
}
