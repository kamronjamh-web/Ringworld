using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Pure-C# pin of the GPU vegetation parameter words (no RenderingDevice). Words 0..63 of tree and blade frames
/// must keep the layout captured before ParameterBytes grew to 512; words 64..127 are card-only. Run via
/// tools/gpu_vegetation_parameter_layout_test.gd.</summary>
public partial class GpuVegetationParameterLayoutTest : RefCounted
{
    private const int Words = 128;
    private readonly List<string> _failures = new();

    // FNV-1a over the IEEE bits of words 0..63, captured on the 256-byte layout before the card words were added.
    private static readonly Dictionary<string, string> Golden = new()
    {
        ["tree/plain"] = "0x654890BCAFBB017A",
        ["tree/custom"] = "0x6FE1BC923D5190CD",
        ["tree/dither"] = "0xD8D6A1E8B74443C0",
        ["tree/protected-canonical"] = "0x23A33E4C0CE9D6FF",
        ["tree/distance-protected-fades"] = "0x1191DE4816E75F71",
        ["blade/planes-lod"] = "0xFF013648F2EA85F1",
        ["blade/planes-nolod"] = "0x78A0B28BAB71069A",
        ["blade/noplanes"] = "0x0A9D8AECA523A626",
    };

    public bool Run()
    {
        bool capture = OS.GetEnvironment("PARAM_LAYOUT_CAPTURE") == "1";
        var request = new RingGpuVegetationCompute.FrameRequest(new Vector3(10.5f, -3.25f, 77f), new uint[] { 100, 200, 300, 400, 500 },
            Array.Empty<RingGpuVegetationCompute.BatchFrameParameters>());
        var xform = new Transform3D(new Basis(new Vector3(0.3f, 0.5f, 0.1f).Normalized(), 0.7f).Scaled(new Vector3(1f, 1.1f, 0.9f)), new Vector3(100f, 20f, -50f));
        Vector4[] planes = { new(1, 0, 0, 5), new(-1, 0, 0, 6), new(0, 1, 0, 7), new(0, -1, 0, 8), new(0, 0, 1, 9), new(0, 0, -1, 10) };
        var cases = new List<(string Name, RingGpuVegetationCompute.BatchFrameParameters P, bool Custom, bool Dither, float Radius)>
        {
            ("tree/plain", new(1, xform, false, 1, 20f, 80f, 8f, .9f, .4f, true, .75f, false, null, 2f), false, false, 0f),
            ("tree/custom", new(2, xform, false, 0, 0f, 60f, 5f, 1f, .5f, true, .6f, false, null, 0f), true, false, 0f),
            ("tree/dither", new(3, xform, false, 2, 30f, 90f, 12f, .8f, .3f, true, .5f, false, null, 1f), false, true, 0f),
            ("tree/protected-canonical", new(4, xform, false, 0, 0f, 50f, 4f, 1f, 1f, true, 1f, true, null, 3f, new Vector2(12.5f, -7.5f), 42), true, true, 0f),
            ("tree/distance-protected-fades", new(5, xform, false, 3, 10f, 70f, 6f, .7f, .2f, true, .9f, true, null, 0f, null, 7, true, 5f, 15f, 60f, 70f), false, false, 0f),
            ("blade/planes-lod", new(6, xform, true, 0, 5f, 100f, 10f, 1f, .2f, true, 1f, false, planes, 4f, null, 9), false, false, 0.35f),
            ("blade/planes-nolod", new(7, xform, true, 1, 0f, 50f, 2f, .5f, .5f, false, 1f, false, planes, 4f), false, false, 0.35f),
            ("blade/noplanes", new(8, xform, true, 0, 0f, 25f, 1f, 1f, 1f, false, 1f, false, null, 0f), false, false, 0.12f),
        };
        foreach (var c in cases)
        {
            var data = new float[Words];
            RingGpuVegetationCompute.WriteParameterWords(request, c.P, c.Custom, c.Dither, c.Radius, data);
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < 64; i++) hash = (hash ^ BitConverter.SingleToUInt32Bits(data[i])) * 1099511628211UL;
            string got = "0x" + hash.ToString("X16");
            if (capture) GD.Print($"PARAM_LAYOUT_CAPTURE [\"{c.Name}\"] = \"{got}\",");
            else if (!Golden.TryGetValue(c.Name, out string? want) || want != got) _failures.Add($"{c.Name}: words 0..63 hash {got} != golden {(want ?? "<none>")}");
            for (int i = 64; i < Words; i++)
                if (data[i] != 0f) { _failures.Add($"{c.Name}: word {i} = {data[i]}, expected 0 for a non-card frame"); break; }
            if (data[62] != 0f || data[63] != 0f) _failures.Add($"{c.Name}: words 62/63 must be 0 for a non-card frame");
        }

        // Card grass: blade words 0..61 unchanged apart from the flag, card words at 62/64..77.
        var card = new RingGrassCardComputeParameters(400f, 14f, 2.5f, .15f, 1.3f, 20f, 8f, 45f, 8f, 22f, .08f, 2f, 2f, 5.15f);
        var cardFrame = new RingGpuVegetationCompute.BatchFrameParameters(9, xform, true, 0, 0f, 400f, 1f, 1f, 1f, false, 1f, false, planes, 4f, GrassCard: card);
        var plainFrame = cardFrame with { GrassCard = null };
        var cardData = new float[Words]; var plainData = new float[Words];
        RingGpuVegetationCompute.WriteParameterWords(request, cardFrame, false, false, .25f, cardData);
        RingGpuVegetationCompute.WriteParameterWords(request, plainFrame, false, false, .25f, plainData);
        for (int i = 0; i < 62; i++) if (cardData[i] != plainData[i]) { _failures.Add($"card frame changed word {i}"); break; }
        float[] expected = { 400f, 14f, 2.5f, .15f, 1.3f, 20f, 8f, 45f, 8f, 22f, .08f, 2f, 2f, 5.15f };
        if (cardData[62] != 1f || cardData[63] != 0f) _failures.Add($"card flag/reserved = {cardData[62]}/{cardData[63]}");
        for (int i = 0; i < expected.Length; i++) if (cardData[64 + i] != expected[i]) _failures.Add($"card word {64 + i} = {cardData[64 + i]}, expected {expected[i]}");
        for (int i = 78; i < Words; i++) if (cardData[i] != 0f) { _failures.Add($"card word {i} must be 0"); break; }
        for (int i = 62; i < Words; i++) if (plainData[i] != 0f) { _failures.Add($"null GrassCard left word {i} set"); break; }
        // The skip-unchanged comparison must see GrassCard: records are equal only when the card block is.
        if (card == (card with { DistanceMarginMeters = 3f })) _failures.Add("record equality ignores the margin");
        if (capture) return true;
        foreach (string f in _failures) GD.PushError("PARAM_LAYOUT " + f);
        GD.Print(_failures.Count == 0 ? "PARAM_LAYOUT=PASS" : "PARAM_LAYOUT=FAIL");
        return _failures.Count == 0;
    }
}
