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
    private const int SnapshotRequestTimeoutTicks = 180;
    private const int ActionRequestTimeoutTicks = 300;
    private CraftingMonitorSnapshotResponseMessage snapshot;
    private readonly Func<CraftingMonitorActionRequestMessage, bool> sendRequest;
    private readonly Func<Guid, Guid, bool> requestSnapshot;
    private readonly List<ClickableComponent> amountButtons = new();
    private readonly List<ClickableComponent> cancelButtons = new();
    private readonly List<ClickableComponent> pipelineButtons = new();
    private ClickableComponent refreshButton = null!;
    private ClickableComponent queueButton = null!;
    private ClickableComponent pipelineToggleButton = null!;
    private ClickableComponent caskPipelineButton = null!;
    private int queueAmount = 1;
    private int jobScrollOffset;
    private int pipelineScrollOffset;
    private bool longJobConfirmationArmed;
    private bool requestPending;
    private bool snapshotRequestPending;
    private int snapshotRequestAtTick;
    private int actionRequestAtTick;
    private PatternData? previewQueuePattern;
    private int previewQueueBatches = 1;
    private readonly Guid menuSessionId;
    private long lastAppliedRequestSequence;

    public RemoteCraftingMonitorMenu(
        CraftingMonitorSnapshotResponseMessage snapshot,
        Func<CraftingMonitorActionRequestMessage, bool> sendRequest,
        Func<Guid, Guid, bool> requestSnapshot)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.snapshot = snapshot;
        this.menuSessionId = snapshot.MenuSessionId;
        this.lastAppliedRequestSequence = snapshot.RequestSequence;
        this.sendRequest = sendRequest;
        this.requestSnapshot = requestSnapshot;
        this.BuildLayout();
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private static int GetMenuWidth() => Math.Min(980, Game1.uiViewport.Width - 80);

    private static int GetMenuHeight() => Math.Min(680, Game1.uiViewport.Height - 80);

    private void BuildLayout()
    {
        var buttonY2 = this.yPositionOnScreen + this.height - SVSAPMenuWidgets.Pad - 38;
        var actionBounds = SVSAPMenuWidgets.CalculateRightAlignedButtonRow(
            this.xPositionOnScreen,
            this.width,
            buttonY2,
            buttonCount: 4,
            preferredWidth: 116,
            height: 38,
            gap: 16,
            horizontalMargin: 56);
        var amountX = this.xPositionOnScreen + 56;
        const int amountRowWidth = 5 * 58 + 4 * 6;
        var isTwoRow = amountX + amountRowWidth + 16 > actionBounds[0].X;
        var buttonY1 = isTwoRow ? buttonY2 - 40 : buttonY2;

        this.refreshButton = new ClickableComponent(
            actionBounds[3],
            "refresh",
            ModText.Get("craftingMonitor.button.refresh"));
        this.queueButton = new ClickableComponent(
            actionBounds[2],
            "queue",
            ModText.Get("craftingMonitor.button.queue"));
        this.pipelineToggleButton = new ClickableComponent(
            actionBounds[1],
            "toggle_pipeline",
            ModText.Get("craftingMonitor.button.pipeline"));
        this.caskPipelineButton = new ClickableComponent(
            actionBounds[0],
            "toggle_cask",
            ModText.Get("craftingMonitor.button.cask"));

        this.amountButtons.Clear();
        foreach (var amount in new[] { 1, 5, 10, 25, 100 })
        {
            this.amountButtons.Add(new ClickableComponent(
                new Rectangle(amountX, buttonY1, 58, 38),
                amount.ToString(),
                amount.ToString()));
            amountX += 64;
        }
    }

    public bool MatchesNetwork(Guid networkId)
    {
        return this.snapshot.NetworkId == networkId;
    }

    public bool MatchesSnapshotContext(CraftingMonitorSnapshotResponseMessage candidate)
    {
        return RemoteSnapshotSessionRules.Matches(this.menuSessionId, candidate.MenuSessionId)
            && candidate.NetworkId == this.snapshot.NetworkId
            && candidate.EndpointId == this.snapshot.EndpointId;
    }

    public bool TryApplyRefreshSnapshot(CraftingMonitorSnapshotResponseMessage updated)
    {
        if (!this.MatchesSnapshotContext(updated))
            return false;

        if (!RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, updated.RequestSequence))
            return false;

        this.snapshotRequestPending = false;
        this.lastAppliedRequestSequence = updated.RequestSequence;
        this.ApplySnapshot(updated);
        return true;
    }

    public void MarkSnapshotRequestFailed(long requestSequence)
    {
        if (!RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, requestSequence))
            return;

        this.lastAppliedRequestSequence = requestSequence;
        this.snapshotRequestPending = false;
    }

    public void ApplySnapshot(CraftingMonitorSnapshotResponseMessage updated)
    {
        if (!SameQueuePattern(this.snapshot.QueuePattern, updated.QueuePattern)
            || !string.Equals(this.snapshot.CaskPipelineItemPrototype, updated.CaskPipelineItemPrototype, StringComparison.Ordinal))
        {
            this.longJobConfirmationArmed = false;
        }

        this.snapshot = updated;
        this.snapshotRequestPending = false;
        this.ClampScrolls();
    }

    public bool MarkActionComplete(CraftingMonitorActionResponseMessage response)
    {
        if (response.NetworkId != this.snapshot.NetworkId
            || !RemoteSnapshotSessionRules.Matches(this.menuSessionId, response.MenuSessionId)
            || !RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, response.RequestSequence))
        {
            return false;
        }

        this.lastAppliedRequestSequence = response.RequestSequence;
        this.requestPending = false;
        if (response.Snapshot is not null && response.Snapshot.Success && this.MatchesSnapshotContext(response.Snapshot))
            this.ApplySnapshot(response.Snapshot);

        return true;
    }

    public void ApplyActionResult(CraftingMonitorActionResponseMessage response)
    {
        this.longJobConfirmationArmed = response.RequiresConfirmation && response.PreviewLines.Count == 0;
        if (response.PreviewLines.Count == 0 || response.PreviewPattern is null)
            return;

        this.previewQueuePattern = response.PreviewPattern;
        this.previewQueueBatches = Math.Max(1, response.PreviewBatches);
        Game1.activeClickableMenu = new CraftingConfirmationMenu(
            this,
            PatternDisplayNames.Get(response.PreviewPattern),
            response.PreviewLines,
            this.QueuePreviewedPattern);
        Game1.playSound("smallSelect");
    }

    public override void update(GameTime time)
    {
        base.update(time);
        var tick = Game1.ticks;
        if (this.requestPending
            && RemoteSnapshotSessionRules.HasTimedOut(this.actionRequestAtTick, tick, ActionRequestTimeoutTicks))
        {
            this.requestPending = false;
            this.RequestSnapshot();
        }

        if (this.snapshotRequestPending
            && RemoteSnapshotSessionRules.HasTimedOut(this.snapshotRequestAtTick, tick, SnapshotRequestTimeoutTicks))
        {
            this.snapshotRequestPending = false;
        }
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        this.ClampScrolls();

        var jobs = this.snapshot.Jobs;
        var pipelines = this.snapshot.Pipelines;
        var rowAllocation = this.GetRowAllocation();
        var title = ModText.Format("remoteCraftingMonitor.title", this.snapshot.NetworkName, jobs.Count, pipelines.Count);
        SVSAPMenuWidgets.DrawFittedTitle(
            b,
            title,
            new Rectangle(this.xPositionOnScreen + SVSAPMenuWidgets.Pad + 12, this.yPositionOnScreen + 22, this.width - SVSAPMenuWidgets.Pad * 2 - 104, 46),
            Game1.textColor);
        if (this.requestPending || this.snapshotRequestPending)
            SVSAPMenuWidgets.DrawPixelStatusLight(b, this.xPositionOnScreen + this.width - 96, this.yPositionOnScreen + 40, PixelStatus.Processing);

        var y = this.yPositionOnScreen + 86;
        this.cancelButtons.Clear();
        this.pipelineButtons.Clear();

        var maxJobRows = rowAllocation.JobRows;
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
            var status = string.IsNullOrWhiteSpace(job.StatusMessage) ? string.Empty : $"  {job.StatusMessage}";
            var detail = $"{GetJobDisplayName(job)}  {FormatJobState(job.State)}{FormatCpuSlot(job)}{FormatNodeCount(job)}{FormatReservations(job)}  {job.CompletedCount:N0}/{job.RequestedCount:N0}{status}";
            SVSAPMenuWidgets.DrawFittedLine(
                b,
                detail,
                new Rectangle(this.xPositionOnScreen + 64, y - 2, this.width - 248, 34),
                color,
                horizontalPadding: 0);

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
            SVSAPMenuWidgets.DrawFittedLine(
                b,
                ModText.Get("craftingMonitor.noJobs"),
                new Rectangle(this.xPositionOnScreen + 64, y, this.width - 128, 30),
                Color.DarkSlateGray,
                horizontalPadding: 0);
            y += 38;
        }
        else if (this.jobScrollOffset + maxRows < jobs.Count)
        {
            SVSAPMenuWidgets.DrawFittedLine(
                b,
                ModText.Format("craftingMonitor.moreJobs", jobs.Count - this.jobScrollOffset - maxRows),
                new Rectangle(this.xPositionOnScreen + 64, y, this.width - 128, 30),
                Color.DarkSlateGray,
                horizontalPadding: 0);
            y += 32;
        }

        if (pipelines.Count > 0)
        {
            y += 16;
            var shown = Math.Min(rowAllocation.PipelineRows, pipelines.Count - this.pipelineScrollOffset);
            for (var i = 0; i < shown; i++)
            {
                var pipeline = pipelines[this.pipelineScrollOffset + i];
                var color = pipeline.Enabled ? Game1.textColor : Color.DarkSlateGray;
                var status = pipeline.Enabled ? ModText.Get("craftingMonitor.state.on") : ModText.Get("craftingMonitor.state.off");
                var target = pipeline.TargetKeep > 0 ? pipeline.TargetKeep.ToString("N0") : ModText.Get("craftingMonitor.unlimited");
                var mode = pipeline.Mode == ProductionPipelineMode.CaskAging ? ModText.Get("craftingMonitor.mode.cask") : ModText.Get("craftingMonitor.mode.processing");
                var line = ModText.Format("remoteCraftingMonitor.pipelineLine", status, mode, GetPipelineDisplayName(pipeline), pipeline.Priority, target, pipeline.ItemsPerCycle, FormatPipelineStatus(pipeline));
                SVSAPMenuWidgets.DrawFittedLine(
                    b,
                    line,
                    new Rectangle(this.xPositionOnScreen + 64, y - 2, Math.Max(80, this.width - 460), 34),
                    color,
                    horizontalPadding: 0);

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
            var amountTop = this.amountButtons.Count > 0
                ? this.amountButtons[0].bounds.Y
                : this.yPositionOnScreen + this.height - SVSAPMenuWidgets.Pad - 38;
            if (this.longJobConfirmationArmed)
            {
                SVSAPMenuWidgets.DrawFittedLine(
                    b,
                    ModText.Get("craftingMonitor.longJob.armed"),
                    new Rectangle(this.xPositionOnScreen + 56, amountTop - 56, this.width - 112, 24),
                    Color.Firebrick,
                    horizontalPadding: 0);
            }

            var queueText = ModText.Format("craftingMonitor.queueLine", PatternDisplayNames.Get(this.snapshot.QueuePattern), this.queueAmount);
            SVSAPMenuWidgets.DrawFittedLine(
                b,
                queueText,
                new Rectangle(this.xPositionOnScreen + 56, amountTop - 28, this.width - 112, 24),
                Game1.textColor,
                horizontalPadding: 0);

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
            var caskText = ModText.Format("craftingMonitor.caskLine", this.GetCaskPipelineItemDisplayName());
            SVSAPMenuWidgets.DrawFittedLine(b, caskText, new Rectangle(this.xPositionOnScreen + 56, this.caskPipelineButton.bounds.Y - 28, this.width - 112, 24), Game1.textColor, horizontalPadding: 0);
            this.DrawBottomButton(b, this.caskPipelineButton);
        }

        this.DrawBottomButton(b, this.refreshButton);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            base.receiveLeftClick(x, y, playSound);
            return;
        }

        base.receiveLeftClick(x, y, playSound);

        if (this.refreshButton.containsPoint(x, y))
        {
            Game1.playSound(this.RequestSnapshot() ? "smallSelect" : "cancel");
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
                this.SendMutation(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.ToggleCaskPipeline,
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
                });
                return;
            }

            if (this.HasProcessingQueuePattern() && this.pipelineToggleButton.containsPoint(x, y))
            {
                this.longJobConfirmationArmed = false;
                this.SendMutation(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.TogglePipeline,
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
                });
                return;
            }

            if (this.queueButton.containsPoint(x, y))
            {
                this.SendMutation(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.PreviewQueueJob,
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype,
                    Batches = Math.Max(1, this.queueAmount)
                });
                return;
            }
        }
        else if (this.HasCaskPipelineItem() && this.caskPipelineButton.containsPoint(x, y))
        {
            this.longJobConfirmationArmed = false;
            this.SendMutation(new CraftingMonitorActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = this.snapshot.NetworkId,
                EndpointId = this.snapshot.EndpointId,
                Action = CraftingMonitorActionKind.ToggleCaskPipeline,
                CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
            });
            return;
        }

        foreach (var button in this.cancelButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (Guid.TryParse(button.name, out var jobId))
            {
                this.SendMutation(new CraftingMonitorActionRequestMessage
                {
                    TransactionId = Guid.NewGuid(),
                    NetworkId = this.snapshot.NetworkId,
                    EndpointId = this.snapshot.EndpointId,
                    Action = CraftingMonitorActionKind.CancelJob,
                    JobId = jobId,
                    QueuePattern = this.snapshot.QueuePattern,
                    CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype
                });
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
                this.SendMutation(new CraftingMonitorActionRequestMessage
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
            }
            return;
        }
    }

    private void QueuePreviewedPattern()
    {
        if (this.previewQueuePattern is null)
            return;

        var pattern = this.previewQueuePattern;
        var batches = Math.Max(1, this.previewQueueBatches);
        this.previewQueuePattern = null;
        this.previewQueueBatches = 1;
        this.longJobConfirmationArmed = false;

        this.SendMutation(new CraftingMonitorActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            NetworkId = this.snapshot.NetworkId,
            EndpointId = this.snapshot.EndpointId,
            Action = CraftingMonitorActionKind.QueueJob,
            QueuePattern = pattern,
            CaskPipelineItemPrototype = this.snapshot.CaskPipelineItemPrototype,
            Batches = batches,
            ConfirmLongJob = true
        });
    }

    private bool SendMutation(CraftingMonitorActionRequestMessage request)
    {
        if (this.requestPending || this.snapshotRequestPending)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteMonitor.requestPending"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return false;
        }

        request.MenuSessionId = this.menuSessionId;
        var sent = this.sendRequest(request);
        this.requestPending = sent;
        if (sent)
            this.actionRequestAtTick = Game1.ticks;
        Game1.playSound(sent ? "smallSelect" : "cancel");
        return sent;
    }

    private bool RequestSnapshot()
    {
        if (this.requestPending)
            return false;

        var tick = Game1.ticks;
        if (this.snapshotRequestPending
            && !RemoteSnapshotSessionRules.HasTimedOut(this.snapshotRequestAtTick, tick, SnapshotRequestTimeoutTicks))
        {
            return false;
        }

        this.snapshotRequestAtTick = tick;
        this.snapshotRequestPending = this.requestSnapshot(this.snapshot.NetworkId, this.snapshot.EndpointId);
        return this.snapshotRequestPending;
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            this.exitThisMenu();
            return;
        }

        base.receiveKeyPress(key);
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

    private static string GetJobDisplayName(RemoteCraftingJobMessage job)
    {
        return job.Pattern is not null
            ? PatternDisplayNames.Get(job.Pattern)
            : job.JobId.ToString("N").Substring(0, 8);
    }

    private static string GetPipelineDisplayName(RemoteProductionPipelineMessage pipeline)
    {
        return pipeline.Pattern is not null
            ? PatternDisplayNames.Get(pipeline.Pattern)
            : pipeline.PipelineId.ToString("N").Substring(0, 8);
    }

    private string GetCaskPipelineItemDisplayName()
    {
        var item = SVSAPMenuWidgets.CreateIconItem(string.Empty, this.snapshot.CaskPipelineItemPrototype);
        return item?.DisplayName ?? ModText.Get("common.none");
    }

    private static string FormatPipelineStatus(RemoteProductionPipelineMessage pipeline)
    {
        if (!pipeline.Enabled)
            return ModText.Get("pipeline.status.disabled");

        if (pipeline.TargetKeep > 0)
            return ModText.Format("pipeline.status.targetKeep", pipeline.TargetKeep);

        return ModText.Get("pipeline.status.enabled");
    }

    private static bool SameQueuePattern(PatternData? left, PatternData? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return left.Kind == right.Kind
            && string.Equals(left.DisplayNameKey, right.DisplayNameKey, StringComparison.Ordinal)
            && left.DisplayNameArguments.SequenceEqual(right.DisplayNameArguments, StringComparer.Ordinal)
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

    private (int JobRows, int PipelineRows) GetRowAllocation()
    {
        var pipelineCount = this.snapshot.Pipelines.Count;
        var preferredJobRows = this.HasBottomControls()
            ? pipelineCount > 0 ? 5 : 8
            : pipelineCount > 0 ? 7 : 10;
        var preferredPipelineRows = this.HasBottomControls() ? 3 : 5;
        var contentTop = this.yPositionOnScreen + 86;
        var contentHeight = Math.Max(0, this.GetContentBottom() - contentTop);
        return SVSAPMenuWidgets.CalculateMonitorRowAllocation(
            contentHeight,
            this.snapshot.Jobs.Count,
            pipelineCount,
            preferredJobRows,
            preferredPipelineRows);
    }

    private int GetContentBottom()
    {
        var contentBottom = this.refreshButton.bounds.Y - 12;
        if (this.snapshot.QueuePattern is not null)
        {
            var amountTop = this.amountButtons.Count > 0
                ? this.amountButtons[0].bounds.Y
                : this.yPositionOnScreen + this.height - SVSAPMenuWidgets.Pad - 38;
            contentBottom = Math.Min(contentBottom, amountTop - 62);
        }
        else if (this.HasCaskPipelineItem())
        {
            contentBottom = Math.Min(contentBottom, this.caskPipelineButton.bounds.Y - 32);
        }

        return contentBottom;
    }

    private bool IsMouseInPipelineArea()
    {
        var maxJobRows = this.GetRowAllocation().JobRows;
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
        var rowAllocation = this.GetRowAllocation();
        var maxJobOffset = Math.Max(0, this.snapshot.Jobs.Count - rowAllocation.JobRows);
        this.jobScrollOffset = Math.Clamp(this.jobScrollOffset, 0, maxJobOffset);

        var maxPipelineOffset = Math.Max(0, this.snapshot.Pipelines.Count - rowAllocation.PipelineRows);
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
