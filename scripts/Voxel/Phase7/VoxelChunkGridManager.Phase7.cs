using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using RingworldTechDemo.Voxel.Phase5;
using RingworldTechDemo.Voxel.Phase7;

namespace RingworldTechDemo.Voxel.Phase2;

public partial class VoxelChunkGridManager
{
    [Export] public bool EnablePhase7CommandJournal { get; set; } = false;
    [Export] public bool IncludeRejectedCommandsInPhase7Journal { get; set; } = true;
    [Export(PropertyHint.Range, "16,50000,1")] public int MaxPhase7JournalEntries { get; set; } = 4096;
    [Export] public bool LogPhase7ReplayValidation { get; set; } = false;
    [Export] public bool ResetPhase7JournalOnGridRebuild { get; set; } = true;

    private readonly List<VoxelCommandJournalEntry> _phase7CommandJournal = new();
    private VoxelChunkDeltaSnapshot? _phase7BaselineSnapshot;
    private bool _phase7IsReplaying;

    public event Action<VoxelReplayValidationReport>? Phase7ReplayValidationCompleted;

    public int GetPhase7JournalCount()
    {
        return _phase7CommandJournal.Count;
    }

    public bool HasPhase7ReplayBaseline()
    {
        return _phase7BaselineSnapshot != null;
    }

    public IReadOnlyList<VoxelCommandJournalEntry> BuildPhase7CommandJournalSnapshot()
    {
        return new List<VoxelCommandJournalEntry>(_phase7CommandJournal);
    }

    public void CapturePhase7ReplayBaseline()
    {
        _phase7BaselineSnapshot = CloneSparseSnapshot(BuildSparseChunkDeltaSnapshot());
    }

    public void ClearPhase7CommandJournal(bool keepBaseline = true)
    {
        _phase7CommandJournal.Clear();
        if (!keepBaseline)
        {
            _phase7BaselineSnapshot = null;
        }
    }

    public VoxelReplayValidationReport ValidatePhase7DeterministicReplay(bool restoreWorldStateAfterValidation = true)
    {
        if (!EnablePhase7CommandJournal)
        {
            return BuildPhase7ValidationReportFailure("journal_disabled");
        }

        if (_phase7IsReplaying)
        {
            return BuildPhase7ValidationReportFailure("replay_in_progress");
        }

        if (_phase7BaselineSnapshot == null)
        {
            return BuildPhase7ValidationReportFailure("baseline_missing");
        }

        if (_phase7CommandJournal.Count == 0)
        {
            return BuildPhase7ValidationReportFailure("journal_empty");
        }

        VoxelChunkDeltaSnapshot preReplaySnapshot = CloneSparseSnapshot(BuildSparseChunkDeltaSnapshot());
        string expectedHash = ComputeSparseSnapshotHash(preReplaySnapshot);

        ApplySparseChunkDeltaSnapshot(CloneSparseSnapshot(_phase7BaselineSnapshot), flushRebuildQueueAfterApply: true);

        int replayedCount = 0;
        int mismatchCount = 0;

        _phase7IsReplaying = true;
        try
        {
            foreach (VoxelCommandJournalEntry entry in _phase7CommandJournal)
            {
                _ = TryApplyAuthoritativeEditCommand(entry.Command, out VoxelBlockEditResult replayResult);
                replayedCount++;

                if (!AreDeterministicResultsEquivalent(entry.Result, replayResult))
                {
                    mismatchCount++;
                }
            }
        }
        finally
        {
            _phase7IsReplaying = false;
        }

        VoxelChunkDeltaSnapshot replayedSnapshot = CloneSparseSnapshot(BuildSparseChunkDeltaSnapshot());
        string replayedHash = ComputeSparseSnapshotHash(replayedSnapshot);
        bool finalStateMatches = string.Equals(expectedHash, replayedHash, StringComparison.Ordinal);
        bool success = mismatchCount == 0 && finalStateMatches;

        if (restoreWorldStateAfterValidation)
        {
            ApplySparseChunkDeltaSnapshot(preReplaySnapshot, flushRebuildQueueAfterApply: true);
        }

        string message = success
            ? "replay_deterministic"
            : $"replay_mismatch results={mismatchCount} stateMatch={finalStateMatches}";

        var report = new VoxelReplayValidationReport(
            Success: success,
            JournalCommandCount: _phase7CommandJournal.Count,
            ReplayedCommandCount: replayedCount,
            ResultMismatchCount: mismatchCount,
            FinalStateMatches: finalStateMatches,
            ExpectedStateHash: expectedHash,
            ReplayedStateHash: replayedHash,
            Message: message);

        if (LogPhase7ReplayValidation)
        {
            GD.Print($"Phase7 replay validation: success={report.Success} journal={report.JournalCommandCount} replayed={report.ReplayedCommandCount} mismatches={report.ResultMismatchCount} stateMatch={report.FinalStateMatches}");
        }

        Phase7ReplayValidationCompleted?.Invoke(report);
        return report;
    }

    private VoxelReplayValidationReport BuildPhase7ValidationReportFailure(string reason)
    {
        var report = new VoxelReplayValidationReport(
            Success: false,
            JournalCommandCount: _phase7CommandJournal.Count,
            ReplayedCommandCount: 0,
            ResultMismatchCount: 0,
            FinalStateMatches: false,
            ExpectedStateHash: string.Empty,
            ReplayedStateHash: string.Empty,
            Message: reason);

        Phase7ReplayValidationCompleted?.Invoke(report);
        return report;
    }

    private void OnRebuildGridPhase7()
    {
        if (!ResetPhase7JournalOnGridRebuild)
        {
            return;
        }

        _phase7CommandJournal.Clear();
        _phase7BaselineSnapshot = null;
        _phase7IsReplaying = false;
    }

    private void OnClearExistingChunksPhase7()
    {
        if (!ResetPhase7JournalOnGridRebuild)
        {
            return;
        }

        _phase7CommandJournal.Clear();
        _phase7BaselineSnapshot = null;
        _phase7IsReplaying = false;
    }

    private void EnsurePhase7ReplayBaselineCaptured()
    {
        if (!EnablePhase7CommandJournal || _phase7IsReplaying)
        {
            return;
        }

        if (_phase7BaselineSnapshot != null)
        {
            return;
        }

        _phase7BaselineSnapshot = CloneSparseSnapshot(BuildSparseChunkDeltaSnapshot());
    }

    private void OnAuthoritativeEditProcessedPhase7(in VoxelBlockEditCommand command, in VoxelBlockEditResult result)
    {
        if (!EnablePhase7CommandJournal || _phase7IsReplaying)
        {
            return;
        }

        if (!IncludeRejectedCommandsInPhase7Journal && !result.Applied)
        {
            return;
        }

        int sequence = _phase7CommandJournal.Count;
        _phase7CommandJournal.Add(new VoxelCommandJournalEntry(sequence, command, result));

        int maxEntries = Math.Max(16, MaxPhase7JournalEntries);
        if (_phase7CommandJournal.Count <= maxEntries)
        {
            return;
        }

        int overflow = _phase7CommandJournal.Count - maxEntries;
        _phase7CommandJournal.RemoveRange(0, overflow);

        for (int i = 0; i < _phase7CommandJournal.Count; i++)
        {
            VoxelCommandJournalEntry existing = _phase7CommandJournal[i];
            _phase7CommandJournal[i] = existing with { Sequence = i };
        }
    }

    private static bool AreDeterministicResultsEquivalent(in VoxelBlockEditResult expected, in VoxelBlockEditResult replayed)
    {
        return expected.Applied == replayed.Applied
            && string.Equals(expected.Reason, replayed.Reason, StringComparison.Ordinal)
            && expected.ChunkCoordinate == replayed.ChunkCoordinate
            && expected.LocalVoxel == replayed.LocalVoxel
            && expected.PreviousBlockType == replayed.PreviousBlockType
            && expected.NewBlockType == replayed.NewBlockType;
    }

    private static VoxelChunkDeltaSnapshot CloneSparseSnapshot(VoxelChunkDeltaSnapshot source)
    {
        var clone = new VoxelChunkDeltaSnapshot();

        foreach ((string chunkKey, Dictionary<int, ushort> overrides) in source.ChunkOverrides)
        {
            clone.ChunkOverrides[chunkKey] = new Dictionary<int, ushort>(overrides);
        }

        return clone;
    }

    private static string ComputeSparseSnapshotHash(VoxelChunkDeltaSnapshot snapshot)
    {
        ulong hash = 1469598103934665603UL;

        var chunkKeys = new List<string>(snapshot.ChunkOverrides.Keys);
        chunkKeys.Sort(StringComparer.Ordinal);

        foreach (string key in chunkKeys)
        {
            HashString(ref hash, key);
            HashByte(ref hash, (byte)':');

            Dictionary<int, ushort> overrides = snapshot.ChunkOverrides[key];
            var localIndices = new List<int>(overrides.Keys);
            localIndices.Sort();

            foreach (int localIndex in localIndices)
            {
                HashInt(ref hash, localIndex);
                HashInt(ref hash, overrides[localIndex]);
            }

            HashByte(ref hash, (byte)';');
        }

        return hash.ToString("X16");
    }

    private static void HashString(ref ulong hash, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        foreach (byte b in bytes)
        {
            HashByte(ref hash, b);
        }
    }

    private static void HashInt(ref ulong hash, int value)
    {
        unchecked
        {
            HashByte(ref hash, (byte)(value & 0xFF));
            HashByte(ref hash, (byte)((value >> 8) & 0xFF));
            HashByte(ref hash, (byte)((value >> 16) & 0xFF));
            HashByte(ref hash, (byte)((value >> 24) & 0xFF));
        }
    }

    private static void HashByte(ref ulong hash, byte value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}
