using System;
using System.Collections.Generic;
using System.Linq;

namespace RingworldTechDemo.Vegetation;

public enum TreeFormKind : byte { Tree = 0, Snag = 1, FallenTree = 2, Stump = 3, Shrub = 4 }          // append-only
public enum TreeSizeClass : byte { Sapling = 0, Small = 1, Medium = 2, Big = 3, Large = 4 }             // append-only
public enum TreeSourceCatalog : byte { LolipopBase = 0, T6Derived = 1 }                                 // T8 appends
public enum TreePlacementRole : byte { Canopy = 0, Understory = 1, DeadWood = 2, Shrub = 3 }            // append-only

public sealed record ImportedTreeSourceRow(
    int Index, TreeSpecies Species, TreeFormKind Form, TreeSizeClass SizeClass, int AgeRank,
    TreePlacementRole Role, TreeSourceCatalog Catalog, string SourceName, int SourceBaseVariant,  // -1 for base rows
    string? EnablingFeature,                                                                       // null for the 30 base rows
    ImportedTreeSourceImport? Import = null);                                                      // T8-3 fills it

public sealed record ImportedTreeSourceImport(string Folder, int ExpectedVariantCount, float TrunkHeightFraction,
    float TrunkRadiusFraction, string? MaterialQuirk);                                             // reserved for T8-3, unused by T6

/// <summary>
/// T6 design A1: one source of truth for what each placeable tree index is (species, form, size class, age rank, role, owning catalog and enabling feature).
/// Plain C#, no Godot types, usable on workers. Size class, age rank and role are pure functions of the index and are pinned against the catalog's variant names by forest_structure_test.
/// The 30 base rows are the LOLIPOP catalog; derived rows 30-42 (T6-5: snags, fallen trees, stumps) are enabled by vegetation.forest_structure. Generation 1 placement keeps its own literal ranges and never reads this table.
/// </summary>
public sealed class TreeSourceTable
{
    private readonly ImportedTreeSourceRow[] _rows;
    private readonly Dictionary<int, ImportedTreeSourceRow> _byIndex = new();
    private readonly Dictionary<(TreeSpecies, TreeSizeClass), int[]> _canopy = new();
    private readonly Dictionary<(TreeSpecies, TreePlacementRole), int[]> _ranges = new();

    internal TreeSourceTable(IEnumerable<ImportedTreeSourceRow> rows)
    {
        _rows = rows.OrderBy(row => row.Index).ToArray();
        foreach (ImportedTreeSourceRow row in _rows)
            if (!_byIndex.TryAdd(row.Index, row)) throw new ArgumentException($"Duplicate tree source row {row.Index}.", nameof(rows));
        foreach (var group in _rows.GroupBy(row => (row.Species, row.Role)))
            _ranges[group.Key] = group.Select(row => row.Index).ToArray();
        foreach (var group in _rows.Where(row => row.Role == TreePlacementRole.Canopy).GroupBy(row => (row.Species, row.SizeClass)))
            _canopy[group.Key] = group.Select(row => row.Index).ToArray();
    }

    public IReadOnlyList<ImportedTreeSourceRow> Rows => _rows;

    public ImportedTreeSourceRow Row(int index)
        => _byIndex.TryGetValue(index, out ImportedTreeSourceRow? row) ? row : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>The base rows plus every row whose enabling feature is in the set, in index order.</summary>
    public IReadOnlyList<ImportedTreeSourceRow> RowsEnabled(IReadOnlySet<string> featureSet)
        => _rows.Where(row => row.EnablingFeature == null || featureSet.Contains(row.EnablingFeature)).ToArray();

    /// <summary>Indices of the canopy rows of one species and size class, ascending. Empty when the class has none. The result is shared: do not modify it.</summary>
    public IReadOnlyList<int> CanopyVariants(TreeSpecies species, TreeSizeClass sizeClass)
        => _canopy.TryGetValue((species, sizeClass), out int[]? list) ? list : Array.Empty<int>();

    /// <summary>Indices of the rows of one species and placement role, ascending (not necessarily contiguous: the pine canopy skips the saplings). The result is shared: do not modify it.</summary>
    public IReadOnlyList<int> Range(TreeSpecies species, TreePlacementRole role)
        => _ranges.TryGetValue((species, role), out int[]? list) ? list : Array.Empty<int>();

    /// <summary>The index exists, its row is enabled by the feature set, its role is Canopy and its species matches.</summary>
    public bool IsCanopySelectable(int index, TreeSpecies species, IReadOnlySet<string> featureSet)
        => _byIndex.TryGetValue(index, out ImportedTreeSourceRow? row)
            && row.Role == TreePlacementRole.Canopy && row.Species == species
            && (row.EnablingFeature == null || featureSet.Contains(row.EnablingFeature));
}

public static class ImportedTreeSourceTable
{
    public static TreeSourceTable Production { get; } = new(AllRows());

    public static IReadOnlyList<ImportedTreeSourceRow> Rows => Production.Rows;
    public static ImportedTreeSourceRow Row(int index) => Production.Row(index);
    public static IReadOnlyList<ImportedTreeSourceRow> RowsEnabled(IReadOnlySet<string> featureSet) => Production.RowsEnabled(featureSet);
    public static IReadOnlyList<int> CanopyVariants(TreeSpecies species, TreeSizeClass sizeClass) => Production.CanopyVariants(species, sizeClass);
    public static IReadOnlyList<int> Range(TreeSpecies species, TreePlacementRole role) => Production.Range(species, role);
    public static bool IsCanopySelectable(int index, TreeSpecies species, IReadOnlySet<string> featureSet) => Production.IsCanopySelectable(index, species, featureSet);

    /// <summary>A table over caller-supplied rows (a synthetic row enabled by an unused feature, for T6-5's append test).</summary>
    public static TreeSourceTable CreateForTests(IEnumerable<ImportedTreeSourceRow> rows) => new(rows);

    public const string DerivedFeature = "vegetation.forest_structure";

    private static IEnumerable<ImportedTreeSourceRow> AllRows()
    {
        List<ImportedTreeSourceRow> rows = BaseRows().ToList();
        rows.AddRange(DerivedRows(rows));
        return rows;
    }

    /// <summary>T6-5 derived rows 30-42 (A1): snags 30-35, fallen trees 36-39, stumps 40-42; species, class and age rank are the source variant's. Shrubs (43-54) come with T6-7. Role is DeadWood, so they are never canopy.</summary>
    private static IEnumerable<ImportedTreeSourceRow> DerivedRows(IReadOnlyList<ImportedTreeSourceRow> baseRows)
    {
        var forms = new (TreeFormKind Form, string Prefix, int[] Sources)[]
        {
            (TreeFormKind.Snag, "Snag:", new[] { 6, 7, 3, 15, 18, 12 }),
            (TreeFormKind.FallenTree, "Fallen:", new[] { 6, 8, 21, 23 }),
            (TreeFormKind.Stump, "Stump:", new[] { 7, 22, 12 }),
        };
        int index = 30;
        foreach (var (form, prefix, sources) in forms)
            foreach (int source in sources)
            {
                ImportedTreeSourceRow b = baseRows[source];
                yield return new ImportedTreeSourceRow(index++, b.Species, form, b.SizeClass, b.AgeRank, TreePlacementRole.DeadWood, TreeSourceCatalog.T6Derived, prefix + b.SourceName, source, DerivedFeature);
            }
    }

    private static IEnumerable<ImportedTreeSourceRow> BaseRows()
    {
        // Sorted-name order of the LOLIPOP packs (docs/work-orders/terrain-vegetation/T6.md P4). Fir has no size in its names; it is inferred by height (12 large 9.3 m, 13 medium 4.2 m, 14 small 1.5 m).
        // Fir 13 is the fir canopy "medium" class in the structured path; fir 14 and the saplings are understory.
        var rows = new List<ImportedTreeSourceRow>(30);
        void Add(int first, int count, TreeSpecies species, TreeSizeClass size, int rank, string prefix, bool numbered = true)
        {
            TreePlacementRole role = size == TreeSizeClass.Sapling || (species == TreeSpecies.Fir && size == TreeSizeClass.Small) ? TreePlacementRole.Understory : TreePlacementRole.Canopy;
            for (int i = 0; i < count; i++)
                rows.Add(new ImportedTreeSourceRow(first + i, species, TreeFormKind.Tree, size, rank, role, TreeSourceCatalog.LolipopBase,
                    numbered ? prefix + (i + 1) : prefix, -1, null));
        }
        Add(0, 3, TreeSpecies.Maple, TreeSizeClass.Sapling, 0, "Acer_Sapling_");
        Add(3, 3, TreeSpecies.Maple, TreeSizeClass.Large, 3, "Acer_large_");
        Add(6, 3, TreeSpecies.Maple, TreeSizeClass.Medium, 2, "Acer_medium_");
        Add(9, 3, TreeSpecies.Maple, TreeSizeClass.Small, 1, "Acer_small_");
        Add(12, 1, TreeSpecies.Fir, TreeSizeClass.Large, 2, "Christmas tree", false);
        Add(13, 1, TreeSpecies.Fir, TreeSizeClass.Medium, 1, "Christmas tree_2", false);
        Add(14, 1, TreeSpecies.Fir, TreeSizeClass.Small, 0, "Christmas tree_3", false);
        Add(15, 3, TreeSpecies.Pine, TreeSizeClass.Big, 3, "Pine_big_");
        Add(18, 3, TreeSpecies.Pine, TreeSizeClass.Large, 4, "Pine_large_");
        Add(21, 3, TreeSpecies.Pine, TreeSizeClass.Medium, 2, "Pine_medium_");
        Add(24, 3, TreeSpecies.Pine, TreeSizeClass.Sapling, 0, "Pine_sapling_");
        Add(27, 3, TreeSpecies.Pine, TreeSizeClass.Small, 1, "Pine_small_");
        return rows;
    }
}
