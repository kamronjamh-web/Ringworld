using System;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.ContainmentField;

// The disturbance contract of docs/containment-field-design.md 4.1. Created by megastorm WO8 so the megastorm publisher compiles against it before the
// field exists; the field's WO2 owns the file from here (adds the bus, pool and budget behind IContainmentDisturbanceSink, nothing here changes shape).

public enum ContainmentDisturbanceKind { Lightning = 1, UpperAtmosphere = 2, StormContact = 3, MegastormContact = 4, Impact = 5, Scripted = 6, UpdraftBurst = 7,
    FieldStrike = 8 }

/// <summary>
/// One transient disturbance. Position is the SOURCE point; the bus projects it onto the field. Time is the weather clock
/// (RingVolumetricCloudLayer.CloudSimulationTimeSeconds), so paused clocks and scrubbing are deterministic.
/// </summary>
public readonly record struct ContainmentDisturbance(
    long SourceId,                       // dedupe key: lightning EventId, sprite EventId ^ salt, hash(systemId, time bucket), ...
    ContainmentDisturbanceKind Kind,
    RingCanonicalPosition Position,
    double StartTimeSeconds,
    float Energy,                        // 0..1 after coupling
    float RadiusMeters,                  // Rmax on the surface
    float DurationSeconds,               // T
    float FlashCoupling,                 // 0..1: share of the source's flash that lights the ripple (0 = refraction only)
    Func<double, float>? FlashLevel = null,          // flash 0..1 at weather time t; null = 0
    RingCanonicalPosition? OccluderCenter = null,    // source storm footprint for own-storm occlusion
    float OccluderRadiusMeters = 0, float OccluderTopMeters = 0);

public interface IContainmentDisturbanceSink { bool Publish(in ContainmentDisturbance d); }   // false = rejected (dedupe/budget/out of range)
