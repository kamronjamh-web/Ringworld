using System;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.ContainmentField;

// The disturbance contract of docs/containment-field-design.md 4.1. Created by megastorm WO8 so the megastorm publisher compiles against it before the
// field exists; the field's WO2 owns the file from here (ContainmentDisturbanceBus is the real IContainmentDisturbanceSink behind it).

// BodyContact (2026-11-04 decision 4): the player or a fast body passing through the sheet. The bus reserves its 2 pool slots and its per-source cooldown now;
// the adapter that emits it comes with the contact-ripple work order.
public enum ContainmentDisturbanceKind { Lightning = 1, UpperAtmosphere = 2, StormContact = 3, MegastormContact = 4, Impact = 5, Scripted = 6, UpdraftBurst = 7,
    FieldStrike = 8, BodyContact = 9,
    EmitterShot = 10 }   // an emitter orb dispersing at the field: lightning-like, its own pool reservation and its own 1/s spawn window

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
    float OccluderRadiusMeters = 0, float OccluderTopMeters = 0,
    bool NaturalSource = false);         // FieldStrike only: made by the natural lightning scheduler (the bus caps those at 1/s inside the 2/s FieldStrike cap)

public interface IContainmentDisturbanceSink { bool Publish(in ContainmentDisturbance d); }   // false = rejected (dedupe/budget/out of range)
