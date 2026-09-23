using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Biomes;

namespace RingworldTechDemo.Tools;

/// <summary>Isolated, cancellable authoring preview. It never mutates a world.</summary>
[Tool]
public partial class BiomeAuthoringPreview : Node3D
{
    public const string ScenePath = "res://scenes/BiomeAuthoringPreview.tscn";
    [Export] public BiomeAuthoringPreset? Preset { get; set; }
    public bool IsPreview => true;
    public bool IsPreviewReady => _builtRevision == _requestedRevision && _builder == null && _error.Length == 0;

    private Node3D? _published;
    private Node3D? _buildingRoot;
    private Camera3D? _camera;
    private Label? _status;
    private IEnumerator? _builder;
    private BiomeAuthoringSampler? _activeSampler;
    private BiomeAuthoringSnapshot? _activeProfile;
    private BiomeAuthoringSnapshot? _publishedProfile;
    private int _requestedRevision;
    private int _builtRevision = -1;
    private int _publishedTerrainVertices;
    private int _publishedTrees;
    private int _publishedGrass;
    private Aabb _publishedAabb;
    private string _error = "";
    private double _debounceSeconds;
    private long _buildItems;
    private long _buildTotal;
    private long _processFrames;
    private string _buildStage = "idle";

    public override void _Ready()
    {
        _camera = GetNodeOrNull<Camera3D>("Camera3D");
        _status = GetNodeOrNull<Label>("CanvasLayer/Status");
        ProcessMode = ProcessModeEnum.Always;
        SetProcess(true);
        if (Preset == null)
            Preset = ResourceLoader.Load<BiomeAuthoringPreset>("res://resources/biomes/temperate_woodland.tres")?.Duplicate(true) as BiomeAuthoringPreset;
        RequestRebuild();
        if (!Engine.IsEditorHint()) FrameOverview();
    }

    public override void _Process(double delta)
    {
        _debounceSeconds -= delta;
        if (_builder == null && _error.Length == 0 && _requestedRevision != _builtRevision && _debounceSeconds <= 0)
            StartBuild();
        if (_builder != null)
            AdvanceBuild();
        if (!Engine.IsEditorHint())
            RuntimeInput((float)delta);
    }

    public void SetPreset(BiomeAuthoringPreset preset)
    {
        Preset = preset?.Duplicate(true) as BiomeAuthoringPreset;
        RequestRebuild();
    }

    public void RequestRebuild()
    {
        _requestedRevision++;
        _error = "";
        _debounceSeconds = .15;
        CancelBuild();
        SetStatus("Biome preview queued…");
    }

    private void StartBuild()
    {
        if (Preset == null)
        {
            _error = "No biome preset is selected.";
            SetStatus("Biome preview error: " + _error);
            return;
        }
        if (!Preset.TryValidate(out _error))
        {
            SetStatus("Biome preview error: " + _error);
            return;
        }
        _activeProfile = Preset.CreateSnapshot();
        _activeSampler = new BiomeAuthoringSampler(_activeProfile.Value);
        _buildItems = 0;
        _processFrames = 0;
        _buildStage = "terrain heights";
        _builder = Build(_requestedRevision, _activeSampler, _activeProfile.Value).GetEnumerator();
    }

    private void AdvanceBuild()
    {
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            int items = 0;
            _processFrames++;
            while (items++ < 8192 && watch.Elapsed.TotalMilliseconds < 2)
            {
                IEnumerator iterator = _builder!;
                if (iterator.MoveNext()) continue;
                (iterator as IDisposable)?.Dispose();
                if (ReferenceEquals(_builder, iterator))
                {
                    _builder = null;
                    _activeSampler?.Dispose();
                    _activeSampler = null;
                    _activeProfile = null;
                }
                break;
            }
            if (_builder != null)
                SetStatus(_buildStage == "vegetation"
                    ? "Building vegetation (previous preview retained)…"
                    : $"Building {_buildStage}: {_buildItems:n0}/{_buildTotal:n0} items (previous preview retained)");
        }
        catch (Exception error)
        {
            _error = error.Message;
            CancelBuild();
            SetStatus("Biome preview error: " + _error);
        }
    }

    private void CancelBuild()
    {
        (_builder as IDisposable)?.Dispose();
        _builder = null;
        _activeSampler?.Dispose();
        _activeSampler = null;
        _activeProfile = null;
        if (_buildingRoot != null)
        {
            _buildingRoot.Free();
            _buildingRoot = null;
        }
    }

    private IEnumerable Build(int revision, BiomeAuthoringSampler sampler, BiomeAuthoringSnapshot profile)
    {
        List<float> axis = CreateAdaptiveAxis();
        int width = axis.Count;
        int vertexCount = width * width;
        _buildTotal = vertexCount * 2L;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];

        for (int z = 0; z < width; z++)
        for (int x = 0; x < width; x++)
        {
            if (revision != _requestedRevision) yield break;
            int index = z * width + x;
            float height = sampler.SampleSurfaceHeight(axis[x], axis[z]);
            vertices[index] = new Vector3(axis[x], height, axis[z]);
            uvs[index] = new Vector2(axis[x], axis[z]) / profile.GroundTextureScale;
            _buildItems++; yield return 0;
        }

        _buildStage = "terrain normals";
        for (int z = 0; z < width; z++)
        for (int x = 0; x < width; x++)
        {
            int index = z * width + x;
            int left = Math.Max(0, x - 1), right = Math.Min(width - 1, x + 1);
            int down = Math.Max(0, z - 1), up = Math.Min(width - 1, z + 1);
            float dx = (vertices[z * width + right].Y - vertices[z * width + left].Y) / (axis[right] - axis[left]);
            float dz = (vertices[up * width + x].Y - vertices[down * width + x].Y) / (axis[up] - axis[down]);
            normals[index] = new Vector3(-dx, 1, -dz).Normalized();
            _buildItems++; yield return 0;
        }

        int[] indices = new int[(width - 1) * (width - 1) * 6];
        int output = 0;
        for (int z = 0; z < width - 1; z++)
        for (int x = 0; x < width - 1; x++)
        {
            int a = z * width + x;
            // Godot's terrain winding is clockwise when viewed from above.
            indices[output++] = a; indices[output++] = a + 1; indices[output++] = a + width;
            indices[output++] = a + 1; indices[output++] = a + width + 1; indices[output++] = a + width;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _buildingRoot = new Node3D { Name = "GeneratedAuthoringPreview", Owner = null };
        _buildingRoot.AddChild(new MeshInstance3D { Name = "Adaptive10kmContext", Mesh = mesh, MaterialOverride = CreateGroundMaterial(profile) });
        if (profile.MeanElevationMeters - profile.ElevationVariationMeters - profile.DetailAmplitudeMeters <= 0)
        {
            _buildingRoot.AddChild(new MeshInstance3D
            {
                Name = "AuthoredSeaPlane",
                Mesh = new PlaneMesh { Size = new Vector2(10000, 10000) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(.08f, .25f, .62f, .55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = .25f }
            });
        }

        _buildStage = "vegetation";
        foreach (int _ in BuildVegetation(_buildingRoot, sampler, profile))
        {
            _buildItems++;
            yield return 0;
        }

        if (revision != _requestedRevision) yield break;
        Node3D complete = _buildingRoot;
        _buildingRoot = null;
        if (_published != null)
        {
            RemoveChild(_published);
            _published.QueueFree();
        }
        AddChild(complete);
        _published = complete;
        _publishedProfile = profile;
        _publishedTerrainVertices = vertexCount;
        _publishedAabb = mesh.GetAabb();
        _publishedTrees = CountPrefix(complete, "PreviewTrees_");
        _publishedGrass = complete.GetNodeOrNull<MultiMeshInstance3D>("PreviewGrass")?.Multimesh?.InstanceCount ?? 0;
        _builtRevision = revision;
        SetStatus($"Ready: {profile.DisplayName} | trees {_publishedTrees}, grass {_publishedGrass}\nO/F frame | RMB look | WASD/QE fly | Shift fast");
    }

    private static int CountPrefix(Node root, string prefix)
    {
        int count = 0;
        foreach (Node child in root.GetChildren())
            if (child.Name.ToString().StartsWith(prefix, StringComparison.Ordinal) && child is MultiMeshInstance3D trees)
                count += trees.Multimesh?.InstanceCount ?? 0;
        return count;
    }

    private static List<float> CreateAdaptiveAxis()
    {
        var values = new SortedSet<float>();
        for (int x = -5000; x <= 5000; x += 250) values.Add(x);
        for (int x = -128; x <= 128; x += 4) values.Add(x);
        return new List<float>(values);
    }

    private static Material CreateGroundMaterial(BiomeAuthoringSnapshot profile) => new StandardMaterial3D
    {
        AlbedoTexture = ResourceLoader.Load<Texture2D>(profile.GroundTexturePath),
        AlbedoColor = profile.GroundTint,
        Roughness = .92f
    };

    public Godot.Collections.Dictionary GetPreviewDiagnostics()
    {
        BiomeAuthoringSnapshot? profile = _publishedProfile;
        return new()
        {
            ["ready"] = IsPreviewReady,
            ["requested_revision"] = _requestedRevision,
            ["built_revision"] = _builtRevision,
            ["extent_meters"] = 10000,
            ["tree_count"] = _publishedTrees,
            ["grass_count"] = _publishedGrass,
            ["terrain_vertex_count"] = _publishedTerrainVertices,
            ["error"] = _error,
            ["builder_active"] = _builder != null,
            ["process_frames"] = _processFrames,
            ["build_items"] = _buildItems,
            ["build_total"] = _buildTotal,
            ["build_stage"] = _buildStage,
            ["debounce_remaining"] = Math.Max(0, _debounceSeconds),
            ["preset_name"] = profile?.DisplayName ?? "",
            ["seed"] = profile?.Seed ?? 0,
            ["elevation"] = profile?.MeanElevationMeters ?? 0,
            ["grass_height"] = profile?.GrassHeightMeters ?? 0,
            ["grass_tint"] = profile?.GrassTint ?? Colors.White,
            ["ground_material"] = (int)(profile?.GroundMaterial ?? BiomeGroundMaterial.GrassSoil),
            ["published_aabb"] = _publishedAabb
        };
    }

    private void SetStatus(string value)
    {
        if (_status != null) _status.Text = value;
    }

    public override void _ExitTree()
    {
        CancelBuild();
        ReleasePreviewMouse();
    }
}
