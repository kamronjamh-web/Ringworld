using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.Pda;

namespace RingworldTechDemo.Tools;

/// <summary>Narrow read-only policy and typed-state answers. Run tools/run_tests.ps1 -Tests pda_answer_policy_test -Label pda_debug.</summary>
public partial class PdaAnswerPolicyTest : RefCounted
{
    private static void LookupAnswers(PdaContextSnapshot baseSnapshot, Action<bool, string> check)
    {
        var rich = baseSnapshot with
        {
            StateEvidence = new PdaStateEvidence(HubActive: false, HubRawActive: false, HubProviderBound: false, LocalEclipseCoverageFraction: 0, PrecipitationInitialized: true,
                PrecipitationDrive: 0, PrecipitationMode: PdaPrecipitationMode.Negligible, PrecipitationSuspended: false, PrecipitationPotential: 0,
                Wind: new PdaWindEvidence(18.49, 15.32, -10.35), Cloud: new PdaCloudEvidence(500, 3997.6, 354, 32.2),
                Time: new PdaTimeEvidence("Afternoon", true, 3000, 750), Enclosure: new PdaEnclosureEvidence("Surface", -353.94, 1, false),
                Storm: new PdaStormEvidence(MegastormTriggered: false, WeatherKind: "MajorFront", WeatherInfluence: .45, NaturalArrivalPhase: "Clear"))
        };
        bool Ask(PdaContextSnapshot s, string question, out string answer, out string reason) => PdaAnswerPolicy.TryAnswer(question, s, out answer, out reason);
        check(Ask(rich, "How strong is the wind here and which way is it blowing?", out string wind, out _) && wind.Contains("18.49 m/s") && wind.Contains("toward spinward")
            && wind.Contains("rim wall") && !Regex.IsMatch(wind, @"\b(?:north|south|east|west)\b", RegexOptions.IgnoreCase), "wind lookup lost speed or invented compass directions: " + wind);
        check(Ask(rich, "How high is the cloud base above me?", out string cloud, out _) && cloud.Contains("500 m") && cloud.Contains("354 m above your position")
            && !cloud.Contains("462"), "cloud-base lookup did not give the single base with units: " + cloud);
        check(Ask(rich, "What time of day is it here, and when will it get dark?", out string time, out _) && time.Contains("afternoon") && time.Contains("12.5 game minutes")
            && !time.Contains("simulation seconds"), "time lookup lacked phase and in-game minutes to sunset: " + time);
        var night = rich with { StateEvidence = rich.StateEvidence! with { Time = new PdaTimeEvidence("AstronomicalTwilight", false, 1800, 4000) } };
        check(Ask(night, "When is sunrise?", out string sunrise, out _) && sunrise.Contains("astronomical twilight") && sunrise.Contains("30 game minutes"), "night lookup lacked time to sunrise: " + sunrise);
        check(Ask(rich, "Am I underground or on the surface, and how can you tell?", out string surface, out _) && surface.Contains("on the surface, not underground")
            && surface.Contains("353.94 m above the terrain surface") && !surface.Contains("-353"), "surface lookup misread negative depth: " + surface);
        var cave = rich with { StateEvidence = rich.StateEvidence! with { Enclosure = new PdaEnclosureEvidence("Underground", 60, 0, true) } };
        check(Ask(cave, "Am I underground?", out string under, out _) && under.Contains("You are underground") && under.Contains("60 m below"), "underground lookup failed: " + under);
        check(Ask(rich, "Is a storm heading toward me?", out string storm, out string stormReason) && storm.Contains("No scripted megastorm is triggered")
            && storm.Contains("No natural storm front is arriving") && stormReason.Contains("storm"), "storm lookup lacked megastorm/natural status: " + storm);
        var active = rich with { StateEvidence = rich.StateEvidence! with { Storm = new PdaStormEvidence(true, true, "Wall", 120, 600, 3600, "Megastorm", 1, "Approach", 90) } };
        check(Ask(active, "Is a megastorm coming?", out string coming, out _) && coming.Contains("reaches you in 2 game minutes") && coming.Contains("3600 m away") && coming.Contains("clears in 10 game minutes"), "active megastorm lookup lacked arrival/clearing: " + coming);
        check(Ask(rich, "Is the hub light on and is there an eclipse?", out string both, out _) && both.Contains("inactive") && both.Contains("0 percent"), "hub/eclipse pair failed: " + both);
        var unbound = rich with { StateEvidence = rich.StateEvidence! with { HubActive = null } };
        check(Ask(unbound, "Is the artificial sun on?", out string provisional, out _) && provisional.Contains("provisional"), "unbound hub reading was presented as certain: " + provisional);
        check(Ask(rich, "Is it safe to stay outside right now?", out string safety, out _) && safety.Contains("can't call it safe or unsafe") && safety.Contains("no health, oxygen")
            && !Regex.IsMatch(safety, @"\b(?:it is|it's|you are|is) safe\b", RegexOptions.IgnoreCase), "safety answer overclaimed: " + safety);
        var none = baseSnapshot with { StateEvidence = new PdaStateEvidence() };
        check(Ask(none, "Is it safe to stay outside right now?", out string unknownSafety, out _) && unknownSafety.Contains("Unknown because the readings are missing")
            && unknownSafety.Contains("megastorm status") && unknownSafety.Contains("enclosure"), "safety with missing readings was not reported as unknown: " + unknownSafety);
        check(Ask(none, "How strong is the wind?", out string noWind, out _) && noWind.Contains("don't have") && !Regex.IsMatch(noWind, @"\d"), "missing wind became a number: " + noWind);
        check(Ask(none, "What time of day is it?", out string noTime, out _) && noTime.Contains("don't have"), "missing time was invented: " + noTime);
        check(!Ask(baseSnapshot with { StateEvidence = null }, "How strong is the wind?", out _, out _), "lookup reconstructed wind without typed evidence");
        foreach (string explain in new[] { "Why is the wind blowing?", "Explain the wind.", "What does the wind mean for me?", "The precipitation potential is high. Does that mean rain is falling on me?", "Why is it bright here while the real sun is eclipsed?" })
            check(!Ask(rich, explain, out _, out _), "explain/why question was answered by lookup policy: " + explain);
        check(!Ask(rich, "Is it windy and is the hub on and what about the cloud base?", out _, out _), "mixed multi-topic question was answered by one lookup");
        check(Ask(rich, "What can you see in my current view?", out string noView, out string viewReason) && noView == PdaAnswerPolicy.NoImageReply && viewReason == "view_image_not_attached", "a view question without an image was left to invent a view");
        check(!PdaAnswerPolicy.TryAnswer("What can you see in my current view?", rich, out _, out _, imageAttached: true), "a view question with an attached image was answered without the image");
        string facts = PdaAnswerPolicy.BuildVerifiedFacts("Why is it so bright here right now?", rich);
        check(facts.StartsWith("- ") && facts.Contains("afternoon") && facts.Contains("inactive") && facts.Contains("0 percent") && facts.Contains("on the surface"), "verified facts for a brightness question lacked time/hub/eclipse/enclosure: " + facts);
        check(PdaAnswerPolicy.BuildVerifiedFacts("Why is the wind so strong?", rich).Contains("18.49 m/s"), "verified facts omitted the asked-about wind");
        check(PdaAnswerPolicy.BuildVerifiedFacts("Why?", baseSnapshot with { StateEvidence = null }).Length == 0, "verified facts invented evidence");
        check(PdaAnswerPolicy.BuildVerifiedFacts("Does precipitation potential mean rain?", rich).Contains("precipitation potential is 0"), "a real zero potential was not stated as zero");
    }

    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            var snapshot = new PdaContextSnapshot(1, DateTimeOffset.UnixEpoch, Array.Empty<PdaContextFact>(), Array.Empty<PdaKnowledgeEntry>(),
                new[] { new PdaRecentEvent("weather: old rain drive 0.7", 10, "old sensor") },
                new PdaStateEvidence(HubActive: false, LocalEclipseCoverageFraction: 0, PrecipitationInitialized: true,
                    PrecipitationDrive: 0, PrecipitationMode: PdaPrecipitationMode.Negligible, PrecipitationSuspended: false));
            Check(PdaAnswerPolicy.TryAnswer("Turn the artificial sun on for me.", snapshot, out string action, out string reason)
                && action == PdaAnswerPolicy.ReadOnlyReply && reason == "read_only_control_request", "explicit control request reached inference");
            Check(PdaAnswerPolicy.TryAnswer("Please start the activation sequence.", snapshot, out action, out _)
                && action == PdaAnswerPolicy.ReadOnlyReply, "activation sequence request reached inference");
            Check(PdaAnswerPolicy.TryAnswer("How much oxygen do I have left, and how many minutes can I survive?", snapshot, out string oxygen, out _)
                && oxygen.Contains("don't have") && !oxygen.Contains("0.7"), "unsupported oxygen/survival request fabricated telemetry");
            Check(PdaAnswerPolicy.TryAnswer("Is it still raining?", snapshot, out string rain, out reason)
                && rain.Contains("drive is 0") && !rain.Contains("0.7") && rain.Contains("does not establish") && reason == "current_precipitation_effect_reading", "current zero effects were overridden by old rain or promoted to physical dryness");
            Check(PdaAnswerPolicy.TryAnswer("Is the artificial sun on?", snapshot, out string hub, out _) && hub.Contains("inactive"), "typed inactive hub was lost");
            Check(PdaAnswerPolicy.TryAnswer("Is the real sun eclipsed?", snapshot, out string eclipse, out _) && eclipse.Contains("0 percent"), "typed zero eclipse was lost");
            var unknown = snapshot with { StateEvidence = new PdaStateEvidence(PrecipitationInitialized: false) };
            Check(PdaAnswerPolicy.TryAnswer("Is it raining?", unknown, out string unavailable, out _) && unavailable.Contains("not initialized")
                && !unavailable.Contains("drive is 0"), "unknown initialization became a zero reading");
            var missing = snapshot with { StateEvidence = null };
            Check(!PdaAnswerPolicy.TryAnswer("Is it raining?", missing, out _, out _), "missing typed evidence was reconstructed from prose/history");
            var suspended = snapshot with { StateEvidence = snapshot.StateEvidence! with { PrecipitationDrive = .7, PrecipitationMode = PdaPrecipitationMode.Rain, PrecipitationSuspended = true } };
            Check(PdaAnswerPolicy.TryAnswer("Is it raining?", suspended, out string held, out _) && held.Contains("suspended") && held.Contains("doesn't establish"), "held underground cache became current rainfall");
            var snow = snapshot with { StateEvidence = snapshot.StateEvidence! with { PrecipitationDrive = .8, PrecipitationMode = PdaPrecipitationMode.Snow } };
            Check(PdaAnswerPolicy.TryAnswer("Is it snowing?", snow, out string snowAnswer, out _) && snowAnswer.Contains("snow mode") && snowAnswer.Contains("0.8"), "typed snow reading lost source mode/value");
            LookupAnswers(snapshot, Check);
            foreach (string question in new[] { "Why is it dark?", "Why is my oxygen low?", "How do I turn the light on?", "What local conditions are present?", "Is this rain or hail, and am I getting hurt?", "What was the rain like earlier?" })
                Check(!PdaAnswerPolicy.TryAnswer(question, snapshot, out _, out _), "policy clobbered model explanation/mixed/ambiguous question: " + question);
            foreach (string generated in new[]
            {
                "The artificial hub light is inactive. I have initiated the activation sequence.",
                "I have initiated the activation sequence. The artificial hub light was inactive.",
                "The artificial hub light is not currently active. I have started the activation sequence.",
                "Sure, I've turned the artificial sun on for you.",
                "I have successfully enabled the weather system."
            })
                Check(PdaAnswerPolicy.GuardGeneratedAnswer(generated, out bool replaced) == PdaAnswerPolicy.ReadOnlyReply && replaced, "generated fake action confirmation escaped guard: " + generated);
            foreach (string generated in new[] { "I cannot initiate the activation sequence.", "I explained activation; I have no controls.", "The emitter network has activated the hub light.", "I have observed that the hub is active.", "I turned my attention to the lights." })
                Check(PdaAnswerPolicy.GuardGeneratedAnswer(generated, out bool replaced) == generated && !replaced, "legitimate observation/negative statement was replaced: " + generated);
        }
        catch (Exception e) { failures.Add(e.ToString()); }
        if (failures.Count > 0) { Console.Error.WriteLine("PDA_ANSWER_POLICY=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("PDA_ANSWER_POLICY=PASS");
        return 0;
    }
}
