namespace RingworldTechDemo.Voxel.Core;

// Six block IDs currently fit in one byte. Save/replication payloads continue
// storing IDs as ushort for backward compatibility.
public enum BlockType : byte
{
    Air = 0,
    Soil = 1,
    Rock = 2,
    Bedrock = 3,
    Concrete = 4,
    Glass = 5
}
