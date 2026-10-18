using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Last value written per material for one uniform. Godot queues a material update for every
/// set_shader_parameter call, so unchanged values are skipped. Entries are keyed by material
/// object, so recreated materials always receive their first write.
/// </summary>
internal sealed class EmitterUniformGate<T> where T : IEquatable<T>
{
    private readonly ConditionalWeakTable<GodotObject, StrongBox<T>> _values = new();
    private readonly RingMaterialWriteCounters.Site _site;

    public EmitterUniformGate(RingMaterialWriteCounters.Site site) => _site = site;

    public void Set(ShaderMaterial? material, StringName name, T value)
    {
        if (material is null) return;
        if (_values.TryGetValue(material, out var stored))
        {
            if (EqualityComparer<T>.Default.Equals(stored.Value!, value)) return;
            stored.Value = value;
        }
        else _values.Add(material, new StrongBox<T>(value));
        material.SetShaderParameter(name, Variant.From(value));
        RingMaterialWriteCounters.Record(_site);
    }
}

/// <summary>
/// Per-material change gate for a Vector4 array uniform. Elements are compared one by one and the
/// packed Variant is converted at most once per batch of <see cref="Set"/> calls; call <see cref="End"/> afterwards.
/// </summary>
internal sealed class EmitterVector4ArrayGate
{
    private readonly ConditionalWeakTable<GodotObject, Vector4[]> _values = new();
    private readonly RingMaterialWriteCounters.Site _site;
    private Variant _packed;
    private bool _hasPacked;

    public EmitterVector4ArrayGate(RingMaterialWriteCounters.Site site) => _site = site;

    public void Set(ShaderMaterial? material, StringName name, Vector4[] value)
    {
        if (material is null) return;
        if (_values.TryGetValue(material, out Vector4[]? stored))
        {
            bool same = stored.Length == value.Length;
            for (int index = 0; same && index < value.Length; index++) same = stored[index] == value[index];
            if (same) return;
            if (stored.Length == value.Length) Array.Copy(value, stored, value.Length);
            else { _values.Remove(material); _values.Add(material, (Vector4[])value.Clone()); }
        }
        else _values.Add(material, (Vector4[])value.Clone());
        if (!_hasPacked) { _packed = Variant.From(value); _hasPacked = true; }
        material.SetShaderParameter(name, _packed);
        RingMaterialWriteCounters.Record(_site);
    }

    /// <summary>Releases the Variant converted during the batch.</summary>
    public void End()
    {
        if (!_hasPacked) return;
        _packed.Dispose();
        _packed = default;
        _hasPacked = false;
    }
}
