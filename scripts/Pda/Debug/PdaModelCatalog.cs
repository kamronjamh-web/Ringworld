using System;
using System.Collections.Generic;
using System.Linq;

namespace RingworldTechDemo.Pda;

public enum PdaModelFamily { Qwen2, Qwen3, Granite, Llama3, Gemma3 }
public sealed record PdaModelSpec(string Key, string DisplayName, string RelativeFile, PdaModelFamily Family,
    bool ComparisonOnly = false, int StartupTimeoutSeconds = 45, int RequestTimeoutSeconds = 60);

/// <summary>Shared by the game and its isolated comparison helper. Trial entries do not change the runtime default or UI choices.</summary>
public static class PdaModelCatalog
{
    public const string DefaultModelKey = "qwen3-0.6b";
    public static IReadOnlyList<PdaModelSpec> All { get; } = Array.AsReadOnly(new[]
    {
        new PdaModelSpec("qwen2-0.5b", "Qwen2 0.5B", "qwen2-0_5b-instruct-q4_k_m.gguf", PdaModelFamily.Qwen2),
        new PdaModelSpec("qwen2.5-0.5b", "Qwen2.5 0.5B", "qwen2.5-0.5b-instruct-q4_k_m.gguf", PdaModelFamily.Qwen2),
        new PdaModelSpec(DefaultModelKey, "Qwen3 0.6B", "Qwen3-0.6B-Q8_0.gguf", PdaModelFamily.Qwen3),
        new PdaModelSpec("qwen3-8b-q4", "Qwen3 8B Q4_K_M (trial)", "_comparison/Qwen3-8B-Q4_K_M.gguf", PdaModelFamily.Qwen3, true, 180, 180),
        new PdaModelSpec("granite-3.3-8b-q4", "Granite 3.3 8B Q4_K_M (trial)", "_comparison/granite-3.3-8b-instruct-Q4_K_M.gguf", PdaModelFamily.Granite, true, 180, 180),
        new PdaModelSpec("llama-3.1-8b-q4", "Llama 3.1 8B Q4_K_M (trial)", "_comparison/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf", PdaModelFamily.Llama3, true, 180, 180),
        new PdaModelSpec("gemma-3-4b-q4", "Gemma3 4B Instruct Q4_K_M (trial)", "_comparison/gemma-3-4b-it-Q4_K_M.gguf", PdaModelFamily.Gemma3, true, 180, 180)
    });

    public static IReadOnlyList<PdaModelSpec> DebugChoices { get; } = Array.AsReadOnly(new[]
    {
        Get(DefaultModelKey), Get("qwen2.5-0.5b"), Get("qwen2-0.5b"), Get("qwen3-8b-q4"), Get("gemma-3-4b-q4")
    });

    public static PdaModelSpec Get(string key) => All.FirstOrDefault(model => string.Equals(model.Key, key, StringComparison.Ordinal))
        ?? throw new ArgumentException("Unknown local PDA model: " + key, nameof(key));
}

/// <summary>Validates the native GGUF template's family. It never substitutes a generic format for an unsupported template.</summary>
// Official template references: https://huggingface.co/ibm-granite/granite-3.3-8b-instruct/raw/main/tokenizer_config.json
// https://huggingface.co/Qwen/Qwen3-8B/raw/main/tokenizer_config.json
// https://ai.google.dev/gemma/docs/core/prompt-structure
public static class PdaModelPromptFormat
{
    public const string EmptyThinkingSuffix = "<think>\n\n</think>\n\n";
    public const string MediaMarker = "<__media__>";
    private static readonly string[] GemmaControlMarkers = { "<start_of_turn>", "<end_of_turn>", "<bos>", "<eos>" };

    public static bool ContainsRequestControlTokens(string text) => text.Contains("<|", StringComparison.Ordinal)
        || text.Contains("<think", StringComparison.OrdinalIgnoreCase)
        || text.Contains(MediaMarker, StringComparison.Ordinal)
        || GemmaControlMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal));

    public static string SystemText(PdaModelFamily family, string text) => family == PdaModelFamily.Qwen3 ? text + " /no_think" : text;

    public static string CompleteRenderedPrompt(PdaModelFamily family, string prompt, bool allowSingleImageMarker = false)
    {
        if (allowSingleImageMarker && family != PdaModelFamily.Gemma3) throw new FormatException("Only Gemma3 supports the debug image marker.");
        bool qwen = family is PdaModelFamily.Qwen2 or PdaModelFamily.Qwen3;
        bool correct = family switch
        {
            PdaModelFamily.Qwen2 or PdaModelFamily.Qwen3 => prompt.Contains("<|im_start|>system\n", StringComparison.Ordinal)
                && prompt.Contains("<|im_start|>user\n", StringComparison.Ordinal) && prompt.Contains("<|im_end|>", StringComparison.Ordinal)
                && (prompt.EndsWith("<|im_start|>assistant\n", StringComparison.Ordinal)
                    || (family == PdaModelFamily.Qwen3 && prompt.EndsWith("<|im_start|>assistant\n" + EmptyThinkingSuffix, StringComparison.Ordinal))),
            PdaModelFamily.Granite => prompt.StartsWith("<|start_of_role|>system<|end_of_role|>", StringComparison.Ordinal)
                && prompt.Contains("<|start_of_role|>user<|end_of_role|>", StringComparison.Ordinal)
                && prompt.Contains("<|end_of_text|>", StringComparison.Ordinal)
                && prompt.EndsWith("<|start_of_role|>assistant<|end_of_role|>", StringComparison.Ordinal),
            PdaModelFamily.Llama3 => prompt.Contains("<|start_header_id|>system<|end_header_id|>\n\n", StringComparison.Ordinal)
                && prompt.Contains("<|start_header_id|>user<|end_header_id|>\n\n", StringComparison.Ordinal)
                && prompt.Contains("<|eot_id|>", StringComparison.Ordinal)
                && prompt.EndsWith("<|start_header_id|>assistant<|end_header_id|>\n\n", StringComparison.Ordinal),
            PdaModelFamily.Gemma3 => IsGemma3Prompt(prompt, allowSingleImageMarker),
            _ => false
        };
        if (!qwen && (prompt.Contains("<|im_start|>", StringComparison.Ordinal) || prompt.Contains("<|im_end|>", StringComparison.Ordinal))) correct = false;
        if (family != PdaModelFamily.Qwen3 && (prompt.Contains("/no_think", StringComparison.Ordinal)
            || prompt.Contains("<think>", StringComparison.Ordinal) || prompt.Contains("</think>", StringComparison.Ordinal))) correct = false;
        if (!correct) throw new FormatException("The GGUF chat template does not render the expected " + family + " role/EOS format; this model is unsupported by the pinned runtime.");
        return family == PdaModelFamily.Qwen3 && !prompt.EndsWith(EmptyThinkingSuffix, StringComparison.Ordinal) ? prompt + EmptyThinkingSuffix : prompt;
    }

    private static bool IsGemma3Prompt(string prompt, bool allowSingleImageMarker)
    {
        // Gemma folds our system message into the first user turn; assistant turns use the native 'model' role.
        string turns = prompt.StartsWith("<bos>", StringComparison.Ordinal) ? prompt[5..] : prompt;
        const string userHeader = "<start_of_turn>user\n";
        const string answerSuffix = "<end_of_turn>\n<start_of_turn>model\n";
        if (!turns.StartsWith(userHeader, StringComparison.Ordinal) || !turns.EndsWith(answerSuffix, StringComparison.Ordinal)
            || turns.Length <= userHeader.Length + answerSuffix.Length) return false;
        string body = turns[userHeader.Length..^answerSuffix.Length];
        if (allowSingleImageMarker)
        {
            int marker = body.IndexOf(MediaMarker, StringComparison.Ordinal);
            if (marker < 0 || marker != body.LastIndexOf(MediaMarker, StringComparison.Ordinal)) return false;
            body = body.Remove(marker, MediaMarker.Length);
        }
        int separator = body.IndexOf("\n\n", StringComparison.Ordinal);
        return separator > 0 && !string.IsNullOrWhiteSpace(body[..separator])
            && !string.IsNullOrWhiteSpace(body[(separator + 2)..]) && !ContainsRequestControlTokens(body);
    }

    public static IReadOnlyList<string> StopSequences(PdaModelFamily family) => family switch
    {
        PdaModelFamily.Qwen2 or PdaModelFamily.Qwen3 => Array.AsReadOnly(new[] { "<|im_end|>", "<|im_start|>" }),
        PdaModelFamily.Granite => Array.AsReadOnly(new[] { "<|end_of_text|>", "<|start_of_role|>" }),
        PdaModelFamily.Llama3 => Array.AsReadOnly(new[] { "<|eot_id|>", "<|end_of_text|>", "<|start_header_id|>" }),
        PdaModelFamily.Gemma3 => Array.AsReadOnly(new[] { "<end_of_turn>", "<eos>", "<start_of_turn>" }),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
}
