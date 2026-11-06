using System;
using System.Collections.Generic;
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
            Check(memory != null && models != null && panel.RequestedMemoryMode == PdaMemoryMode.SystemRam, "system RAM is the default memory backend");
            memory!.Select(1); models!.Select(1);
            panel._Process(0);
            Check(panel.SelectedMemoryMode == PdaMemoryMode.GpuVram && panel.RequestedMemoryMode == PdaMemoryMode.SystemRam,
                "changing memory selection does not silently change the running backend");
            Check(panel.RequestedModelKey == "qwen3-0.6b" && panel.StatusText.Contains("Selection pending"), "model changes remain pending until explicit reload");
            var beforeReload = new TaskCompletionSource<PdaQaReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken reloadToken = default;
            panel.AskOverride = (_, _, token) => { reloadToken = token; return beforeReload.Task; };
            panel.SubmitQuestion("What are the local conditions?");
            panel.ReloadModel();
            Check(reloadToken.IsCancellationRequested && !panel.IsBusy, "explicit reload cancels the previous request");
            Check(panel.RequestedMemoryMode == PdaMemoryMode.GpuVram && panel.RequestedModelKey == "qwen2.5-0.5b", "reload applies the selected model and requested VRAM mode");
            beforeReload.SetResult(new PdaQaReply("STALE MODEL REPLY", "fixture", 1));
            panel._Process(0);
            Check(!panel.TranscriptText.Contains("STALE MODEL REPLY") && panel.StatusText.Contains("no real model loaded"), "reload discards old completion and does not claim fixture GPU readiness");
            memory.Select(0); models.Select(0); panel.ReloadModel();

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
}
