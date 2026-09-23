using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>Immutable smooth-only journal envelope; it never references legacy save formats.</summary>
public readonly record struct SmoothTerrainJournalEnvelope(int FormatVersion,ulong TerrainDefinitionHash,double CircumferenceMeters,ulong Revision,ulong NextCommandId,long NextSequence,SmoothTerrainEditCommand[] Commands);
public readonly record struct SmoothTerrainJournalLoadResult(bool Success,SmoothTerrainJournalEnvelope Envelope,string Error);

public static class SmoothTerrainEditJournalStore
{
    public const int FormatVersion=1;
    public const string DefaultPath="user://smooth_terrain_edits_v1.json";
    public static string PreparePathOnMainThread(string userPath)
    { if(string.IsNullOrWhiteSpace(userPath))throw new ArgumentException("Journal path is empty.",nameof(userPath));string path=ProjectSettings.GlobalizePath(userPath);if(string.IsNullOrWhiteSpace(path))throw new InvalidOperationException("Journal path cannot be globalized.");return path; }
    public static Task<SmoothTerrainJournalLoadResult> SaveSnapshotAsync(string fullAbsolutePath,SmoothTerrainJournalEnvelope envelope,CancellationToken cancellationToken=default)=>Task.Run(()=>Save(fullAbsolutePath,envelope,cancellationToken),cancellationToken);
    private static SmoothTerrainJournalLoadResult Save(string path,SmoothTerrainJournalEnvelope envelope,CancellationToken token)
    { string? tmp=null;try{ValidateEnvelope(envelope,envelope.TerrainDefinitionHash,envelope.CircumferenceMeters);token.ThrowIfCancellationRequested();string dir=Path.GetDirectoryName(path)??throw new InvalidOperationException("Journal directory missing.");Directory.CreateDirectory(dir);tmp=Path.Combine(dir,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllText(tmp,JsonSerializer.Serialize(envelope));token.ThrowIfCancellationRequested();File.Move(tmp,path,true);tmp=null;return new(true,envelope,"");}catch(Exception e){return new(false,default,e.Message);}finally{if(tmp!=null){try{if(File.Exists(tmp))File.Delete(tmp);}catch{}}} }
    public static SmoothTerrainJournalLoadResult LoadSnapshot(string fullAbsolutePath,ulong expectedHash,double expectedCircumference,params ulong[] legacyHashes)
    { try{var envelope=JsonSerializer.Deserialize<SmoothTerrainJournalEnvelope>(File.ReadAllText(fullAbsolutePath));ValidateEnvelope(envelope,expectedHash,expectedCircumference,legacyHashes);return new(true,envelope,"");}catch(Exception e){return new(false,default,e.Message);} }
    public static void ValidateEnvelope(SmoothTerrainJournalEnvelope envelope,ulong expectedHash,double expectedCircumference,params ulong[] legacyHashes)
    { bool compatibleHash=envelope.TerrainDefinitionHash==expectedHash||Array.Exists(legacyHashes,hash=>hash!=0&&envelope.TerrainDefinitionHash==hash);if(envelope.FormatVersion!=FormatVersion||!double.IsFinite(envelope.CircumferenceMeters)||envelope.CircumferenceMeters<=0||!compatibleHash||Math.Abs(envelope.CircumferenceMeters-expectedCircumference)>.01||envelope.NextCommandId==0||envelope.NextSequence<=0||envelope.Commands==null)throw new InvalidDataException("Smooth terrain journal envelope is incompatible.");_ = new SmoothTerrainEditSnapshot(envelope.Commands,envelope.Revision,checked((long)Math.Round(envelope.CircumferenceMeters)));foreach(var c in envelope.Commands)if(c.CommandId>=envelope.NextCommandId||c.Sequence>=envelope.NextSequence)throw new InvalidDataException("Journal identity envelope is invalid."); }
}
