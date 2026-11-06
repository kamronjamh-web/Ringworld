using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Pda;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Bounded local inference measurement using explicitly synthetic, feed-style snapshots.
/// Run CPU/GPU variants serially through tools/run_tests.ps1 -Tests pda_latency_test,pda_latency_gpu_test -Label pda_latency.
/// Verdict covers transport, requested backend, prompt bounds and residency; generated answer correctness requires review.
/// </summary>
public partial class PdaLatencyTest : Node
{
    private sealed record Fixture(string Name, string Question, PdaContextSnapshot Snapshot);
    private sealed record PreparedFixture(Fixture Fixture, PdaQuestionContext Verbose, PdaQuestionContext Compact);
    private sealed record Sample(string Scenario, string Variant, int Repeat, string Question, long SnapshotVersion,
        string Context, PdaQaReply Reply, double WallMilliseconds, int? WorkerProcessId);

    private readonly CancellationTokenSource _lifetime = new();
    private Task<string?>? _checks;
    private LocalPdaQaClient? _client;
    private string _marker = "PDA_LATENCY_RAM";
    public string Result { get; private set; } = "RUNNING";

    public void Begin(bool gpu)
    {
        if (_checks != null) throw new InvalidOperationException("Latency probe is already running.");
        _marker = gpu ? "PDA_LATENCY_VRAM" : "PDA_LATENCY_RAM";
        _lifetime.CancelAfter(TimeSpan.FromSeconds(225));
        _checks = RunChecksAsync(ProjectSettings.GlobalizePath("res://"), gpu, _lifetime.Token);
    }

    public override void _Process(double delta)
    {
        if (Result != "RUNNING" || _checks is not { IsCompleted: true }) return;
        try { string? error = _checks.GetAwaiter().GetResult(); Result = error == null ? "PASS" : "FAIL " + error; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        if (Result == "PASS") Console.WriteLine(_marker + "=PASS transport/backend/residency; answer quality requires review");
        else Console.Error.WriteLine(_marker + "=" + Result);
    }

    public override void _ExitTree()
    {
        _lifetime.Cancel();
        _client?.Dispose();
        _lifetime.Dispose();
    }

    private async Task<string?> RunChecksAsync(string projectRoot, bool gpu, CancellationToken token)
    {
        var failures = new List<string>();
        var samples = new List<Sample>();
        PreparedFixture[] prepared = Array.Empty<PreparedFixture>();
        PdaBackendInfo? backend = null;
        PdaQaReply? primingReply = null;
        int? residentPid = null;
        double warmupMs = 0d, primingWallMs = 0d;
        void Check(bool condition, string reason) { if (!condition) failures.Add(reason); }
        try
        {
            prepared = CreateFixtures().Select(f => new PreparedFixture(f,
                PdaContextComposer.Compose(f.Snapshot, f.Question, useCompactText: false),
                PdaContextComposer.Compose(f.Snapshot, f.Question, useCompactText: true))).ToArray();
            foreach (PreparedFixture f in prepared)
            {
                Check(f.Verbose.ContextText.Length >= 4000 && f.Verbose.ContextText.Length <= 6000,
                    $"{f.Fixture.Name} verbose fixture must contain 4000-6000 characters (actual {f.Verbose.ContextText.Length})");
                Check(f.Compact.ContextText.Length < f.Verbose.ContextText.Length, f.Fixture.Name + " compact context did not shrink");
                Check(!f.Verbose.DebugText.Contains("OMITTED", StringComparison.Ordinal)
                    && !f.Compact.DebugText.Contains("OMITTED", StringComparison.Ordinal), f.Fixture.Name + " fixture budget omitted entries");
                Check(f.Verbose.SnapshotVersion == f.Compact.SnapshotVersion, f.Fixture.Name + " variants differ in source snapshot");
            }
            if (failures.Count > 0) return string.Join(" | ", failures);

            _client = new LocalPdaQaClient(projectRoot, "qwen3-0.6b", 4, gpu ? PdaMemoryMode.GpuVram : PdaMemoryMode.SystemRam);
            var timer = Stopwatch.StartNew();
            await _client.WarmAsync(token).ConfigureAwait(false);
            warmupMs = timer.Elapsed.TotalMilliseconds;
            residentPid = _client.WorkerProcessId;
            backend = _client.BackendInfo;
            Check(residentPid.HasValue, "no resident owned helper after warmup");
            Check(backend != null, "worker did not report its actual backend");
            if (backend != null)
            {
                Check(backend.MemoryMode == (gpu ? "vram" : "ram"), "actual memory mode differs from the requested mode");
                Check(backend.Backend == (gpu ? "cuda" : "cpu"), "actual compute backend differs from the requested backend");
                Check(gpu ? backend.OffloadedLayers > 0 && backend.TotalLayers >= backend.OffloadedLayers : backend.OffloadedLayers == 0,
                    "requested backend has incorrect GPU layer offload");
            }
            if (failures.Count > 0) return string.Join(" | ", failures);

            timer.Restart();
            primingReply = await _client.AskAsync(prepared[0].Fixture.Question, prepared[0].Compact.ContextText, token).ConfigureAwait(false);
            primingWallMs = timer.Elapsed.TotalMilliseconds;
            CheckReply(primingReply, "priming", failures);
            Check(_client.WorkerProcessId == residentPid, "priming changed the resident worker PID");

            // Alternating order avoids assigning every first-of-pair cost to the same context variant.
            for (int repeat = 0; repeat < 2; repeat++)
                foreach (PreparedFixture fixture in prepared)
                    foreach (string variant in repeat == 0 ? new[] { "verbose", "compact" } : new[] { "compact", "verbose" })
                    {
                        token.ThrowIfCancellationRequested();
                        string context = variant == "verbose" ? fixture.Verbose.ContextText : fixture.Compact.ContextText;
                        timer.Restart();
                        PdaQaReply reply = await _client.AskAsync(fixture.Fixture.Question, context, token).ConfigureAwait(false);
                        double wallMs = timer.Elapsed.TotalMilliseconds;
                        int? pid = _client.WorkerProcessId;
                        samples.Add(new Sample(fixture.Fixture.Name, variant, repeat, fixture.Fixture.Question,
                            fixture.Fixture.Snapshot.Version, context, reply, wallMs, pid));
                        CheckReply(reply, fixture.Fixture.Name + "/" + variant, failures);
                        Check(pid == residentPid, "a measured request replaced the resident worker");
                    }
        }
        catch (Exception error) { failures.Add(error.GetType().Name + ": " + error.Message); }
        finally
        {
            _client?.Dispose();
            var summary = samples.GroupBy(s => s.Variant).Select(group => new
            {
                variant = group.Key,
                count = group.Count(),
                medianPromptTokens = Median(group.Select(s => (double)s.Reply.PromptTokens)),
                medianContextCharacters = Median(group.Select(s => (double)s.Context.Length)),
                medianFirstTextMs = Median(group.Select(s => s.Reply.FirstTokenMilliseconds)),
                medianPrefillMs = MedianAvailableNativeTime(group.Select(s => s.Reply.PrefillMilliseconds)),
                medianDecodeMs = MedianAvailableNativeTime(group.Select(s => s.Reply.DecodeMilliseconds)),
                medianInferenceMs = Median(group.Select(s => s.Reply.ElapsedMilliseconds)),
                medianWallMs = Median(group.Select(s => s.WallMilliseconds)),
                medianQueueMs = Median(group.Select(s => s.Reply.QueueMilliseconds)),
                medianModelReadyMs = Median(group.Select(s => s.Reply.ModelReadyMilliseconds))
            }).ToArray();
            string reportPath = Path.Combine(projectRoot, ".godot", "pda_latency", gpu ? "latency_vram.json" : "latency_ram.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                verdict = failures.Count == 0 ? "PASS" : "FAIL",
                scope = "Transport/backend/bounded-prompt/residency measurement only",
                fixtureNotice = "Synthetic representative prepared snapshots, not captures of a running world. Both context variants use the identical snapshot.",
                semanticQuality = "REVIEW every answer against its supplied context. No semantic correctness or speed threshold is asserted.",
                model = "qwen3-0.6b", requestedMemoryMode = gpu ? "vram" : "ram", cpuThreads = 4,
                backend, residentPid, warmupMs, primingWallMs, primingReply,
                workerDiagnostics = _client?.DiagnosticTail,
                outputPiecesNotice = "OutputPieces counts streamed text pieces; it is not an output-token count.",
                nativeTimingsNotice = "Native prefill/decode counters are unavailable in this LLamaSharp runtime. Nonpositive native timings mean uncollected, not instantaneous; all-unavailable native medians are null. First-text, inference and wall timings are measured separately.",
                preparedFixtures = prepared.Select(f => new { name = f.Fixture.Name, f.Fixture.Question, f.Fixture.Snapshot,
                    verboseContext = f.Verbose.ContextText, compactContext = f.Compact.ContextText }),
                summary, samples, failures
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            foreach (var row in summary) Console.WriteLine(_marker + "_MEASURE " + JsonSerializer.Serialize(row));
            Console.WriteLine(_marker + "_REPORT " + reportPath);
        }
        return failures.Count == 0 ? null : string.Join(" | ", failures);
    }

    private static void CheckReply(PdaQaReply reply, string label, List<string> failures)
    {
        if (reply.Error != null) failures.Add(label + " returned error: " + reply.Error);
        if (string.IsNullOrWhiteSpace(reply.Answer)) failures.Add(label + " returned an empty answer");
        if (reply.PromptTokens <= 0 || reply.PromptTokens >= 4096) failures.Add(label + " did not report a bounded actual prompt-token count");
        if (!double.IsFinite(reply.ElapsedMilliseconds) || reply.ElapsedMilliseconds < 0d) failures.Add(label + " reported invalid elapsed time");
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0d;
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2d : sorted[sorted.Length / 2];
    }

    private static double? MedianAvailableNativeTime(IEnumerable<double> values)
    {
        double[] available = values.Where(value => double.IsFinite(value) && value > 0d).ToArray();
        return available.Length == 0 ? null : Median(available);
    }

    private static Fixture[] CreateFixtures() => new[]
    {
        new Fixture("hub-active-front-inactive", "Why is it bright, and does the weather model prove it is raining here?", BuildSnapshot(false)),
        new Fixture("enclosed-front-approaching", "Why is it dark, is the megastorm active, and do you know my oxygen level?", BuildSnapshot(true))
    };

    private static PdaContextSnapshot BuildSnapshot(bool approaching)
    {
        var facts = new List<PdaContextFact>();
        void F(string id, string topic, string text, string compact, string source = "Synthetic latency fixture")
            => facts.Add(new PdaContextFact(id, topic, text, source, compact));
        F("support.readings", "support", "The supplied readings cover local environment, movement, celestial conditions, enclosure and scripted storms. Player health, oxygen, inventory and injury attribution are unavailable. Missing readings establish uncertainty, not a zero value or safety.",
            "Local environment readings only. Health, oxygen, inventory and injury attribution unavailable; unknown is not zero or safe.");
        F("clocks.celestial", "clocks", "The celestial simulation clock is 14200 seconds. Celestial contact estimates use this clock, which is independent of weather time; these readings cannot establish the simulation's real-world playback speed.",
            "Celestial clock 14200 simulation seconds; independent of weather time.");
        F("clocks.weather", "clocks", "The weather simulation clock is 5000 seconds and is running. Storm arrival and clearing estimates use weather seconds. Its numeric value must not be subtracted from the separate celestial clock.",
            "Weather clock 5000 simulation seconds, running; storm durations use weather seconds.");
        F("location.mode", "location", "The player's movement mode is normal physics movement, and the camera is not underwater. This describes the current explorer controller state; it supplies no suit vitals, carried equipment state or movement-penalty attribution.",
            "Normal physics movement; camera not underwater. No suit-vitals or equipment readings.");
        F("celestial.state", "celestial", approaching
            ? "Local natural lighting phase is Day; the real star is above the local horizon. This is a geometric celestial classification. It does not establish visibility through clouds, terrain or the observer's current enclosure."
            : "Local natural lighting phase is Totality; the real star's direction is above the local horizon. Natural star visibility is obstructed by the gas giant at this observer, independently of artificial illumination.",
            approaching ? "Natural phase Day; real star above horizon. Geometry does not establish unobstructed visibility." : "Natural phase Totality; real star above horizon but occulted locally by giant.");
        F("celestial.eclipse", "celestial", approaching
            ? "There is no local gas-giant eclipse: coverage of the real star's disc is 0 percent. This observer-local celestial reading does not describe cloud cover, terrain shadows or whether every longitude of the ring is lit."
            : "The local gas-giant eclipse is Total and covers 100 percent of the real star's disc. This is the player's local observation; another longitude can enter or leave totality at a different celestial time.",
            approaching ? "Local giant eclipse None; real star covered 0 percent; other ring locations may differ." : "Local giant eclipse Total; real star covered 100 percent; other ring locations may differ.");
        F("eclipse.state", "celestial", approaching
            ? "The scripted eclipse event phase is Idle and its event binding is ready. The configured event timeline is not currently supplying its artificial-sun response. A natural eclipse and a scripted event are separate readings."
            : "The scripted eclipse event phase is Sustain and event binding is ready. This is an active event-timeline phase. The event phase is reported separately from observer-local natural eclipse coverage and direct light visibility.",
            approaching ? "Scripted eclipse event Idle; binding ready; natural eclipse is separate." : "Scripted eclipse event Sustain; binding ready; natural eclipse is separate.");
        F("eclipse.hub", "celestial", approaching
            ? "Artificial hub-light output is inactive and there is no debug light-level override. No hub illumination is currently reported. General descriptions of the ring's artificial light system do not establish activation in this snapshot."
            : "Artificial hub-light output is active, driven by the configured eclipse event timeline; no debug light-level override is active. The model reports illumination output, not guaranteed visibility through enclosure or cloud cover.",
            approaching ? "Artificial hub light inactive; no debug override; no hub illumination reported." : "Artificial hub light active; cause eclipse event timeline; no debug override.");
        F("weather.state", "weather", "At the player's position the local weather model reports RainBand with system influence 0.8. Cloud presentation is enabled. This local model state does not by itself establish precipitation striking the player or whole-ring rainfall.",
            "Local weather model RainBand, influence 0.8; cloud presentation enabled. Not proof of player rainfall.");
        F("weather.air", "weather", "Local weather-model wind speed is 12 m/s, with around-ring component 12 m/s and across-ring component 0 m/s. These vector components are reported in ring-local axes, not compass bearings or a measured movement penalty.",
            "Local modeled wind 12 m/s; around 12 m/s, across 0 m/s; ring-local axes.");
        F("weather.clouds", "weather", "Local modeled cloud coverage is 75 percent, with cloud base altitude 1800 m and cloud top altitude 3200 m. These are model-layer values at the observer and do not establish the state of distant ring sectors or surface wetness.",
            "Local modeled cloud coverage 75 percent; base 1800 m, top 3200 m; no distant-sector claim.");
        F("weather.precipitation", "weather", "Local precipitation potential is 0.7 on the weather model's scale. Potential describes model suitability for precipitation; it is not a rain gauge, a rainfall intensity observation, or proof that rain or hail is hitting the player.",
            "Local precipitation potential 0.7; forecast/model quantity, not observed rainfall or hail.");
        F("weather.effects.state", "weather", "Local precipitation-effect readings are unavailable or not initialized. Rain and snow effect drive is unknown. Zero-valued defaults and precipitation potential cannot establish dry conditions, player wetness, or precipitation damage.",
            "Local precipitation-effect drive unavailable/uninitialized; rain/snow unknown. Defaults do not establish dry conditions.");
        F("underground.state", "underground", approaching
            ? "Local enclosure mode is Enclosed and underground lighting darkening is applied. Mode override is Automatic. This controller reports an enclosure-driven contribution to lighting; it does not measure the entire rendered scene's luminance."
            : "Local enclosure mode is Surface and underground lighting darkening is not applied. Mode override is Automatic. This identifies the local controller's state and does not establish the visibility of every astronomical or artificial light source.",
            approaching ? "Local enclosure Enclosed; underground lighting darkening applied; override Automatic." : "Local enclosure Surface; underground lighting darkening not applied; override Automatic.");
        F("underground.enclosure", "underground", approaching
            ? "The local enclosure reading reports depth 30 m, openness 0.05, enclosure blend 1 and cave fog weight 0.8. These values describe the observer's enclosure and presentation coupling, not oxygen reserves, breathable air or cave safety."
            : "The local enclosure reading reports depth 0 m, openness 1, enclosure blend 0 and cave fog weight 0. These values describe local enclosure and fog coupling; they are not health, air composition or environmental safety readings.",
            approaching ? "Local depth 30 m; openness 0.05; enclosure blend 1; cave fog weight 0.8; oxygen unknown." : "Local depth 0 m; openness 1; enclosure blend 0; cave fog weight 0; oxygen unknown.");
        F("megastorm.state", "megastorm", approaching
            ? "A scripted megastorm is triggered; its sample at the player is active and the local phase is Approach. This state is separate from the ordinary RainBand weather model. An active hazard model is not evidence that the player has been injured."
            : "No scripted megastorm is currently triggered. Its local hazard, structural resonance and front-arrival readings do not apply in this snapshot. Ordinary weather-model RainBand conditions can still exist independently of this inactive event.",
            approaching ? "Scripted megastorm triggered; local sample active, phase Approach; separate from ordinary weather." : "Scripted megastorm not triggered; local hazards and arrival not applicable; ordinary weather may exist.");
        if (approaching)
        {
            F("megastorm.arrival", "megastorm", "Local modeled front-edge distance is 12000 m, arrival in 90 weather seconds, clearing in 450 weather seconds. Durations use weather simulation time; real-time playback speed is unavailable.",
                "Local front edge 12000 m; arrival in 90 weather seconds; clearing in 450 weather seconds.");
            F("megastorm.darkness", "megastorm", "Local megastorm extra-darkness weight is 0.4 and darkness multiplier is 0.6 (1 is neutral). These describe this storm's modeled contribution, not combined rendered-scene luminance.",
                "Local megastorm extra-darkness weight 0.4; darkness multiplier 0.6 (1 neutral); modeled contribution.");
            F("megastorm.shelter", "megastorm", "Observer shelter class is Enclosed and wind-aligned rain exposure is 0.1. This local exposure estimate does not establish complete safety or supply a health or injury reading.",
                "Observer shelter Enclosed; wind-aligned rain exposure 0.1; not proof of complete safety or injury.");
        }
        var knowledge = new[]
        {
            new PdaKnowledgeEntry("lore.eclipse", "celestial", "The gas giant can occult the real star. The ring's emitter network powers a hub light during configured eclipse-event phases. The hub can also be controlled by a debug override. Activation must be established from current readings.",
                "Giant occultation blocks the real star. Configured eclipse event powers hub light; debug override is separate. Readings establish activation."),
            new PdaKnowledgeEntry("lore.weather", "weather", "Ordinary local weather and the scripted megastorm are independent inputs. Weather-model precipitation potential describes a model quantity. A separate initialized precipitation-effect reading is needed to describe current local effect drive.",
                "Ordinary weather and megastorm are separate. Precipitation potential is not observed rainfall; initialized effect drive is separate."),
            new PdaKnowledgeEntry("lore.underground", "underground", "The enclosure controller can darken underground lighting and couple cave fog to the local enclosure state. This explains a modeled lighting contribution. Enclosure and fog readings do not establish oxygen, health or environmental safety.",
                "Enclosure can apply underground darkening and cave fog. These readings do not establish oxygen, health or safety."),
            new PdaKnowledgeEntry("lore.megastorm", "megastorm", "A triggered megastorm has observer-local phases, arrival estimates, darkness and hazard outputs. Durations are weather simulation seconds. Hazard output describes modeled conditions and is distinct from measured injury or player damage attribution.",
                "Triggered megastorm reports local phases, weather-second estimates and modeled hazards. Hazard output does not establish injury.")
        };
        return new PdaContextSnapshot(approaching ? 12 : 11, DateTimeOffset.Parse("2026-11-05T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            facts.AsReadOnly(), Array.AsReadOnly(knowledge), Array.Empty<PdaRecentEvent>());
    }
}
