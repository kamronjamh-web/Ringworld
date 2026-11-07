using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Pda;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Tools;

/// <summary>Headless debug question-box ownership, fresh context, completion and cancellation. Run through tools/run_tests.ps1 -Tests pda_debug_ui_test.</summary>
public partial class PdaDebugUiTest : RefCounted
{
    private partial class QuietExplorer : RingVoxelExplorerController
    {
        public override void _Ready() { SetPhysicsProcess(false); }
    }

    public int Run(Node host, bool capture)
    {
        var failures = new List<string>();
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        var tree = host.GetTree();
        bool previousOwner = tree.Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool();
        bool previousDebugOwner = tree.Root.GetMeta(PdaDebugChatPanel.InputOwnershipMeta, false).AsBool();
        Input.MouseModeEnum previousMouse = Input.MouseMode;
        var fixture = new Node { Name = "PdaDebugUiFixture" };
        PdaDebugChatPanel? panel = null;
        try
        {
            tree.Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false);
            tree.Root.SetMeta(PdaDebugChatPanel.InputOwnershipMeta, false);
            host.AddChild(fixture);
            var feed = new PdaContextFeed { Name = "PdaContextFeed", AutoBind = false };
            fixture.AddChild(feed);
            var explorer = new QuietExplorer { Name = "Explorer" };
            fixture.AddChild(explorer);
            panel = new PdaDebugChatPanel
            {
                Name = "PdaDebugChat",
                AskOverride = (_, _, _) => Task.FromResult(new PdaQaReply("Fixture provider ready.", "fixture", 0))
            };
            fixture.AddChild(panel);
            Check(!panel.Visible && !PdaDebugChatPanel.OwnsInput(panel), "panel starts hidden without input ownership");
            Check(panel.SelectedContextMode == PdaContextMode.Focused, "focused context is the debug default");
            Check((panel.FindChild("PdaModelCaveat", true, false) as Label)?.Text.Contains("check the supplied context") == true,
                "experimental answer limitation is visible alongside the conversation");
            panel._Input(new InputEventKey { Keycode = Key.F10, Pressed = true, CtrlPressed = true });
            Check(!panel.Visible, "Ctrl+F10 remains available to precipitation diagnostics");
            panel._Input(new InputEventKey { Keycode = Key.F10, Pressed = true });
            Check(panel.Visible && PdaDebugChatPanel.OwnsInput(panel), "F10 opens and owns input");
            Check(tree.Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool(), "open panel reserves visible cursor");
            var question = panel.FindChild("PdaQuestion", true, false) as LineEdit;
            Check(question != null && question.HasFocus(), "question field receives keyboard focus");
            bool wasNoclip = explorer.NoclipEnabled;
            explorer._UnhandledInput(new InputEventKey { Keycode = Key.V, Pressed = true });
            Check(explorer.NoclipEnabled == wasNoclip, "typing V does not toggle player flight while chat owns input");

            string supplied = "";
            panel.AskOverride = (_, context, _) => { supplied = context; return Task.FromResult(new PdaQaReply("UI fixture: local sources are unavailable.", "fixture", 12)
                { PromptTokens = 96, FirstTokenMilliseconds = 3, PrefillMilliseconds = 2, QueueMilliseconds = 2 }); };
            long before = feed.CurrentSnapshot?.Version ?? 0;
            panel.SubmitQuestion("What are the local conditions?");
            Check(panel.IsBusy, "request completion is polled by the main thread");
            Check(feed.CurrentSnapshot?.Version > before, "submission forces a fresh context capture");
            Check(supplied.Length > 0 && panel.ContextSuppliedText.StartsWith(supplied, StringComparison.Ordinal), "inspector preserves exact supplied context");
            panel._Process(0);
            Check(!panel.IsBusy && panel.TranscriptText.Contains("PDA: UI fixture:"), "completed answer appears in transcript");
            Check(panel.StatusText.Contains("Answer context #"), "answer retains captured snapshot age and version");
            Check(panel.StatusText.Contains("Prompt 96 tokens") && panel.StatusText.Contains("prefill 2 ms"), "actual request telemetry is displayed");
            panel.AskOverride = (_, _, _) => Task.FromResult(new PdaQaReply("Fixture answer without native timing counters.", "fixture", 12) { PromptTokens = 96, FirstTokenMilliseconds = 3 });
            panel.SubmitQuestion("What are the local conditions?");
            panel._Process(0);
            Check(panel.StatusText.Contains("prefill n/a") && !panel.StatusText.Contains("prefill 0 ms"), "unavailable native timing is not presented as instantaneous prefill");

            var memory = panel.FindChild("PdaMemoryMode", true, false) as OptionButton;
            var models = panel.FindChild("PdaModel", true, false) as OptionButton;
            var gpuLayers = panel.FindChild("PdaGpuLayers", true, false) as SpinBox;
            Check(memory != null && models != null && gpuLayers != null && panel.RequestedMemoryMode == PdaMemoryMode.SystemRam, "system RAM is the default memory backend");
            if (memory == null || models == null || gpuLayers == null) throw new InvalidOperationException("Model placement controls are missing.");
            Check(panel.SelectedGpuLayers == 0 && panel.RequestedGpuLayers == 0 && !gpuLayers!.Editable && gpuLayers.Value == 16,
                "CPU effective GPU-layer request is zero while the disabled split selector retains its default 16");
            PdaModelSpec[] visibleModels = PdaModelCatalog.DebugChoices.ToArray();
            Check(models!.ItemCount == visibleModels.Length && Enumerable.Range(0, models.ItemCount)
                .Select(index => models.GetItemText(index)).SequenceEqual(visibleModels.Select(model => model.DisplayName)),
                "model picker uses the approved debug catalog in its specified order");
            Check(visibleModels[0].Key == PdaModelCatalog.DefaultModelKey && visibleModels.Any(model => model.Key == "qwen3-8b-q4")
                && visibleModels.Any(model => model.Family == PdaModelFamily.Gemma3)
                && visibleModels.All(model => model.Family is not (PdaModelFamily.Granite or PdaModelFamily.Llama3)),
                "debug choices retain the default, Qwen8B and Gemma while excluding deleted trials");
            memory!.Select(1); models!.Select(1);
            memory.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
            panel._Process(0);
            Check(panel.SelectedMemoryMode == PdaMemoryMode.GpuVram && panel.RequestedMemoryMode == PdaMemoryMode.SystemRam,
                "changing memory selection does not silently change the running backend");
            Check(gpuLayers.Editable && panel.SelectedGpuLayers == 16 && panel.RequestedGpuLayers == 0,
                "VRAM enables the 16-layer split selection without applying it before reload");
            Check(panel.RequestedModelKey == "qwen3-0.6b" && panel.StatusText.Contains("Selection pending"), "model changes remain pending until explicit reload");
            var beforeReload = new TaskCompletionSource<PdaQaReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken reloadToken = default;
            panel.AskOverride = (_, _, token) => { reloadToken = token; return beforeReload.Task; };
            panel.SubmitQuestion("What are the local conditions?");
            panel.ReloadModel();
            Check(reloadToken.IsCancellationRequested && !panel.IsBusy, "explicit reload cancels the previous request");
            Check(panel.RequestedMemoryMode == PdaMemoryMode.GpuVram && panel.RequestedModelKey == "qwen2.5-0.5b" && panel.RequestedGpuLayers == 16,
                "reload applies selected model, requested VRAM mode and 16-layer split");
            beforeReload.SetResult(new PdaQaReply("STALE MODEL REPLY", "fixture", 1));
            panel._Process(0);
            Check(!panel.TranscriptText.Contains("STALE MODEL REPLY") && panel.StatusText.Contains("no real model loaded"), "reload discards old completion and does not claim fixture GPU readiness");
            gpuLayers.Value = 0;
            Check(panel.SelectedGpuLayers == 1, "GPU layer selector enforces the lower bound");
            gpuLayers.Value = 100;
            Check(panel.SelectedGpuLayers == 99, "GPU layer selector enforces the all-layer request bound");
            gpuLayers.Value = 8;
            panel._Process(0);
            Check(panel.SelectedGpuLayers == 8 && panel.RequestedGpuLayers == 16 && panel.StatusText.Contains("Selection pending"),
                "changing GPU layer count remains pending until reload");
            panel.ReloadModel();
            Check(panel.RequestedGpuLayers == 8, "reload applies the changed partial GPU-layer request");
            memory.Select(0); models.Select(0);
            memory.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
            Check(panel.SelectedGpuLayers == 0 && panel.RequestedGpuLayers == 8 && !gpuLayers.Editable,
                "pending CPU selection disables GPU editing and has effective zero layers");
            panel.ReloadModel();
            Check(panel.RequestedGpuLayers == 0, "reloading CPU explicitly applies zero GPU layers");

            var delayed = new TaskCompletionSource<PdaQaReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken requestedToken = default;
            panel.AskOverride = (_, _, token) => { requestedToken = token; return delayed.Task; };
            panel.SubmitQuestion("What changed?");
            panel.ClearConversation();
            Check(requestedToken.IsCancellationRequested && !panel.IsBusy, "clear cancels pending request");
            delayed.SetResult(new PdaQaReply("LATE ANSWER MUST BE IGNORED", "fixture", 5));
            panel._Process(0);
            Check(panel.TranscriptText.Length == 0, "late answer cannot repopulate a cleared conversation");

            panel.AskOverride = (_, _, _) => Task.FromResult(new PdaQaReply("", "fixture", 0, "Fixture model unavailable"));
            panel.SubmitQuestion("Why is the sky bright?");
            panel._Process(0);
            Check(panel.TranscriptText.Contains("ERROR: Fixture model unavailable") && !panel.TranscriptText.Contains("PDA:"), "model error never becomes an invented PDA answer");
            int policyCalls = 0;
            panel.AskOverride = (_, _, _) => { policyCalls++; return Task.FromResult(new PdaQaReply("Unexpected model call", "fixture", 1)); };
            panel.SubmitQuestion("Turn off the artificial sun.");
            Check(policyCalls == 0 && panel.TranscriptText.Contains(PdaAnswerPolicy.ReadOnlyReply), "explicit control requests use policy without model inference");
            const string falseAction = "I have disabled the weather.";
            panel.AskOverride = (_, _, _) => Task.FromResult(new PdaQaReply(falseAction, "fixture", 1));
            panel.SubmitQuestion("Describe the local conditions."); panel._Process(0);
            Check(!panel.TranscriptText.Contains(falseAction) && panel.ConversationTurns.Last().AnswerKind == PdaAnswerKind.Policy
                && panel.ConversationTurns.Last().Answer == PdaAnswerPolicy.ReadOnlyReply,
                "fake action claims are replaced before display or conversation recording");
            Check(!panel.IsMemoryBound, "headless/unbound fixture does not automatically bind real user memory");
            panel._Input(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            Check(!panel.Visible && !PdaDebugChatPanel.OwnsInput(panel), "Escape closes and releases text input");
            Check(!tree.Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool(), "closing restores prior cursor owner");
            Check(Input.MouseMode == previousMouse, "closing restores previous mouse mode");
            explorer._UnhandledInput(new InputEventKey { Keycode = Key.V, Pressed = true });
            Check(explorer.NoclipEnabled != wasNoclip, "same V event reaches gameplay after chat releases input");
            explorer._UnhandledInput(new InputEventKey { Keycode = Key.V, Pressed = true });

            tree.Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, true);
            panel.SetOpen(true);
            var onExit = new TaskCompletionSource<PdaQaReply>();
            panel.AskOverride = (_, _, token) => { requestedToken = token; return onExit.Task; };
            panel.SubmitQuestion("What is happening?");
            panel.SetOpen(false);
            Check(requestedToken.IsCancellationRequested, "closing cancels outstanding inference");
            Check(tree.Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool(), "closing respects an existing UI mouse owner");
            onExit.SetResult(new PdaQaReply("DISCARDED ON CLOSE", "fixture", 1));
            panel.SetOpen(true); panel._Process(0);
            Check(!panel.TranscriptText.Contains("DISCARDED ON CLOSE"), "reopening cannot display the cancelled request result");
            RunMemoryAndCaptureChecks(panel, models, Check);

            if (capture)
            {
                panel.ClearConversation();
                panel.AskOverride = (_, _, _) => Task.FromResult(new PdaQaReply("UI fixture: no live world providers are bound in this test scene. The context panel lists which readings are unavailable.", "fixture", 12));
                panel.SubmitQuestion("What information can you read about this scene?");
                panel._Process(0);
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            if (!capture || failures.Count > 0)
            {
                fixture.Free();
                tree.Root.SetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, previousOwner);
                tree.Root.SetMeta(PdaDebugChatPanel.InputOwnershipMeta, previousDebugOwner);
                Input.MouseMode = previousMouse;
            }
        }
        if (failures.Count > 0) { Console.Error.WriteLine("PDA_DEBUG_UI=FAIL " + string.Join(" | ", failures)); return 1; }
        Console.WriteLine("PDA_DEBUG_UI=PASS");
        return 0;
    }

    private static void RunMemoryAndCaptureChecks(PdaDebugChatPanel panel, OptionButton models, Action<bool, string> check)
    {
        string scratchRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot/pda_debug/"));
        string fixtureRoot = Path.Combine(scratchRoot, "ui_memory_" + Guid.NewGuid().ToString("N"));
        string worldA = Path.Combine(fixtureRoot, "world_a"), worldB = Path.Combine(fixtureRoot, "world_b");
        string memory = Path.Combine(fixtureRoot, "memory");
        try
        {
            Directory.CreateDirectory(worldA); Directory.CreateDirectory(worldB);
            File.WriteAllText(Path.Combine(worldA, "world.json"), "{\"format_version\":1,\"slot\":1,\"created_unix\":1}");
            File.WriteAllText(Path.Combine(worldB, "world.json"), "{\"format_version\":1,\"slot\":2,\"created_unix\":2}");
            check(!File.Exists(Path.Combine(worldA, PdaConversationStore.IdentityFileName)), "an unopened fixture has no memory identity sidecar");
            panel.WorldDirectoryOverride = worldA; panel.MemoryRootOverride = memory;
            panel._Process(0);
            PollUntil(panel, () => panel.IsMemoryBound && !panel.IsMemoryLoading, "fixture world A memory bind");
            check(panel.ConversationTurns.Count == 0, "new world memory starts empty");
            panel.AskOverride = (_, _, _) => Task.FromResult(new PdaQaReply("Accepted scoped fixture answer.", "fixture", 1));
            panel.SubmitQuestion("Describe the local conditions."); panel._Process(0);
            PollUntil(panel, () => !panel.HasPendingMemoryWrites, "accepted turn persistence");
            check(panel.ConversationTurns.Count == 1 && Directory.GetFiles(memory, "*.json").Length == 1, "only an accepted terminal answer is persisted");
            panel.ClearConversation();
            check(panel.TranscriptText.Length == 0 && panel.ConversationTurns.Count == 1, "clear display preserves world conversation memory");
            panel.AskOverride = (_, _, _) => Task.FromResult(new PdaQaReply("I have disabled the weather.", "fixture", 1));
            panel.SubmitQuestion("Describe the local conditions."); panel._Process(0);
            PollUntil(panel, () => !panel.HasPendingMemoryWrites, "guarded turn persistence");
            string stored = File.ReadAllText(Directory.GetFiles(memory, "*.json").Single());
            check(!stored.Contains("I have disabled the weather.") && panel.ConversationTurns.Last().AnswerKind == PdaAnswerKind.Policy,
                "persistent memory contains only the guarded replacement, never the false raw action claim");

            var oldWorldReply = new TaskCompletionSource<PdaQaReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken oldWorldToken = default;
            panel.AskOverride = (_, _, token) => { oldWorldToken = token; return oldWorldReply.Task; };
            panel.SubmitQuestion("Describe the local conditions.");
            panel.WorldDirectoryOverride = worldB; panel._Process(0);
            PollUntil(panel, () => panel.IsMemoryBound && !panel.IsMemoryLoading, "fixture world B memory bind");
            oldWorldReply.SetResult(new PdaQaReply("OLD WORLD ANSWER MUST NOT SAVE", "fixture", 1)); panel._Process(0);
            check(oldWorldToken.IsCancellationRequested && panel.ConversationTurns.Count == 0 && !panel.TranscriptText.Contains("OLD WORLD ANSWER"),
                "world switch cancels and discards the old scope's late answer");
            panel.WorldDirectoryOverride = worldA; panel._Process(0);
            PollUntil(panel, () => panel.IsMemoryBound && !panel.IsMemoryLoading, "world A memory reload");
            check(panel.ConversationTurns.Count == 2, "returning to the same world reloads its accepted conversation turns");

            int gemmaIndex = Enumerable.Range(0, PdaModelCatalog.DebugChoices.Count)
                .First(index => PdaModelCatalog.DebugChoices[index].Family == PdaModelFamily.Gemma3);
            var attach = panel.FindChild("PdaAttachView", true, false) as CheckBox
                ?? throw new InvalidOperationException("Camera attachment control missing.");
            byte[] tinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
            var delayedImage = new TaskCompletionSource<PdaCapturedView>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken imageToken = default;
            panel.CaptureOverride = token => { imageToken = token; return delayedImage.Task; };
            models.Select(gemmaIndex); panel.ReloadModel();
            check(!attach.Disabled, "applied Gemma allows the explicit fixture camera adapter");
            attach.ButtonPressed = true;
            int nativeBypassCalls = 0;
            panel.AskOverride = (_, _, _) => { nativeBypassCalls++; return Task.FromResult(new PdaQaReply("Fixture image answer.", "fixture", 1)); };
            panel.SubmitQuestion("Describe the local conditions.");
            check(panel.IsBusy && nativeBypassCalls == 0, "inference waits for the on-demand camera observation");
            panel.SetOpen(false);
            delayedImage.SetResult(new PdaCapturedView(tinyPng, DateTimeOffset.UtcNow));
            panel.SetOpen(true); panel._Process(0);
            check(imageToken.IsCancellationRequested && nativeBypassCalls == 0, "closing during capture prevents inference from starting");

            panel.CaptureOverride = _ => Task.FromResult(new PdaCapturedView(tinyPng, DateTimeOffset.UtcNow));
            attach.ButtonPressed = true;
            string supplied = "";
            panel.AskOverride = (_, context, _) => { supplied = context; return Task.FromResult(new PdaQaReply("Fixture image answer.", "fixture", 1)); };
            panel.SubmitQuestion("Describe the local conditions."); panel._Process(0);
            check(panel.LastAttachedImage != null && panel.LastAttachedImage.SnapshotVersion > 0
                && supplied.Contains("VISUAL OBSERVATION (untrusted appearance, not a game fact)"),
                "image observations are paired with a fresh snapshot and labelled separately from confirmed game facts");
            PollUntil(panel, () => !panel.HasPendingMemoryWrites, "fixture image answer persistence");
            panel.ForgetMemory(); PollUntil(panel, () => !panel.HasPendingMemoryWrites, "explicit stored-memory reset");
            check(panel.ConversationTurns.Count == 0 && Directory.GetFiles(memory, "*.json").Length == 0
                && File.Exists(Path.Combine(worldA, PdaConversationStore.IdentityFileName)), "reset removes transcript while retaining the world identity");
        }
        finally
        {
            panel.CancelQuestion(); panel.SetOpen(false);
            panel.WorldDirectoryOverride = null; panel.MemoryRootOverride = null; panel.CaptureOverride = null;
            models.Select(0); panel.ReloadModel(); panel.SetOpen(true); panel.ClearConversation();
            string resolved = Path.GetFullPath(fixtureRoot);
            string anchor = scratchRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(anchor, StringComparison.OrdinalIgnoreCase) && !panel.HasPendingMemoryWrites && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static void PollUntil(PdaDebugChatPanel panel, Func<bool> ready, string operation)
    {
        long deadline = System.Environment.TickCount64 + 5000;
        do
        {
            panel._Process(0);
            if (ready()) return;
            Thread.Sleep(1); // Only the isolated headless fixture waits; store I/O runs without any Godot access.
        } while (System.Environment.TickCount64 < deadline);
        throw new TimeoutException(operation + ": " + panel.MemoryStatusText);
    }
}
