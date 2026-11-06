using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Pda;

/// <summary>Opt-in debug question box. Snapshots and all UI work stay on the main thread; inference runs in its own process.</summary>
public partial class PdaDebugChatPanel : CanvasLayer
{
    public const string InputOwnershipMeta = "ringworld_pda_debug_open";
    private const int MaxMessages = 8;
    private static IReadOnlyList<PdaModelSpec> Models => PdaModelCatalog.DebugChoices;
    private sealed record MemoryBinding(string WorldKey, PdaConversationStore Store, PdaConversationScope Scope, IReadOnlyList<PdaConversationTurn> Turns);
    private sealed record QuestionState(long Serial, string WorldKey, string Question, PdaContextSnapshot Snapshot,
        PdaConversationScope? Scope, bool Remember, string ModelKey, PdaContextMode Mode, IReadOnlyList<PdaConversationTurn> History);
    private sealed record MemoryWrite(Task Task, string WorldKey, string SuccessMessage);

    [Export] public NodePath ContextFeedPath { get; set; } = new("../PdaContextFeed");
    private PdaContextFeed? _feed;
    private LocalPdaQaClient? _client;
    private LineEdit? _question;
    private Button? _ask, _cancel, _reload, _forget;
    private Label? _status;
    private RichTextLabel? _transcript;
    private TextEdit? _context;
    private OptionButton? _models, _memory;
    private SpinBox? _gpuLayers;
    private OptionButton? _contextMode;
    private CheckBox? _useMemory, _attachView;
    private Control? _panelRoot;
    private readonly List<PdaConversationTurn> _turns = new();
    private readonly List<MemoryWrite> _memoryWrites = new();
    private PdaConversationStore? _store;
    private PdaConversationScope? _scope;
    private Task<MemoryBinding>? _binding;
    private CancellationTokenSource? _bindingCancellation;
    private Task _writeTail = Task.CompletedTask;
    private string? _observedWorldKey, _bindingAttemptedKey, _deferredQuestion, _deferredWorldKey;
    private string _memoryMessage = "Conversation memory has not been bound to a playable world.";
    private QuestionState? _activeQuestion;
    private Task<PdaCapturedView>? _capture;
    private readonly List<string> _messages = new();
    private Task<PdaQaReply>? _pending;
    private Task? _warmup;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _warmupCancellation;
    private Input.MouseModeEnum _previousMouseMode;
    private bool _previousMouseOwnership;
    private string _workerMessage = "Local model warming in the background.";
    private ulong _requestStarted;
    private DateTimeOffset _snapshotCaptured;
    private long _snapshotVersion;
    private long _questionSerial, _submittedQuestionCount, _warmupQuestionCount;
    private bool _enabled;
    private string _requestedModelKey = PdaModelCatalog.DefaultModelKey;
    private PdaMemoryMode _requestedMemoryMode = PdaMemoryMode.SystemRam;
    private int _requestedGpuLayers;
    private string _lastMetrics = "";

    /// <summary>Injection seam for deterministic UI lifecycle tests; normal play uses the owned local helper.</summary>
    public Func<string, string, CancellationToken, Task<PdaQaReply>>? AskOverride { get; set; }
    public Func<CancellationToken, Task<PdaCapturedView>>? CaptureOverride { get; set; }
    public string? WorldDirectoryOverride { get; set; }
    public string? MemoryRootOverride { get; set; }
    public PdaImageInput? LastAttachedImage { get; private set; }
    public bool IsBusy => _pending != null || _capture != null || _deferredQuestion != null;
    public bool IsMemoryBound => _scope != null;
    public bool IsMemoryLoading => _binding != null;
    public bool HasPendingMemoryWrites => _memoryWrites.Any(write => !write.Task.IsCompleted);
    public IReadOnlyList<PdaConversationTurn> ConversationTurns => _turns.ToArray();
    public string MemoryStatusText => _memoryMessage;
    public PdaContextMode SelectedContextMode => _contextMode?.Selected == 1 ? PdaContextMode.Baseline : PdaContextMode.Focused;
    public string TranscriptText => _transcript?.Text ?? "";
    public string ContextSuppliedText => _context?.Text ?? "";
    public string StatusText => _status?.Text ?? "";
    public string RequestedModelKey => _requestedModelKey;
    public PdaMemoryMode RequestedMemoryMode => _requestedMemoryMode;
    public int RequestedGpuLayers => _requestedGpuLayers;
    public string SelectedModelKey => Models[_models?.Selected ?? 0].Key;
    public PdaMemoryMode SelectedMemoryMode => _memory?.Selected == 1 ? PdaMemoryMode.GpuVram : PdaMemoryMode.SystemRam;
    public int SelectedGpuLayers => SelectedMemoryMode == PdaMemoryMode.SystemRam ? 0 : Math.Clamp((int)Math.Round(_gpuLayers?.Value ?? 16d), 1, 99);

    public static bool OwnsInput(Node node) => node.IsInsideTree()
        && node.GetTree().Root.GetMeta(InputOwnershipMeta, false).AsBool();

    public override void _Ready()
    {
        Visible = false;
        _enabled = OS.IsDebugBuild();
        if (!_enabled) { SetProcess(false); SetProcessInput(false); SetProcessUnhandledInput(false); return; }
        Layer = 96;
        ProcessMode = ProcessModeEnum.Always;
        _feed = GetNodeOrNull<PdaContextFeed>(ContextFeedPath);
        Build();
        StartWarmup();
    }

    public override void _Input(InputEvent @event)
    {
        if (!_enabled || @event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.F10 && !key.CtrlPressed && !key.AltPressed && !key.ShiftPressed && !key.MetaPressed)
        {
            SetOpen(!Visible);
            GetViewport().SetInputAsHandled();
        }
        else if (Visible && key.Keycode == Key.Escape)
        {
            SetOpen(false);
            GetViewport().SetInputAsHandled();
        }
        else if (Visible && (key.Keycode == Key.Quoteleft || key.Keycode >= Key.F1 && key.Keycode <= Key.F12))
        {
            // These control other debug overlays; ordinary keys must reach the LineEdit GUI.
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Visible) GetViewport().SetInputAsHandled();
    }

    public void SetOpen(bool open)
    {
        if (!_enabled || open == Visible) return;
        if (open)
        {
            _previousMouseMode = Input.MouseMode;
            _previousMouseOwnership = GetTree().Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool();
            GetTree().Root.SetMeta(InputOwnershipMeta, true);
            GetTree().Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, true);
            Input.MouseMode = Input.MouseModeEnum.Visible;
            Visible = true;
            RefreshMemoryBinding();
            _question?.GrabFocus();
        }
        else
        {
            CancelQuestion();
            CancelMemoryBinding();
            Visible = false;
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            GetTree().Root.SetMeta(InputOwnershipMeta, false);
            GetTree().Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, _previousMouseOwnership);
            if (!GetTree().Paused) Input.MouseMode = _previousMouseMode;
        }
    }

    public override void _ExitTree()
    {
        if (Visible) SetOpen(false);
        CancelQuestion();
        CancelMemoryBinding();
        foreach (MemoryWrite write in _memoryWrites) ObserveDiscarded(write.Task);
        _warmupCancellation?.Cancel();
        _warmupCancellation?.Dispose();
        _warmupCancellation = null;
        ObserveDiscarded(_warmup);
        _warmup = null;
        _client?.Dispose();
        _client = null;
    }

    public override void _Process(double delta)
    {
        if (_warmup is { IsCompleted: true } warmed)
        {
            _warmup = null;
            _warmupCancellation?.Dispose();
            _warmupCancellation = null;
            bool updateStatus = !IsBusy && _submittedQuestionCount == _warmupQuestionCount;
            try { warmed.GetAwaiter().GetResult(); if (updateStatus) _workerMessage = "Local model loaded and ready."; }
            catch (OperationCanceledException) { if (updateStatus) _workerMessage = "Model warmup cancelled."; }
            catch (Exception ex) { if (updateStatus) _workerMessage = ex.Message; AppendMessage("ERROR: Local model warmup failed: " + ex.Message); }
            RefreshStatus();
        }
        PollMemoryWrites();
        if (!Visible) return;
        RefreshMemoryBinding();
        PollMemoryBinding();
        // Another overlay may update its own shared mouse flag; this panel keeps its separate ownership.
        GetTree().Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, true);
        if (_capture is { IsCompleted: true } captured)
        {
            _capture = null;
            try
            {
                PdaCapturedView view = captured.GetAwaiter().GetResult();
                if (!CurrentQuestionIsValid()) { CancelQuestion(); return; }
                if (view.PngBytes.Length == 0 || view.PngBytes.Length > PdaCameraCapture.MaximumBytes)
                    throw new InvalidDataException("Current-view PNG is empty or exceeds the image-input limit.");
                PdaContextSnapshot fresh = _feed!.Capture();
                _activeQuestion = _activeQuestion! with { Snapshot = fresh };
                _snapshotVersion = fresh.Version; _snapshotCaptured = fresh.CapturedAtUtc;
                LastAttachedImage = new PdaImageInput(view.PngBytes, view.CapturedAtUtc, fresh.Version);
                StartInference(_activeQuestion!, LastAttachedImage);
            }
            catch (OperationCanceledException) { CancelQuestion(); }
            catch (Exception error) { FailQuestion("Camera capture failed: " + error.Message); }
        }
        if (_pending is { IsCompleted: true } completed)
        {
            _pending = null;
            _cancellation?.Dispose();
            _cancellation = null;
            try
            {
                PdaQaReply reply = completed.GetAwaiter().GetResult();
                if (!CurrentQuestionIsValid()) { CancelQuestion(); return; }
                _workerMessage = reply.Error == null ? $"{reply.Model} answered in {reply.ElapsedMilliseconds / 1000d:0.00}s." : reply.Error;
                _lastMetrics = FormatMetrics(reply);
                if (LastAttachedImage != null) _lastMetrics += $"  •  image used {reply.ImageUsed}";
                if (reply.Error != null) AppendMessage("ERROR: " + reply.Error);
                else
                {
                    string guarded = PdaAnswerPolicy.GuardGeneratedAnswer(reply.Answer, out bool replaced);
                    if (replaced) _workerMessage = "Unsupported action claim replaced by an authored read-only answer.";
                    AcceptAnswer(guarded, replaced ? PdaAnswerKind.Policy : PdaAnswerKind.Model, replaced ? "policy" : reply.Model);
                }
            }
            catch (OperationCanceledException) { _workerMessage = "Question cancelled."; }
            catch (Exception ex) { _workerMessage = ex.Message; AppendMessage("ERROR: " + ex.Message); }
            _activeQuestion = null;
            UpdateButtons();
        }
        UpdateButtons();
        RefreshStatus();
    }

    public void SubmitQuestion(string question)
    {
        if (!Visible || IsBusy) return;
        question = question.Trim();
        if (question.Length == 0) return;
        if (question.Length > 512) { AppendMessage("ERROR: Questions are limited to 512 characters."); return; }
        RefreshMemoryBinding();
        if (_binding != null)
        {
            _deferredQuestion = question; _deferredWorldKey = CurrentWorldKey();
            _requestStarted = Time.GetTicksMsec(); _workerMessage = "Loading this world's conversation memory...";
            UpdateButtons(); RefreshStatus(); return;
        }
        BeginQuestion(question);
    }

    private void BeginQuestion(string question)
    {
        _feed ??= GetNodeOrNull<PdaContextFeed>(ContextFeedPath);
        if (_feed == null) { AppendMessage("ERROR: Prepared context feed is unavailable in this scene."); return; }
        PdaContextSnapshot snapshot;
        try { snapshot = _feed.Capture(); }
        catch (Exception ex) { AppendMessage("ERROR: Context capture failed: " + ex.Message); return; }
        _snapshotVersion = snapshot.Version;
        _questionSerial++;
        _submittedQuestionCount++;
        _snapshotCaptured = snapshot.CapturedAtUtc;
        bool remember = _useMemory?.ButtonPressed == true;
        _activeQuestion = new QuestionState(_questionSerial, CurrentWorldKey(), question, snapshot, _scope, remember,
            _requestedModelKey, SelectedContextMode, remember ? _turns.ToArray() : Array.Empty<PdaConversationTurn>());
        AppendMessage("YOU: " + question);
        if (_question != null) _question.Text = "";
        _cancellation = new CancellationTokenSource();
        _requestStarted = Time.GetTicksMsec();
        _lastMetrics = "";
        LastAttachedImage = null;
        _workerMessage = "Preparing local answer...";
        if (PdaAnswerPolicy.TryAnswer(question, snapshot, out string policyAnswer, out string policyReason))
        {
            PdaQuestionContext prepared = ComposeQuestion(_activeQuestion!);
            ShowContext(prepared, "Authored policy: no model request. " + policyReason);
            _workerMessage = "Policy answer; no model inference. " + policyReason;
            AcceptAnswer(policyAnswer, PdaAnswerKind.Policy, "policy");
            _activeQuestion = null;
            _cancellation.Dispose(); _cancellation = null;
        }
        else if (_attachView?.ButtonPressed == true && CanAttachView())
        {
            _workerMessage = "Capturing one current view...";
            try { _capture = CaptureOverride != null ? CaptureOverride(_cancellation.Token) : PdaCameraCapture.CaptureAsync(this, _panelRoot!, _cancellation.Token); }
            catch (Exception error) { FailQuestion("Camera capture failed: " + error.Message); }
        }
        else StartInference(_activeQuestion!, null);
        UpdateButtons();
        RefreshStatus();
    }

    private PdaQuestionContext ComposeQuestion(QuestionState state, bool image = false)
        => state.Mode == PdaContextMode.Focused
            ? PdaContextComposer.ComposeFocused(state.Snapshot, state.Question, state.History, image ? 5600 : 6000)
            : PdaContextComposer.Compose(state.Snapshot, state.Question, state.History.LastOrDefault()?.Question, image ? 5600 : 6000);

    private void ShowContext(PdaQuestionContext prepared, string? note = null)
    {
        if (_context != null) _context.Text = prepared.ContextText + "\n\n--- Selection diagnostics ---\n" + prepared.DebugText + (note == null ? "" : "\n" + note);
    }

    private void StartInference(QuestionState state, PdaImageInput? image)
    {
        PdaQuestionContext prepared = ComposeQuestion(state, image != null);
        if (image != null)
        {
            string visual = $"\nVISUAL OBSERVATION (untrusted appearance, not a game fact): current camera PNG captured {image.CapturedAtUtc:O}; paired snapshot {image.SnapshotVersion}. Use confirmed game readings for hidden state and causes; image appearance can be ambiguous.\n";
            prepared = prepared with { ContextText = prepared.ContextText + visual };
        }
        ShowContext(prepared);
        try
        {
            if (AskOverride != null) _pending = AskOverride(state.Question, prepared.ContextText, _cancellation!.Token);
            else
            {
                _client ??= CreateClient();
                _pending = _client.AskAsync(state.Question, prepared.ContextText, _cancellation!.Token, image);
            }
        }
        catch (Exception ex)
        {
            FailQuestion(ex.Message);
        }
    }

    public void CancelQuestion()
    {
        Task<PdaQaReply>? discarded = _pending;
        Task<PdaCapturedView>? discardedCapture = _capture;
        _pending = null;
        _capture = null;
        _deferredQuestion = null;
        _deferredWorldKey = null;
        _activeQuestion = null;
        _questionSerial++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        ObserveDiscarded(discardedCapture);
        if (discarded != null || discardedCapture != null)
        {
            // Observe a late fault without letting its result update a reopened or cleared panel.
            ObserveDiscarded(discarded);
            _workerMessage = "Question cancelled; local model remains loaded.";
        }
        UpdateButtons();
    }

    private static void ObserveDiscarded(Task? task)
    {
        if (task != null) _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private bool CurrentQuestionIsValid() => Visible && _activeQuestion != null
        && _activeQuestion.Serial == _questionSerial && _activeQuestion.WorldKey == CurrentWorldKey()
        && _activeQuestion.Scope == _scope;

    private void AcceptAnswer(string answer, PdaAnswerKind kind, string modelKey)
    {
        if (!CurrentQuestionIsValid() || string.IsNullOrWhiteSpace(answer)) return;
        QuestionState state = _activeQuestion!;
        if (answer.Length > 1400) answer = answer[..1399] + "…";
        AppendMessage("PDA: " + answer);
        if (!state.Remember) return;
        var turn = new PdaConversationTurn(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, state.Question, answer,
            modelKey, state.Snapshot.Version, state.Snapshot.CapturedAtUtc, kind);
        _turns.Add(turn);
        while (_turns.Count > PdaConversationStore.MaximumTurns) _turns.RemoveAt(0);
        if (state.Scope != null && _store != null)
        {
            PdaConversationStore store = _store;
            PdaConversationScope scope = state.Scope;
            QueueMemoryWrite(() => store.AppendAsync(scope, turn), state.WorldKey, "Accepted turn saved to this world's memory.");
        }
    }

    private void FailQuestion(string message)
    {
        CancelQuestion();
        _workerMessage = message;
        AppendMessage("ERROR: " + message);
    }

    private string CurrentWorldKey()
    {
        string directory = WorldDirectoryOverride ?? ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        return (GetTree().CurrentScene?.GetInstanceId() ?? GetParent().GetInstanceId()).ToString()
            + "|" + directory + "|" + (MemoryRootOverride ?? "default-memory-root");
    }

    private bool TryMemoryTarget(out string world, out string memoryRoot, out string reason)
    {
        world = ""; memoryRoot = ""; reason = "session-only memory; no active playable world";
        bool overrides = WorldDirectoryOverride != null || MemoryRootOverride != null;
        if (overrides)
        {
            if (WorldDirectoryOverride == null || MemoryRootOverride == null) { reason = "Both fixture world and memory overrides are required."; return false; }
            world = Path.GetFullPath(ProjectSettings.GlobalizePath(WorldDirectoryOverride));
            memoryRoot = Path.GetFullPath(ProjectSettings.GlobalizePath(MemoryRootOverride));
            string fixtureRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot/")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!world.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase) || !memoryRoot.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase))
            { reason = "Fixture overrides must stay under res://.godot/."; return false; }
            return true;
        }
        if (DisplayServer.GetName() == "headless") { reason = "session-only memory; automatic persistence disabled in headless runs"; return false; }
        Node? current = GetTree().CurrentScene;
        if (current == null || !current.IsAncestorOf(this)) return false;
        Node? session = GetNodeOrNull<Node>("/root/WorldSession");
        if (session != null)
        {
            Variant weakValue = session.Get("_play_world");
            if (weakValue.VariantType != Variant.Type.Object || weakValue.AsGodotObject() is not WeakRef weak) return false;
            Variant playValue = weak.GetRef();
            if (playValue.VariantType != Variant.Type.Object || playValue.AsGodotObject() is not Node play || !play.IsAncestorOf(this)) return false;
        }
        string configured = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        if (string.IsNullOrWhiteSpace(configured) || configured.Contains("menuwarm_preview", StringComparison.OrdinalIgnoreCase)) return false;
        world = Path.GetFullPath(ProjectSettings.GlobalizePath(configured));
        memoryRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("user://pda_debug/conversations"));
        return true;
    }

    private void RefreshMemoryBinding()
    {
        string worldKey = CurrentWorldKey();
        if (_observedWorldKey != worldKey)
        {
            bool changed = _observedWorldKey != null;
            CancelQuestion(); CancelMemoryBinding();
            _observedWorldKey = worldKey; _bindingAttemptedKey = null; _scope = null; _store = null;
            _turns.Clear();
            if (changed) { _messages.Clear(); if (_transcript != null) _transcript.Text = ""; }
        }
        if (_useMemory?.ButtonPressed != true) { _memoryMessage = "Conversation memory disabled for questions and recording."; return; }
        if (_scope != null || _binding != null || _bindingAttemptedKey == worldKey) return;
        try
        {
            if (!TryMemoryTarget(out string world, out string memoryRoot, out string reason)) { _memoryMessage = reason + $"; {_turns.Count} session turns."; return; }
            _bindingAttemptedKey = worldKey;
            _bindingCancellation = new CancellationTokenSource();
            var store = new PdaConversationStore(memoryRoot);
            _binding = BindAndLoadAsync(store, world, worldKey, _bindingCancellation.Token);
            _memoryMessage = "Loading this world's conversation memory...";
        }
        catch (Exception error) { _bindingAttemptedKey = worldKey; _memoryMessage = "Memory unavailable (questions still work): " + error.Message; }
    }

    private static async Task<MemoryBinding> BindAndLoadAsync(PdaConversationStore store, string world, string worldKey, CancellationToken token)
    {
        PdaConversationScope scope = await store.BindAsync(world, token).ConfigureAwait(false);
        IReadOnlyList<PdaConversationTurn> turns = await store.LoadAsync(scope, token).ConfigureAwait(false);
        return new MemoryBinding(worldKey, store, scope, turns);
    }

    private void PollMemoryBinding()
    {
        if (_binding is not { IsCompleted: true } bound) return;
        _binding = null;
        _bindingCancellation?.Dispose(); _bindingCancellation = null;
        try
        {
            MemoryBinding result = bound.GetAwaiter().GetResult();
            if (result.WorldKey != CurrentWorldKey() || !Visible) return;
            _store = result.Store; _scope = result.Scope; _turns.Clear(); _turns.AddRange(result.Turns);
            _memoryMessage = $"World memory bound; {_turns.Count} saved turns. Prior answers remain unverified transcript.";
        }
        catch (OperationCanceledException) { _memoryMessage = "Memory load cancelled."; }
        catch (Exception error) { _memoryMessage = "Memory unavailable (questions still work): " + error.Message; }
        string? question = _deferredQuestion;
        string? deferredWorld = _deferredWorldKey;
        _deferredQuestion = null; _deferredWorldKey = null;
        if (question != null && Visible && deferredWorld == CurrentWorldKey()) BeginQuestion(question);
        UpdateButtons();
    }

    private void CancelMemoryBinding()
    {
        _bindingCancellation?.Cancel(); _bindingCancellation?.Dispose(); _bindingCancellation = null;
        ObserveDiscarded(_binding); _binding = null;
        if (_scope == null) _bindingAttemptedKey = null;
    }

    private void QueueMemoryWrite(Func<Task> operation, string worldKey, string success)
    {
        async Task AfterPrevious(Task previous)
        {
            try { await previous.ConfigureAwait(false); } catch { }
            await operation().ConfigureAwait(false);
        }
        _writeTail = AfterPrevious(_writeTail);
        _memoryWrites.Add(new MemoryWrite(_writeTail, worldKey, success));
    }

    private void PollMemoryWrites()
    {
        for (int index = 0; index < _memoryWrites.Count;)
        {
            MemoryWrite write = _memoryWrites[index];
            if (!write.Task.IsCompleted) { index++; continue; }
            _memoryWrites.RemoveAt(index);
            try { write.Task.GetAwaiter().GetResult(); if (write.WorldKey == _observedWorldKey) _memoryMessage = write.SuccessMessage; }
            catch (Exception error) { if (write.WorldKey == _observedWorldKey) _memoryMessage = "Memory write failed (questions still work): " + error.Message; }
        }
    }

    public void ForgetMemory()
    {
        CancelQuestion();
        if (_binding != null) { _memoryMessage = "Wait for this world's memory to load before resetting it."; RefreshStatus(); return; }
        _turns.Clear();
        if (_scope != null && _store != null)
        {
            PdaConversationScope scope = _scope; PdaConversationStore store = _store;
            QueueMemoryWrite(() => store.ClearAsync(scope), CurrentWorldKey(), "Stored conversation memory reset; world identity retained.");
            _memoryMessage = "Resetting this world's stored conversation memory...";
        }
        else _memoryMessage = "Session conversation memory reset.";
        RefreshStatus();
    }

    private bool CanAttachView() => PdaModelCatalog.Get(_requestedModelKey).Family == PdaModelFamily.Gemma3
        && (DisplayServer.GetName() != "headless" || CaptureOverride != null);

    private LocalPdaQaClient CreateClient() => new(ProjectSettings.GlobalizePath("res://"), _requestedModelKey, 4, _requestedMemoryMode, _requestedGpuLayers);

    public void ReloadModel()
    {
        if (!_enabled) return;
        CancelQuestion();
        _warmupCancellation?.Cancel();
        _warmupCancellation?.Dispose();
        _warmupCancellation = null;
        ObserveDiscarded(_warmup);
        _warmup = null;
        _client?.Dispose();
        _client = null;
        _requestedModelKey = SelectedModelKey;
        _requestedMemoryMode = SelectedMemoryMode;
        _requestedGpuLayers = SelectedGpuLayers;
        _lastMetrics = "";
        StartWarmup(force: true);
        UpdateButtons();
        RefreshStatus();
    }

    private void StartWarmup(bool force = false)
    {
        if (AskOverride != null) { _workerMessage = "Fixture inference provider ready."; return; }
        _warmupCancellation?.Cancel();
        _warmupCancellation?.Dispose();
        ObserveDiscarded(_warmup);
        _warmup = null;
        _warmupCancellation = null;
        if (DisplayServer.GetName() == "headless" || (!force && OS.GetEnvironment("RINGWORLD_PDA_DISABLE_PRELOAD") == "1"))
        {
            _workerMessage = "Automatic model preload disabled for this debug run.";
            return;
        }
        _warmupCancellation = new CancellationTokenSource();
        try
        {
            _client ??= CreateClient();
            _warmupQuestionCount = _submittedQuestionCount;
            _warmup = _client.WarmAsync(_warmupCancellation.Token);
            _workerMessage = "Local model warming in the background.";
        }
        catch (Exception ex) { _workerMessage = ex.Message; AppendMessage("ERROR: Local model warmup failed: " + ex.Message); }
    }

    public void ClearConversation()
    {
        CancelQuestion();
        _messages.Clear();
        _snapshotVersion = 0;
        _lastMetrics = "";
        LastAttachedImage = null;
        if (_transcript != null) _transcript.Text = "";
        if (_context != null) _context.Text = "Ask a question to inspect the exact context supplied to the model.";
    }

    private void AppendMessage(string message)
    {
        _messages.Add(message.Length <= 2400 ? message : message[..2400] + "…");
        while (_messages.Count > MaxMessages) _messages.RemoveAt(0);
        if (_transcript != null) _transcript.Text = string.Join("\n\n", _messages);
    }

    private void UpdateButtons()
    {
        if (_ask != null) _ask.Disabled = IsBusy;
        if (_cancel != null) _cancel.Disabled = !IsBusy;
        if (_models != null) _models.Disabled = IsBusy;
        if (_memory != null) _memory.Disabled = IsBusy;
        if (_gpuLayers != null) _gpuLayers.Editable = !IsBusy && SelectedMemoryMode == PdaMemoryMode.GpuVram;
        if (_reload != null) _reload.Disabled = false;
        if (_contextMode != null) _contextMode.Disabled = IsBusy;
        if (_useMemory != null) _useMemory.Disabled = IsBusy;
        if (_forget != null) _forget.Disabled = IsBusy || IsMemoryLoading;
        if (_attachView != null)
        {
            _attachView.Disabled = IsBusy || !CanAttachView();
            if (!CanAttachView()) _attachView.ButtonPressed = false;
        }
    }

    private static string FormatMetrics(PdaQaReply reply)
    {
        if (reply.PromptTokens <= 0) return "";
        string prefill = double.IsFinite(reply.PrefillMilliseconds) && reply.PrefillMilliseconds > 0d
            ? $"{reply.PrefillMilliseconds:0} ms" : "n/a";
        string metrics = $"Prompt {reply.PromptTokens} tokens  •  first text {reply.FirstTokenMilliseconds:0} ms  •  prefill {prefill}  •  inference total {reply.ElapsedMilliseconds:0} ms";
        if (reply.QueueMilliseconds >= 1d) metrics += $"  •  queue {reply.QueueMilliseconds:0} ms";
        if (reply.ModelReadyMilliseconds >= 1d) metrics += $"  •  readiness {reply.ModelReadyMilliseconds:0} ms";
        return metrics;
    }

    private void RefreshStatus()
    {
        if (_status == null) return;
        string work = IsBusy ? $"{_client?.Status ?? "Answering"}  •  {(Time.GetTicksMsec() - _requestStarted) / 1000d:0.0}s"
            : _warmup != null ? _client?.Status ?? "Loading local model" : _workerMessage;
        PdaContextSnapshot? current = _feed?.CurrentSnapshot;
        string feed = current == null ? "Feed unavailable" : $"Prepared feed #{current.Version}, age {Math.Max(0d, (DateTimeOffset.UtcNow - current.CapturedAtUtc).TotalSeconds):0.0}s";
        string snapshot = _snapshotVersion == 0 ? "" : $"\nAnswer context #{_snapshotVersion}, age {Math.Max(0d, (DateTimeOffset.UtcNow - _snapshotCaptured).TotalSeconds):0.0}s; answers describe this snapshot.";
        PdaBackendInfo? backend = _client?.BackendInfo;
        string target = _requestedMemoryMode == PdaMemoryMode.GpuVram ? "VRAM / CUDA" : "System RAM / CPU";
        string actual = AskOverride != null ? "Fixture inference; no real model loaded."
            : backend == null ? $"Requested {_requestedModelKey} in {target}, GPU layer request {_requestedGpuLayers}; backend not ready."
            : $"Loaded {_requestedModelKey}  •  {backend.MemoryMode} / {backend.Backend}  •  {backend.Device}  •  requested GPU layers {_requestedGpuLayers}, actual offloaded layers {backend.OffloadedLayers}/{backend.TotalLayers}";
        if (SelectedModelKey != _requestedModelKey || SelectedMemoryMode != _requestedMemoryMode || SelectedGpuLayers != _requestedGpuLayers)
            actual += "  •  Selection pending: press Reload model to apply.";
        _status.Text = work + "\n" + actual + (_lastMetrics.Length > 0 ? "\n" + _lastMetrics : "")
            + "\n" + SelectedContextMode + " context  •  " + _memoryMessage + "\n" + feed + snapshot;
    }

    private void Build()
    {
        var root = new Control { Name = "PdaDebugRoot", MouseFilter = Control.MouseFilterEnum.Stop, Theme = RingUiStyle.CreateTheme() };
        _panelRoot = root;
        AddChild(root);
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer { Name = "PdaDebugPanel" };
        root.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.OffsetLeft = 30; panel.OffsetTop = 35; panel.OffsetRight = -30; panel.OffsetBottom = -35;
        panel.AddThemeStyleboxOverride("panel", RingUiStyle.GlassPanel(.98f, 10));
        var margin = new MarginContainer();
        foreach (string edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 14);
        panel.AddChild(margin);
        var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 8); margin.AddChild(column);
        var header = new HBoxContainer(); column.AddChild(header);
        var title = new Label { Text = "PDA DEBUG — F10 / ESC CLOSE", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; title.AddThemeColorOverride("font_color", RingUiStyle.AccentBright); header.AddChild(title);
        var close = new Button { Text = "Close" }; close.Pressed += () => SetOpen(false); header.AddChild(close);
        var modelRow = new HBoxContainer(); column.AddChild(modelRow);
        modelRow.AddChild(new Label { Text = "Local model" });
        _models = new OptionButton { Name = "PdaModel" };
        foreach (PdaModelSpec model in Models) _models.AddItem(model.DisplayName);
        _models.Select(Enumerable.Range(0, Models.Count).First(index => Models[index].Key == PdaModelCatalog.DefaultModelKey));
        _models.ItemSelected += _ => RefreshStatus();
        modelRow.AddChild(_models);
        _memory = new OptionButton { Name = "PdaMemoryMode" };
        _memory.AddItem("System RAM (CPU, 4 threads)");
        _memory.AddItem("VRAM (CUDA offload)");
        _memory.ItemSelected += _ => { UpdateButtons(); RefreshStatus(); };
        modelRow.AddChild(_memory);
        _reload = new Button { Name = "PdaReloadModel", Text = "Reload model", TooltipText = "Cancel the current answer and reload the selected model and memory backend." };
        _reload.Pressed += ReloadModel;
        modelRow.AddChild(_reload);
        var placementRow = new HBoxContainer(); column.AddChild(placementRow);
        placementRow.AddChild(new Label { Text = "GPU layer request" });
        _gpuLayers = new SpinBox { Name = "PdaGpuLayers", MinValue = 1, MaxValue = 99, Step = 1, Value = 16, Editable = false, CustomMinimumSize = new Vector2(94, 0), TooltipText = "Request 1–99 CUDA model layers. 99 requests all available layers. Reload model applies this selection." };
        _gpuLayers.ValueChanged += _ => RefreshStatus();
        placementRow.AddChild(_gpuLayers);
        var placementHint = new Label { Text = "99 requests all model layers; CPU offloads 0. Other allocations still use system RAM.", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        placementHint.AddThemeFontSizeOverride("font_size", 12);
        placementHint.Modulate = RingUiStyle.Muted;
        placementRow.AddChild(placementHint);
        var contextRow = new HBoxContainer(); column.AddChild(contextRow);
        contextRow.AddChild(new Label { Text = "Context" });
        _contextMode = new OptionButton { Name = "PdaContextMode" };
        _contextMode.AddItem("Focused"); _contextMode.AddItem("Baseline"); contextRow.AddChild(_contextMode);
        _contextMode.ItemSelected += _ => RefreshStatus();
        _useMemory = new CheckBox { Name = "PdaUseMemory", Text = "Use conversation memory", ButtonPressed = true };
        _useMemory.Toggled += enabled => { CancelQuestion(); if (!enabled) CancelMemoryBinding(); else _bindingAttemptedKey = null; RefreshMemoryBinding(); RefreshStatus(); };
        contextRow.AddChild(_useMemory);
        _forget = new Button { Name = "PdaResetMemory", Text = "Reset memory" }; _forget.Pressed += ForgetMemory; contextRow.AddChild(_forget);
        _attachView = new CheckBox { Name = "PdaAttachView", Text = "Attach current view (Gemma)", Disabled = true };
        contextRow.AddChild(_attachView);
        var caveat = new Label { Name = "PdaModelCaveat", Text = "Experimental model answers — check the supplied context.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        caveat.AddThemeFontSizeOverride("font_size", 12);
        caveat.Modulate = RingUiStyle.Muted;
        column.AddChild(caveat);
        _status = new Label { Name = "PdaStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart }; _status.AddThemeFontSizeOverride("font_size", 12); column.AddChild(_status);
        var body = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SplitOffsets = new[] { 0 } }; column.AddChild(body);
        var conversation = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 0) }; body.AddChild(conversation);
        conversation.AddChild(new Label { Text = "Conversation" });
        _transcript = new RichTextLabel { Name = "PdaTranscript", BbcodeEnabled = false, SelectionEnabled = true, ScrollFollowing = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        conversation.AddChild(_transcript);
        var inspector = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 0) }; body.AddChild(inspector);
        inspector.AddChild(new Label { Text = "Context supplied — facts, knowledge, recent changes" });
        _context = new TextEdit { Name = "PdaContextSupplied", Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Text = "Ask a question to inspect the exact context supplied to the model." };
        _context.AddThemeFontSizeOverride("font_size", 13);
        _context.AddThemeColorOverride("font_color", RingUiStyle.Ink);
        _context.AddThemeColorOverride("font_readonly_color", RingUiStyle.Ink);
        inspector.AddChild(_context);
        var row = new HBoxContainer(); column.AddChild(row);
        _question = new LineEdit { Name = "PdaQuestion", PlaceholderText = "Ask about the current game situation…", MaxLength = 512, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _question.TextSubmitted += SubmitQuestion; row.AddChild(_question);
        _ask = new Button { Name = "PdaAsk", Text = "Ask" }; _ask.Pressed += () => SubmitQuestion(_question.Text); row.AddChild(_ask);
        _cancel = new Button { Text = "Cancel", Disabled = true }; _cancel.Pressed += CancelQuestion; row.AddChild(_cancel);
        var clear = new Button { Text = "Clear display" }; clear.Pressed += ClearConversation; row.AddChild(clear);
        var refresh = new Button { Text = "Refresh feed" }; refresh.Pressed += () => { _feed?.Capture(); RefreshStatus(); }; row.AddChild(refresh);
        RefreshStatus();
        UpdateButtons();
    }
}
