using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RingworldTechDemo.Pda;

/// <summary>Narrow deterministic replies from typed evidence and explicit capability limits. This is not a general factual validator.</summary>
public static class PdaAnswerPolicy
{
    public const string ReadOnlyReply = "I can explain the current readings, but I can't change the game or control its systems. No action has been performed.";

    public static bool TryAnswer(string question, PdaContextSnapshot snapshot, out string answer, out string reason)
    {
        answer = ""; reason = "";
        string q = Normalize(question);
        if (IsExplicitControlRequest(q)) { answer = ReadOnlyReply; reason = "read_only_control_request"; return true; }
        // Explanations, hypothetical instructions, recall and mixed questions need their full context.
        if (Has(q, @"\b(?:why|because|cause|reason|explain|was|were|earlier|before|previously|history|remember)\b|how (?:do|can|would) i")) return false;
        if (Has(q, @"\b(?:and|also)\b") && Has(q, @"\b(?:rain|raining|snow|snowing|hail|hailing|weather|storm|light|sun|hub|eclipse|cave|underground)\b")) return false;
        bool personal = Has(q, @"\b(?:my|me|i|left|remaining)\b");
        if (personal && Has(q, @"\boxygen\b|\b(?:survive|survival)\b"))
        { answer = "I don't have an oxygen or survival-time reading, so I can't determine your remaining oxygen or how long you can survive."; reason = "oxygen_telemetry_unavailable"; return true; }
        if (personal && Has(q, @"\b(?:health|hp|injury|injured|hurt|dying)\b"))
        { answer = "Player health and injury readings aren't available to this PDA feed. I can't determine your condition from environmental hazards alone."; reason = "health_telemetry_unavailable"; return true; }
        if (personal && Has(q, @"\binventory\b"))
        { answer = "I don't have access to your inventory contents in this PDA feed."; reason = "inventory_telemetry_unavailable"; return true; }
        if (Has(q, @"\b(?:and|also)\b")) return false;
        PdaStateEvidence? evidence = snapshot.StateEvidence;
        if (evidence == null) return false;
        if (IsCurrentPrecipitationQuestion(q) && evidence.PrecipitationInitialized.HasValue)
        {
            if (evidence.PrecipitationInitialized == false)
            { answer = "Local precipitation-effect readings are unavailable or not initialized. Their default values don't establish whether it is raining or dry."; reason = "precipitation_uninitialized"; return true; }
            if (evidence.PrecipitationSuspended == true)
            { answer = "Local precipitation presentation is suspended underground. Its cached drive doesn't establish current rainfall or conditions outside."; reason = "precipitation_presentation_suspended"; return true; }
            if (evidence.PrecipitationSuspended != false || evidence.PrecipitationDrive is not { } drive || !double.IsFinite(drive)
                || drive < 0d || evidence.PrecipitationMode == null) return false;
            string n = drive.ToString("0.###", CultureInfo.InvariantCulture);
            if (drive <= .01d || evidence.PrecipitationMode == PdaPrecipitationMode.Negligible)
                answer = $"The current initialized local precipitation-effect drive is {n}, negligible or zero. That does not establish whether you are physically dry or wet.";
            else
                answer = $"The initialized local effects report {(evidence.PrecipitationMode == PdaPrecipitationMode.Snow ? "snow" : "rain")} mode with drive {n}. This is an effect reading, not a measurement of rain striking you, wetness or injury.";
            reason = "current_precipitation_effect_reading"; return true;
        }
        if (Has(q, @"^(?:is|are) (?:the )?(?:artificial (?:sun|(?:hub )?light)|hub(?: light)?)(?: currently| still)? (?:on|off|active|inactive|running)(?: (?:now|here|right now))?\??$")
            && evidence.HubActive is { } hub)
        { answer = $"The artificial hub-light output is currently {(hub ? "active" : "inactive")}."; reason = "current_hub_reading"; return true; }
        if (Has(q, @"^(?:is (?:there (?:an? )?eclipse|(?:the )?(?:real )?sun(?: currently)? eclipsed|(?:the )?ring(?: currently)? eclipsed|it(?: currently)? eclipsed))(?: (?:here|now|right now))?\??$")
            && evidence.LocalEclipseCoverageFraction is { } coverage && double.IsFinite(coverage) && coverage is >= 0d and <= 1d)
        {
            string percent = (coverage * 100d).ToString("0.##", CultureInfo.InvariantCulture);
            answer = coverage == 0d ? "No gas-giant eclipse coverage is currently reported at your location (0 percent of the real star)."
                : $"At your location, the gas giant currently covers {percent} percent of the real star. This local reading does not establish coverage everywhere on the ring.";
            reason = "current_local_eclipse_reading"; return true;
        }
        return false;
    }

    public static string GuardGeneratedAnswer(string answer, out bool replaced)
    {
        string text = Normalize(answer, 2000);
        if (Has(text, @"(?:^|[.!?]\s+)(?:(?:sure|certainly|done|okay|ok)[,!]?\s+)?i(?:'ve| have)?\s+(?:(?:just|now|already|successfully)\s+)*(?:initiated|started|begun|launched)\s+(?:(?:the|an?)\s+)?(?:activation|deactivation|shutdown|startup)\s+sequence\b"))
        { replaced = true; return ReadOnlyReply; }
        foreach (Match match in Regex.Matches(text,
            @"(?:^|[.!?]\s+)(?:(?:sure|certainly|done|okay|ok)[,!]?\s+)?i(?:'ve| have)?\s+(?:(?:just|now|already|successfully)\s+)*(?<verb>activated|deactivated|enabled|disabled|opened|closed|spawned|teleported|changed|set|adjusted|turned|switched)\b(?<target>[^.!?\r\n]{0,180})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string target = match.Groups["target"].Value;
            bool directional = match.Groups["verb"].Value.ToLowerInvariant() is "turned" or "switched";
            if (Has(target, @"\b(?:light|lights|sun|hub|eclipse|storm|weather|door|doors|inventory|player|position|world|clock|time|gravity|settings)\b")
                && (!directional || Has(target, @"\b(?:on|off)\b")))
            { replaced = true; return ReadOnlyReply; }
        }
        replaced = false;
        return answer;
    }

    private static bool IsExplicitControlRequest(string question)
    {
        const string prefix = @"^(?:(?:please|can you|could you|would you|will you|i want you to|go ahead and)\s+)*";
        if (Has(question, prefix + @"(?:start|initiate|begin|launch)\s+(?:(?:the|an?)\s+)?(?:activation|deactivation|shutdown|startup)\s+sequence\b")) return true;
        if (!Has(question, prefix + @"(?:turn|switch|activate|deactivate|toggle|enable|disable|open|close|spawn|teleport|set|change|adjust)\b")) return false;
        return Has(question, @"\b(?:light|lights|sun|hub|eclipse|storm|weather|door|doors|inventory|player|position|world|clock|time|gravity|settings)\b")
            || Has(question, prefix + @"(?:spawn|teleport)\b");
    }
    private static bool IsCurrentPrecipitationQuestion(string question) => Has(question,
        @"^(?:is it(?: currently| still)? (?:raining|snowing)|is (?:the )?rain(?: still| currently)? (?:running|falling)|is there (?:rain|snow))(?: (?:here|now|right now|on me))?\??$");
    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Normalize(string? text, int maximum = 512)
    {
        text = (text ?? "").Trim().Replace('\u2019', '\'');
        return text.Length > maximum ? text[..maximum] : text;
    }
}
