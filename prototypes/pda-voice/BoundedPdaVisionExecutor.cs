using LLama;

namespace PdaVoicePrototype;

/// <summary>One image request owns one context and executor; retained projector weights are shared serially by the helper.</summary>
internal sealed class BoundedPdaVisionExecutor : InteractiveExecutor, IDisposable
{
    private readonly MtmdWeights _projector;
    private readonly int _positionBudget;
    public int PromptPositions { get; private set; }
    public int PromptTokens { get; private set; }

    public BoundedPdaVisionExecutor(LLamaContext context, MtmdWeights projector, int positionBudget) : base(context, projector)
    {
        _projector = projector;
        _positionBudget = positionBudget;
    }

    protected override async Task PreprocessInputs(string? text, InferStateArgs args, CancellationToken cancellationToken)
    {
        await base.PreprocessInputs(text, args, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var chunks = MtmdChunks ?? throw new InvalidDataException("The image prompt did not produce multimodal chunks.");
        long positions = chunks.CountPositions();
        ulong tokens = chunks.CountTokens();
        if (positions > 0 && positions <= int.MaxValue) PromptPositions = (int)positions;
        if (tokens <= int.MaxValue) PromptTokens = (int)tokens;
        if (positions <= 0 || positions > _positionBudget || tokens > int.MaxValue)
            throw new InvalidDataException("The image and text exceed the bounded PDA context-position budget.");
    }

    public void Dispose()
    {
        try { DisposeMtmdChunks(); }
        finally
        {
            try { DisposeEmbeds(); }
            finally { _projector.ClearMedia(); }
        }
    }
}
