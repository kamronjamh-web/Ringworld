using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RingworldTechDemo.Pda;

/// <summary>Narrow deterministic replies from typed evidence and explicit capability limits. This is not a general factual validator.</summary>
public static class PdaAnswerPolicy
{
    public const string ReadOnlyReply = "I can explain the current readings, but I can't change the game or control its systems. No action has been performed.";

    public const string NoImageReply = "I can't see your view: no current-view image is attached. Choose the Gemma model and tick \"Attach current view\" to let me look; otherwise I can only describe the readings.";

    /// <param name="imageAttached">True when this question will carry the current-view image to a vision model.</param>
    public static bool TryAnswer(string question, PdaContextSnapshot snapshot, out string answer, out string reason, bool imageAttached = false)
    {
        answer = ""; reason = "";
        string q = Normalize(question);
        if (IsExplicitControlRequest(q)) { answer = ReadOnlyReply; reason = "read_only_control_request"; return true; }
        if (!imageAttached && Has(q, @"\byou (?:see|seeing)\b|\bmy (?:current )?(?:view|screen)\b|\blooking at\b|\bon (?:my )?screen\b|\bin (?:the )?(?:picture|image|screenshot)\b"))
        { answer = NoImageReply; reason = "view_image_not_attached"; return true; }
        // Explanations, hypothetical instructions, recall and mixed questions need their full context.
        if (Has(q, @"\b(?:why|because|cause|reason|explain|was|were|earlier|before|previously|history|remember)\b|how (?:do|can|would) i")) return false;
        if (TryLookupAnswer(q, snapshot.StateEvidence, out answer, out reason)) return true;
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

    // ---- Deterministic factual lookups (wind, cloud base, time of day, enclosure, storms, eclipse/hub, safety) ----
    private static readonly (string Name, string Pattern)[] LookupTopics =
    {
        ("wind", @"\b(?:wind|winds|windy|breeze|gust|gusts)\b"),
        ("cloudbase", @"\bcloud(?:s)? (?:base|ceiling|height|altitude)\b|\b(?:base|height|altitude|ceiling) of (?:the )?clouds?\b|\bhow (?:high|low) (?:are|is) (?:the )?clouds?\b"),
        ("time", @"\btime of day\b|\bwhat time\b|\b(?:sunset|sunrise|dusk|dawn|daylight|daytime|nighttime)\b|\b(?:get|gets|getting) (?:dark|light)\b|\b(?:day or night|night or day)\b|\bis it (?:day|night)\b|\bhow long (?:until|before) (?:it(?:'s| is| gets)? )?(?:dark|night|light|day)\b|\bwhen (?:will|does|is) it (?:get |going to get )?(?:dark|light|night)\b"),
        ("enclosure", @"\b(?:underground|surface|cave|caves|enclosed|outdoors|outside)\b"),
        ("storm", @"\b(?:megastorm|storms?|weather fronts?)\b"),
        ("hub", @"\bhub\b|\bartificial (?:sun|light)\b"),
        ("eclipse", @"\beclipse|\beclipsed\b"),
        ("safety", @"\b(?:safe|safety|unsafe|dangerous|danger|hazard|hazards|hazardous|at risk)\b")
    };

    private static HashSet<string> DetectTopics(string q)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, string pattern) in LookupTopics) if (Has(q, pattern)) found.Add(name);
        return found;
    }

    private static string? WindSentence(PdaStateEvidence e) => e.Wind is { } w
        ? $"Local wind is {PdaReadingText.Wind(w.SpeedMetersPerSecond, w.AroundMetersPerSecond, w.AcrossMetersPerSecond)}. Spinward means increasing around-ring distance; directions are relative to the ring, not compass points." : null;

    private static string? CloudSentence(PdaStateEvidence e)
    {
        if (e.Cloud is not { } c || !double.IsFinite(c.BaseAltitudeMeters)) return null;
        string n(double v) => PdaContextComposer.Number(v, "0");
        string relation = c.BaseAboveObserverMeters is { } above && double.IsFinite(above)
            ? $", about {n(Math.Abs(above))} m {(above >= 0 ? "above" : "below")} your position" : "";
        return $"The cloud base is at {n(c.BaseAltitudeMeters)} m altitude{relation}; the cloud top is at {n(c.TopAltitudeMeters)} m altitude.";
    }

    private static string? TimeSentence(PdaStateEvidence e)
    {
        if (e.Time is not { } t) return null;
        string phase = PdaReadingText.Phase(t.Phase);
        if (t.SunAboveHorizon)
            return double.IsFinite(t.SecondsUntilSunset)
                ? $"It is {phase} here: the real star is above the horizon, and sunset is in {PdaReadingText.Minutes(t.SecondsUntilSunset)}. It gets dark after sunset."
                : $"It is {phase} here: the real star is above the horizon. I don't have a sunset prediction.";
        return double.IsFinite(t.SecondsUntilSunrise)
            ? $"It is {phase} here: the real star is below the horizon, and sunrise is in {PdaReadingText.Minutes(t.SecondsUntilSunrise)}."
            : $"It is {phase} here: the real star is below the horizon. I don't have a sunrise prediction.";
    }

    private static string? EnclosureSentence(PdaStateEvidence e)
    {
        if (e.Enclosure is not { } z) return null;
        string openness = PdaContextComposer.Number(z.Openness);
        string lighting = z.LightingApplied ? "underground lighting is applied" : "underground lighting is not applied";
        return z.Mode switch
        {
            "Surface" => $"You are on the surface, not underground: enclosure mode is Surface, you are {PdaReadingText.Depth(z.DepthMeters)}, openness is {openness} (1 is open sky), and {lighting}.",
            "Underground" => $"You are underground: enclosure mode is Underground, you are {PdaReadingText.Depth(z.DepthMeters)}, openness is {openness}, and {lighting}.",
            "Entering" => $"You are entering underground (a transition): you are {PdaReadingText.Depth(z.DepthMeters)}, openness is {openness}, and {lighting}.",
            _ => $"You are exiting to the surface (a transition): you are {PdaReadingText.Depth(z.DepthMeters)}, openness is {openness}, and {lighting}."
        };
    }

    private static string StormSentence(PdaStateEvidence e)
    {
        PdaStormEvidence? s = e.Storm;
        string weather = s?.WeatherKind == null ? "" : $" The local weather system is {s.WeatherKind} (influence {PdaContextComposer.Number(s.WeatherInfluence ?? double.NaN)}).";
        string natural = s?.NaturalArrivalPhase == null ? " I have no natural storm-arrival reading."
            : s.NaturalArrivalPhase == "Clear" ? " No natural storm front is arriving at your position."
            : $" A natural storm front is at arrival phase {s.NaturalArrivalPhase}{(s.NaturalSecondsToRain is { } rain && double.IsFinite(rain) ? ", with rain expected in " + PdaReadingText.Minutes(rain) : "")}.";
        if (s?.MegastormTriggered == null) return "I don't have megastorm readings, so I can't say whether a megastorm is underway." + weather + natural;
        if (s.MegastormTriggered == false) return "No scripted megastorm is triggered." + weather + natural;
        if (s.MegastormActiveHere != true) return $"A scripted megastorm is triggered but not active at your position (phase {s.MegastormPhase})." + weather + natural;
        string arrival = s.MegastormSecondsToArrival is { } toArrival && double.IsFinite(toArrival)
            ? toArrival > 0 ? $"its front edge reaches you in {PdaReadingText.Minutes(toArrival)}" + (s.MegastormDistanceToEdgeMeters is { } d && double.IsFinite(d) ? $" ({PdaContextComposer.Number(d, "0")} m away)" : "")
                : "its front edge has already passed you" : "its arrival time is unknown";
        string clear = s.MegastormSecondsToClear is { } toClear && double.IsFinite(toClear) ? $"; it clears in {PdaReadingText.Minutes(toClear)}" : "";
        return $"A scripted megastorm is active at your position (phase {s.MegastormPhase}); {arrival}{clear}." + weather + natural;
    }

    private static string HubSentence(PdaStateEvidence e)
    {
        if (e.HubActive is { } hub) return $"The artificial hub-light output is currently {(hub ? "active" : "inactive")}.";
        if (e.HubRawActive is { } raw) return $"The hub-light reading is {(raw ? "active" : "inactive")}, but the eclipse event system is not bound, so treat that as provisional.";
        return "I don't have a hub-light reading.";
    }

    private static string EclipseSentence(PdaStateEvidence e)
    {
        if (e.LocalEclipseCoverageFraction is not { } coverage || !double.IsFinite(coverage) || coverage is < 0d or > 1d) return "I don't have a local eclipse reading.";
        string percent = (coverage * 100d).ToString("0.##", CultureInfo.InvariantCulture);
        return coverage == 0d ? "No gas-giant eclipse coverage is currently reported at your location (0 percent of the real star)."
            : $"At your location, the gas giant currently covers {percent} percent of the real star. This local reading does not establish coverage everywhere on the ring.";
    }

    private static string PrecipitationSentence(PdaStateEvidence e)
    {
        if (e.PrecipitationInitialized != true || e.PrecipitationDrive is not { } drive) return "The local precipitation-effect reading is unavailable, so rainfall is unknown.";
        string n = drive.ToString("0.###", CultureInfo.InvariantCulture);
        string mode = e.PrecipitationMode == PdaPrecipitationMode.Snow ? "snow" : e.PrecipitationMode == PdaPrecipitationMode.Rain ? "rain" : "negligible (neither rain nor snow)";
        return $"The local precipitation-effect mode is {mode} with drive {n}{(e.PrecipitationPotential is { } p ? $"; the weather model's precipitation potential is {PdaContextComposer.Number(p)}" : "")}.";
    }

    private static string SafetySentence(PdaStateEvidence e)
    {
        var known = new List<string>();
        var unknown = new List<string>();
        if (e.Storm?.MegastormTriggered is { } triggered)
            known.Add(triggered ? (e.Storm.MegastormActiveHere == true ? "a scripted megastorm is active at your position" : "a scripted megastorm is triggered but not active here") : "no scripted megastorm is triggered");
        else unknown.Add("megastorm status");
        if (e.PrecipitationInitialized == true && e.PrecipitationDrive is { } drive)
            known.Add(e.PrecipitationMode == PdaPrecipitationMode.Negligible ? "precipitation-effect drive is negligible" : $"the precipitation effect shows {(e.PrecipitationMode == PdaPrecipitationMode.Snow ? "snow" : "rain")} at drive {drive.ToString("0.###", CultureInfo.InvariantCulture)}");
        else unknown.Add("precipitation effects");
        if (e.Enclosure is { } z) known.Add(z.Mode == "Surface" ? "you are on the surface" : $"enclosure mode is {z.Mode}");
        else unknown.Add("enclosure");
        string readings = known.Count == 0 ? "I have no environment readings to report." : "What the environment readings show: " + string.Join("; ", known) + ".";
        string missing = unknown.Count == 0 ? "" : " Unknown because the readings are missing: " + string.Join(", ", unknown) + ".";
        return "I can't call it safe or unsafe: this PDA has no health, oxygen, injury or whole-ring hazard readings, and environment readings can't prove safety. " + readings + missing;
    }

    private static string? SentenceFor(string topic, PdaStateEvidence e) => topic switch
    {
        "wind" => WindSentence(e) ?? "I don't have a local wind reading.",
        "cloudbase" => CloudSentence(e) ?? "I don't have a cloud-base reading.",
        "time" => TimeSentence(e) ?? "I don't have a time-of-day reading.",
        "enclosure" => EnclosureSentence(e) ?? "Enclosure readings are unavailable, so I can't tell whether you are underground.",
        "storm" => StormSentence(e),
        "hub" => HubSentence(e),
        "eclipse" => EclipseSentence(e),
        "safety" => SafetySentence(e),
        "precipitation" => PrecipitationSentence(e),
        _ => null
    };

    private static bool TryLookupAnswer(string q, PdaStateEvidence? evidence, out string answer, out string reason)
    {
        answer = ""; reason = "";
        if (evidence == null || Has(q, @"\b(?:mean|means|meaning|imply|implies|affect|affects|what can)\b")) return false;
        HashSet<string> topics = DetectTopics(q);
        if (topics.Count == 0) return false;
        // Safety summarizes everything else; otherwise only one lookup topic (or the hub/eclipse pair) may be present.
        if (topics.Contains("safety")) topics.RemoveWhere(t => t != "safety");
        else if (topics.Contains("storm") && Has(q, @"\b(?:rain|raining|snow|snowing|hail|precipitation)\b")) return false;
        else if (topics.Count > 1 && !(topics.Count == 2 && topics.Contains("hub") && topics.Contains("eclipse"))) return false;
        else if (topics.Contains("enclosure") && !Has(q, @"\b(?:underground|surface|cave|caves|enclosed)\b")) return false;
        var sentences = new List<string>();
        foreach (string topic in new[] { "wind", "cloudbase", "time", "enclosure", "storm", "eclipse", "hub", "safety" })
            if (topics.Contains(topic) && SentenceFor(topic, evidence) is { } sentence) sentences.Add(sentence);
        if (sentences.Count == 0) return false;
        answer = string.Join(" ", sentences);
        reason = "current_" + string.Join("_", topics.OrderBy(t => t, StringComparer.Ordinal)) + "_reading";
        return true;
    }

    /// <summary>The exact text the model receives: authoritative deterministic facts first, then the composed readings.</summary>
    public static string ComposeModelContext(string contextText, string question, PdaContextSnapshot snapshot)
    {
        string facts = BuildVerifiedFacts(question, snapshot);
        return facts.Length == 0 ? contextText : "VERIFIED FACTS (authoritative current readings; answer from these first):\n" + facts + "\n\n" + contextText;
    }

    /// <summary>Authoritative facts for explain/why questions, placed first in the prompt so the model reasons from them. Empty when evidence is absent.</summary>
    public static string BuildVerifiedFacts(string question, PdaContextSnapshot snapshot)
    {
        PdaStateEvidence? e = snapshot.StateEvidence;
        if (e == null) return "";
        string q = Normalize(question);
        HashSet<string> topics = DetectTopics(q);
        topics.Remove("safety");
        if (Has(q, @"\b(?:rain|raining|snow|snowing|hail|precipitation|potential)\b")) topics.Add("precipitation");
        if (Has(q, @"\b(?:bright|brighter|brightness|dark|darker|darkness|light|lit|visibility|fog)\b"))
        { topics.Add("time"); topics.Add("eclipse"); topics.Add("hub"); topics.Add("enclosure"); topics.Add("storm"); }
        if (topics.Count == 0) { topics.Add("enclosure"); topics.Add("time"); topics.Add("hub"); topics.Add("storm"); topics.Add("precipitation"); }
        var lines = new List<string>();
        foreach (string topic in new[] { "enclosure", "time", "eclipse", "hub", "storm", "precipitation", "wind", "cloudbase" })
            if (topics.Contains(topic) && SentenceFor(topic, e) is { } sentence) lines.Add("- " + sentence);
        string text = string.Join("\n", lines);
        return text.Length > 1100 ? text[..1100] : text;
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
