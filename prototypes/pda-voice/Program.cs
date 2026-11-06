using PdaVoicePrototype;

// Standalone prototype: small local LLM rewrite + local TTS for a suit-PDA announcer. Not part of the game build.
var cmd = args.FirstOrDefault() ?? "help";
var rest = args.Skip(1).ToArray();
switch (cmd)
{
    case "llm":
        return await LlmBench.RunAll(rest);
    case "llm-worker":
        return await LlmBench.RunWorker(Cli.Value(rest, "--model")!, int.Parse(Cli.Value(rest, "--threads")!), Cli.Value(rest, "--out")!);
    case "qa-worker":
        return await PdaQaWorker.Run(rest);
    case "qa-compare":
        return await PdaModelComparison.Run(rest);
    case "qa-vision-smoke":
        return await PdaVisionSmoke.Run(rest);
    case "tts":
        return await TtsBench.RunAll(rest);
    case "audition":
        return VoiceAudition.Run(rest);
    case "audition-emma":
        return VoiceAudition.RunEmma(rest);
    case "audition-expressive-report":
        return VoiceAudition.RunExpressiveReport(rest);
    case "tts-worker":
        return await TtsBench.RunWorker(Cli.Value(rest, "--engine")!, Cli.Value(rest, "--out")!);
    case "all":
        await LlmBench.RunAll(rest);
        return await TtsBench.RunAll(rest);
    default:
        Console.WriteLine("usage: run.ps1 llm [--models qwen2-0.5b,qwen2.5-0.5b,qwen3-0.6b] [--threads 8,4] | qa-worker [--model qwen3-0.6b] [--threads 4] | qa-compare --model <trial-key> --memory ram|vram [--gpu-layers 8] [--threads 4] [--label pda_8b] [--context baseline|focused] [--guardrails true|false] | qa-vision-smoke --model gemma-3-4b-q4 --memory ram|vram [--gpu-layers 16] [--label pda_memory] | tts | audition | audition-emma | audition-expressive-report | all");
        return 1;
}
