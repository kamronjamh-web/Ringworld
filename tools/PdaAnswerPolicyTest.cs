using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Pda;

namespace RingworldTechDemo.Tools;

/// <summary>Narrow read-only policy and typed-state answers. Run tools/run_tests.ps1 -Tests pda_answer_policy_test -Label pda_debug.</summary>
public partial class PdaAnswerPolicyTest : RefCounted
{
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
