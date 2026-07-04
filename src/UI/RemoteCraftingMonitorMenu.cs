using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemoteCraftingMonitorMenu : IClickableMenu
{
    private CraftingMonitorSnapshotResponseMessage snapshot;
    private readonly Action<CraftingMonitorActionRequestMessage> sendRequest;
    private readonly Action<Guid, Guid> requestSnapshot;
    private readonly List<ClickableComponent> amountButtons = new();
    private readonly List<ClickableComponent> cancelButtons = new();
    private readonly List<ClickableComponent> pipelineButtons = new();
    private readonly ClickableComponent refreshButton;
    private readonly ClickableComponent queueButton;
    private readonly ClickableComponent pipelineToggleButton;
    private readonly ClickableComponent caskPipelineButton;
    private int queueAmount = 1;
    private int jobScrollOffset;
    private int pipelineScrollOffset;
    private bool longJobConfirmationArmed;

    public RemoteCraftingMonitorMenu(
        CraftingMonitorSnapshotResponseMessage snapshot,
        Action<CraftingMonitorActionRequestMessage> sendRequest,
        Action<Guid, Guid> requestSnapshot)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - 980) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - 680) / 2),
            width: 980,
            height: 680,
            showUpperRightCloseButton: true)
    {
        this.snapshot = snapshot;
        this.sendRequest = sendRequest;
        this.requestSnapshot = requestSnapshot;
        this.refreshButton = new ClickableComponent(
            new Rectangle(this.xPositionOnScreen + this.width - 172, this.yPositionOnScreen + this.height - 76, 116, 38),
            "refresh",
            ModText.Get("craftingMonitor.button.refresh"));
        this.queueButton = new ClickableComponent(
            new Rectangle(this.xPositionOnScreen + this.width - 304, this.yPositionOnScreen + this.height - 76, 116, 38),
            "queue",
            ModText.Get("craftingMonitor.button.queue"));
        this.pipelineToggleButton = new ClickableComponent(
            new Rectangle(this.xPositionOnScreen + this.width - 436, this.yPositionOnScreen + this.height - 76, 116, 38),
            "toggle_pipeline",
            ModText.Get("craftingMonitor.button.pipeline"));
        this.caskPipelineButton = new ClickableComponent(
            new Rectangle(this.xPositionOnScreen + this.width - 568, this.yPositionOnScreen + this.height - 76, 116, 38),
            "toggle_cask",
            ModText.Get("craftingMonitor.button.cask"));

        var amountX = this.xPositionOnScreen + 56;
        foreach (var amount in new[] { 1, 5, 10, 25, 100 })
        {
            this.amountButtons.Add(new ClickableComponent(
                new Rectangle(amountX, this.yPositionOnScreen + this.height - 76, 58, 38),
                amount.ToString(),
                amount.ToString()));
            amountX += 64;
        }

        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    public bool MatchesNetwork(Guid networkId)
    {
        return this.snapshot.NetworkId == networkId;
    }

    public void ApplySnapshot(CraftingMonitorSnapshotResponseMessage updated)
    {
        if (!SameQueuePattern(this.snapshot.QueuePattern, updated.QueuePattern)
            || !string.Equals(this.snapshot.CaskPipelineItemPrototype, updated.CaskPipelineItemPrototype, StringComparison.Ordinal))
        {
            this.longJobConfirmationArmed = false;
        }

        this.snapshot = updated;
        this.ClampScrolls();
    }

    public void ApplyActionResult(CraftingMonitorActionResponseMessage response)
    {
        this.longJobConfirmationArmed = response.RequiresConfirmation;
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        this.ClampScrolls();

        var jobs = this.snapshot.Jobs;
        var pipelines = this.snapshot.Pipelines;
        var title = ModText.Format("remoteCraftingMonitor.title", this.snapshot.NetworkName, jobs.Count, pipelines.Count);
        b.DrawString(Game1.dialogueFont, title, new Vector2(this.xPositionOnScreen + 48, this.yPositionOnScreen + 32), Game1.textColor);

        var y = this.yPositionOnScreen + 86;
        this.cancelButtons.Clear();
        this.pipelineButtons.Clear();

        var maxJobRows = this.GetMaxJobRows(pipelines.Count);
        var maxRows = Math.Min(maxJobRows, jobs.Count - this.jobScrollOffset);
        for (var i = 0; i < maxRows; i++)
        {
            var job = jobs[this.jobScrollOffset + i];
            var color = job.State switch
            {
                CraftingJobState.Failed => Color.Firebrick,
                CraftingJobState.Completed => Color.DarkGreen,
                CraftingJobState.Cancelled => Color.DarkSlateGray,
                _ => Game1.textColor
            };
            var detail = $"{job.DisplayName}  {FormatJobState(job.State)}{FormatCpuSlot(job)}{FormatNodeCount(job)}{FormatReservations(job)}  {job.CompletedCount:N0}/{job.RequestedCount:N0}  {job.StatusMessage}";
            b.DrawString(Game1.smallFont, TrimTo(detail, 94), new Vector2(this.xPositionOnScreen + 64, y), color);

            if (job.CanCancel)
            {
                var button = new ClickableComponent(
                    new Rectangle(this.xPositionOnScreen + this.width - 164, y - 4, 92, 34),
                    job.JobId.ToString("N"),
                    ModText.Get("craftingMonitor.button.cancel"));
                this.cancelButtons.Add(button);
                SVSAPMenuWidgets.DrawButton(b, button);
            }

            y += 46;
        }

        if (jobs.Count == 0)
        {
            b.DrawString(Game1.smallFont, ModText.Get("craftingMonitor.noJobs"), new Vector2(this.xPositionOnScreen + 64, y), Color.DarkSlateGray);
            y += 38;
        }
        else if (this.jobScrollOffset + maxRows < jobs.Count)
        {
            b.DrawString(Game1.smallFont, ModText.Format("craftingMonitor.moreJobs", jobs.Count - this.jobScrollOffset - maxRows), new Vector2(this.xPositionOnScreen + 64, y), Color.DarkSlateGray);
            y += 32;
        }

        if (pipelines.Count > 0)
        {
            y += 16;
            var shown = Math.Min(this.GetMaxPipelineRows(), pipelines.Count - this.pipelineScrollOffset);
            for (var i = 0; i < shown; i++)
            {
                var pipeline = pipelines[this.pipelineScrollOffset + i];
                var color = pipeline.Enabled ? Game1.textColor : Color.DarkSlateGray;
                var status = pipeline.Enabled ? ModText.Get("craftingMonitor.state.on") : ModText.Get("craftingMonitor.state.off");
                var target = pipeline.TargetKeep > 0 ? pipeline.TargetKeep.ToString("N0") : ModText.Get("craftingMonitor.unlimited");
                var mode = pipeline.Mode == ProductionPipelineMode.CaskAging ? ModText.Get("craftingMonitor.mode.cask") : ModText.Get("craftingMonitor.mode.processing");
                var line = ModText.Format("remoteCraftingMonitor.pipelineLine", status, mode, pipeline.DisplayName, pipeline.Priority, target, pipeline.ItemsPerCycle, pipeline.StatusMessage);
                b.DrawString(Game1.smallFont, TrimTo(line, 56), new Vector2(this.xPositionOnScreen + 64, y), color);

                var buttonX = this.xPositionOnScreen + this.width - 380;
                this.DrawPipelineButton(b, pipeline, PatternExecutionService.PipelineActionToggle, status, buttonX, y - 3, 52, pipeline.Enabled ? Color.LightGreen : Color.White);
                buttonX += 58;
                this.DrawPipelineButton(b, pipeline, PatternExecutionService.PipelineActionPriorityUp, ModText.Get("craftingMonitor.button.priorityUp"), buttonX, y - 3, 38, Color.White);
                buttonX += 44;
                this.DrawPipelineButton(b, pipeline, PatternExecutionService.PipelineActionPriorityDown, ModText.Get("craftingMonitor.button.priorityDown"), buttonX, y - 3, 38, Color.White);
                buttonX += 44;
                this.DrawPipelineButton(b, pipeline, PatternExecutionService.PipelineActionTargetUp, ModText.Get("craftingMonitor.button.targetUp"), buttonX, y - 3, 38, Color.White);
                buttonX += 44;
                this.DrawPipelineButton(b, pipeline, PatternExecutionService.PipelineActionTargetDown, ModText.Get("craftingMonitor.button.targetDown"), buttonX, y - 3, 38, Color.White);
                buttonX += 44;
                this.DrawPipelineButton(b, pipeline, PatternExecutionService.PipelineActionCycleUp, ModText.Get("craftingMonitor.button.cycleUp"), buttonX, y - 3, 38, Color.White);
                buttonX += 44;
                this.DrawPipelineButton(b, pipeline, PatternExecutionService.PipelineActionCycleDown, ModText.Get("craftingMonitor.button.cycleDown"), buttonX, y - 3, 38, Color.White);

                y += 42;
            }
        }

        if (this.snapshot.QueuePattern is not null)
        {
            if (this.longJobConfirmationArmed)
            {
                b.DrawString(
                    Game1.smallFont,
                    ModText.Get("craftingMonitor.longJob.armed"),
                    new Vector2(this.xPositionOnScreen + 56, this.yPositionOnScreen + this.height - 138),
                    Color.Firebrick);
            }

            var queueText = ModText.Format("craftingMonitor.queueLine", PatternDisplayNames.Get(this.snapshot.QueuePattern), this.queueAmount);
            b.DrawString(Game1.smallFont, TrimTo(queueText, 70), new Vector2(this.xPositionOnScreen + 56, this.yPositionOnScreen + this.height - 112), Game1.textColor);

            foreach (var button in this.amountButtons)
            {
                var selected = int.TryParse(button.name, out var amount) && amount == this.queueAmount;
                this.DrawBottomButton(b, button, selected ? Color.LightGreen : Color.White);
            }

            if (this.HasCaskPipelineItem())
                this.DrawBottomButton(b, this.caskPipelineButton);

            if (this.HasProcessingQueuePattern())
                this.DrawBottomButton(b, this.pipelineToggleButton);

            this.DrawBottomButton(b, this.queueButton);
        }
        else if (this.HasCaskPipelineItem())
        {
            var caskText = ModText.Format("craftingMonitor.caskLine", this.snapshot.CaskPipelineItemDisplayName);
            b.DrawString(Game1.smallFont, TrimTo(caskText, 70), new Vector2(this.xPositionOnScreen + 56, this.yPositionOnScreen + this.height - 112), Game1.textColor);
            this.DrawBottomButton(b, this.caskPipelineButton);
        }

        this.DrawBottomButton(b, this.refreshButton);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        if (this.refreshButton.containsPoint(x, y))
        {
            this.requestSnapshot(this.snapshot.NetworkId, this.snapshot.EndpointId);
            Game1.playSound("smallSelect");
            return;
        }

        if (this.snapshot.QueuePattern is not null)
        {
            foreach (var button in this.amountButtons)
            {
                if (!button.containsPoint(x, y))
                    continue;

                this.queueAmount = int.Parse(button.name);
                this.longJobConfirmationArmed = false;
                Game1.playSound("smallSelect");
                return;
            }

            if (this.HasCaskPipelineItem() && this.caskPipelineButton.containsPoint(x, y))
            {
                this.longJobConfirmationArmed = false;
                this.sendRequest(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.ToggleCaskPipeline,
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
                });
                Game1.playSound("smallSelect");
                return;
            }

            if (this.HasProcessingQueuePattern() && this.pipelineToggleButton.containsPoint(x, y))
            {
                this.longJobConfirmationArmed = false;
                this.sendRequest(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.TogglePipeline,
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
                });
                Game1.playSound("smallSelect");
                return;
            }

            if (this.queueButton.containsPoint(x, y))
            {
                this.sendRequest(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.QueueJob,
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype,
                    Batches = Math.Max(1, this.queueAmount),
                    ConfirmLongJob = this.longJobConfirmationArmed
                });
                Game1.playSound("smallSelect");
                return;
            }
        }
        else if (this.HasCaskPipelineItem() && this.caskPipelineButton.containsPoint(x, y))
        {
            this.longJobConfirmationArmed = false;
            this.sendRequest(new CraftingMonitorActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = this.snapshot.NetworkId,
                EndpointId = this.snapshot.EndpointId,
                Action = CraftingMonitorActionKind.ToggleCaskPipeline,
                CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
            });
            Game1.playSound("smallSelect");
            return;
        }

        foreach (var button in this.cancelButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (Guid.TryParse(button.name, out var jobId))
            {
                this.sendRequest(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.CancelJob,
                    JobId = jobId,
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
                });
                Game1.playSound("smallSelect");
            }
            return;
        }

        foreach (var button in this.pipelineButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            var parts = button.name.Split('|');
            if (parts.Length == 2 && Guid.TryParse(parts[1], out var pipelineId))
            {
                this.sendRequest(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.UpdatePipeline,
                    PipelineId = pipelineId,
                    PipelineAction = parts[0],
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
                });
                Game1.playSound("smallSelect");
            }
            return;
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        base.receiveKeyPress(key);
        if (key == Keys.Escape)
            this.exitThisMenu();
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        this.ClampScrolls();

        var delta = direction > 0 ? -1 : 1;
        if (this.snapshot.Pipelines.Count > 0 && this.IsMouseInPipelineArea())
            this.pipelineScrollOffset += delta;
        else
            this.jobScrollOffset += delta;

        this.ClampScrolls();
    }

    private bool HasProcessingQueuePattern()
    {
        return this.snapshot.QueuePattern?.Kind == PatternKind.Processing;
    }

    private bool HasCaskPipelineItem()
    {
        return !string.IsNullOrWhiteSpace(this.snapshot.CaskPipelineItemPrototype);
    }

    private bool HasBottomControls()
    {
        return this.snapshot.QueuePattern is not null || this.HasCaskPipelineItem();
    }

    private static bool SameQueuePattern(PatternData? left, PatternData? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return left.Kind == right.Kind
            && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
            && string.Equals(left.MachineQualifiedItemId, right.MachineQualifiedItemId, StringComparison.Ordinal);
    }

    private void DrawPipelineButton(SpriteBatch b, RemoteProductionPipelineMessage pipeline, string action, string label, int x, int y, int width, Color tint)
    {
        var button = new ClickableComponent(
            new Rectangle(x, y, width, 32),
            action + "|" + pipeline.PipelineId.ToString("N"),
            label);
        this.pipelineButtons.Add(button);
        SVSAPMenuWidgets.DrawButton(b, button, label, tint);
    }

    private void DrawBottomButton(SpriteBatch b, ClickableComponent button)
    {
        this.DrawBottomButton(b, button, Color.White);
    }

    private void DrawBottomButton(SpriteBatch b, ClickableComponent button, Color tint)
    {
        SVSAPMenuWidgets.DrawButton(b, button, tint: tint);
    }

    private int GetMaxJobRows(int pipelineCount)
    {
        if (this.HasBottomControls())
            return pipelineCount > 0 ? 5 : 8;

        return pipelineCount > 0 ? 7 : 10;
    }

    private int GetMaxPipelineRows()
    {
        return this.HasBottomControls() ? 3 : 5;
    }

    private bool IsMouseInPipelineArea()
    {
        var maxJobRows = this.GetMaxJobRows(this.snapshot.Pipelines.Count);
        var shownJobs = Math.Min(maxJobRows, Math.Max(0, this.snapshot.Jobs.Count - this.jobScrollOffset));
        var pipelineTop = this.yPositionOnScreen + 86 + shownJobs * 46;
        if (this.snapshot.Jobs.Count == 0)
            pipelineTop += 38;
        else if (this.jobScrollOffset + shownJobs < this.snapshot.Jobs.Count)
            pipelineTop += 32;

        pipelineTop += 16;
        return Game1.getMouseY() >= pipelineTop;
    }

    private void ClampScrolls()
    {
        var maxJobOffset = Math.Max(0, this.snapshot.Jobs.Count - this.GetMaxJobRows(this.snapshot.Pipelines.Count));
        this.jobScrollOffset = Math.Clamp(this.jobScrollOffset, 0, maxJobOffset);

        var maxPipelineOffset = Math.Max(0, this.snapshot.Pipelines.Count - this.GetMaxPipelineRows());
        this.pipelineScrollOffset = Math.Clamp(this.pipelineScrollOffset, 0, maxPipelineOffset);
    }

    private static string TrimTo(string value, int max)
    {
        if (value.Length <= max)
            return value;

        return value.Substring(0, Math.Max(0, max - 3)) + "...";
    }

    private static string FormatCpuSlot(RemoteCraftingJobMessage job)
    {
        return string.IsNullOrWhiteSpace(job.CpuSlotLabel)
            ? string.Empty
            : ModText.Format("craftingMonitor.cpuSlot", job.CpuSlotLabel);
    }

    private static string FormatNodeCount(RemoteCraftingJobMessage job)
    {
        return job.NodeCount > 0 ? ModText.Format("craftingMonitor.nodeCount", job.NodeCount) : string.Empty;
    }

    private static string FormatReservations(RemoteCraftingJobMessage job)
    {
        return job.ReservedCount > 0
            ? ModText.Format("craftingMonitor.reservations", job.RemainingReservedCount, job.ReservedCount)
            : string.Empty;
    }

    private static string FormatJobState(CraftingJobState state)
    {
        return state switch
        {
            CraftingJobState.Planning => ModText.Get("craftingMonitor.jobState.planning"),
            CraftingJobState.MissingItems => ModText.Get("craftingMonitor.jobState.missing"),
            CraftingJobState.Reserved => ModText.Get("craftingMonitor.jobState.reserved"),
            CraftingJobState.Running => ModText.Get("craftingMonitor.jobState.running"),
            CraftingJobState.WaitingForMachine => ModText.Get("craftingMonitor.jobState.waitingMachine"),
            CraftingJobState.WaitingForOutput => ModText.Get("craftingMonitor.jobState.waitingOutput"),
            CraftingJobState.Completed => ModText.Get("craftingMonitor.jobState.completed"),
            CraftingJobState.Cancelled => ModText.Get("craftingMonitor.jobState.cancelled"),
            CraftingJobState.Failed => ModText.Get("craftingMonitor.jobState.failed"),
            _ => state.ToString()
        };
    }
}
