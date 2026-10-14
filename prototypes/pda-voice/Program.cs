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
    case "tts":
        return await TtsBench.RunAll(rest);
    case "tts-worker":
        return await TtsBench.RunWorker(Cli.Value(rest, "--engine")!, Cli.Value(rest, "--out")!);
    case "all":
        await LlmBench.RunAll(rest);
        return await TtsBench.RunAll(rest);
    default:
        Console.WriteLine("usage: run.ps1 llm [--models qwen2-0.5b,qwen2.5-0.5b,qwen3-0.6b] [--threads 8,4] | tts | all");
        return 1;
}
