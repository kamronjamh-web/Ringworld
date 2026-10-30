using Godot;

namespace RingworldTechDemo;

/// <summary>One row of the genus x layer matrix.</summary>
[GlobalClass]
public partial class CloudGenusRow : Resource
{
    [Export] public CloudFormationProfile Profile { get; set; } = CloudFormationProfile.Cumulus;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Low { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float Mid { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float High { get; set; }
    [Export] public CloudLowersBase LowersBase { get; set; } = CloudLowersBase.No;
    [Export] public CloudFrontType FrontType { get; set; } = CloudFrontType.None;
}
