using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class CraftingMonitorMenu : IClickableMenu
{
    private readonly NetworkData network;
    private readonly PatternExecutionService executionService;
    private readonly PatternData? queuePattern;
    private readonly Item? caskPipelineItem;
    private readonly Func<string?> getActionBlockMessage;
    private readonly List<ClickableComponent> amountButtons = new();
    private readonly List<ClickableComponent> cancelButtons = new();
    private readonly List<ClickableComponent> pipelineButtons = new();
    private readonly ClickableComponent? pipelineButton;
    private readonly ClickableComponent? caskPipelineButton;
    private int queueAmount = 1;
    private int jobScrollOffset;
    private int pipelineScrollOffset;
    private bool longJobConfirmationArmed;
    private bool? longJobConfirmationNeeded;
    private int longJobConfirmationCachedAmount;

    public CraftingMonitorMenu(
        NetworkData network,
        PatternExecutionService executionService,
        PatternData? queuePattern,
        Item? caskPipelineItem,
        Func<string?>? getActionBlockMessage = null)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - 980) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - 680) / 2),
            width: 980,
            height: 680,
            showUpperRightCloseButton: true)
    {
        this.network = network;
        this.executionService = executionService;
        this.queuePattern = queuePattern;
        this.caskPipelineItem = caskPipelineItem;
        this.getActionBlockMessage = getActionBlockMessage ?? (() => null);

        var buttonY = this.yPositionOnScreen + this.height - 96;
        var buttonX = this.xPositionOnScreen + 56;
        foreach (var amount in new[] { 1, 5, 10, 25, 100 })
        {
            this.amountButtons.Add(new ClickableComponent(new Rectangle(buttonX, buttonY, 76, 42), amount.ToString(), amount.ToString()));
            buttonX += 86;
        }

        if (this.queuePattern?.Kind == PatternKind.Processing)
            this.pipelineButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - 314, buttonY, 126, 42), "pipeline", ModText.Get("craftingMonitor.button.pipeline"));

        if (this.caskPipelineItem is not null)
        {
            var caskButtonX = this.pipelineButton is not null
                ? this.xPositionOnScreen + this.width - 450
                : this.xPositionOnScreen + this.width - 314;
            this.caskPipelineButton = new ClickableComponent(new Rectangle(caskButtonX, buttonY, 126, 42), "cask", ModText.Get("craftingMonitor.button.cask"));
        }

        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var jobs = this.executionService.GetVisibleJobs(this.network);
        var pipelines = this.executionService.GetVisiblePipelines(this.network);
        this.ClampScrolls(jobs.Count, pipelines.Count);
        var title = ModText.Format("craftingMonitor.title", this.network.Name, jobs.Count, pipelines.Count);
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
            var detail = $"{PatternDisplayNames.Get(job.Pattern)}  {FormatJobState(job.State)}{FormatCpuSlot(job)}{FormatNodeCount(job)}{FormatReservations(job)}  {job.CompletedCount:N0}/{job.RequestedCount:N0}  {job.StatusMessage}";
            b.DrawString(Game1.smallFont, TrimTo(detail, 94), new Vector2(this.xPositionOnScreen + 64, y), color);

            if (job.State is CraftingJobState.Planning or CraftingJobState.MissingItems or CraftingJobState.Reserved or CraftingJobState.Running or CraftingJobState.WaitingForMachine or CraftingJobState.WaitingForOutput)
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
                var line = ModText.Format("craftingMonitor.pipelineLine", status, PatternDisplayNames.Get(pipeline.Pattern), pipeline.Priority, target, pipeline.ItemsPerCycle, pipeline.StatusMessage);
                b.DrawString(Game1.smallFont, TrimTo(line, 58), new Vector2(this.xPositionOnScreen + 64, y), color);

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

        if (this.queuePattern is not null)
        {
            if (this.NeedsLongJobConfirmationForCurrentQueue())
            {
                var warning = this.longJobConfirmationArmed
                    ? ModText.Get("craftingMonitor.longJob.armed")
                    : ModText.Get("craftingMonitor.longJob.warning");
                b.DrawString(Game1.smallFont, warning, new Vector2(this.xPositionOnScreen + 56, this.yPositionOnScreen + this.height - 166), Color.Firebrick);
            }

            var queueText = ModText.Format("craftingMonitor.queueLine", PatternDisplayNames.Get(this.queuePattern), this.queueAmount);
            b.DrawString(Game1.smallFont, queueText, new Vector2(this.xPositionOnScreen + 56, this.yPositionOnScreen + this.height - 134), Game1.textColor);

            foreach (var button in this.amountButtons)
            {
                var amount = int.Parse(button.name);
                var selected = amount == this.queueAmount;
                SVSAPMenuWidgets.DrawButton(b, button, tint: selected ? Color.LightGreen : Color.White);
            }

            if (this.caskPipelineButton is not null)
                this.DrawBottomButton(b, this.caskPipelineButton);

            if (this.pipelineButton is not null)
                this.DrawBottomButton(b, this.pipelineButton);

            var queueButtonBounds = new Rectangle(this.xPositionOnScreen + this.width - 176, this.yPositionOnScreen + this.height - 96, 120, 42);
            SVSAPMenuWidgets.DrawButton(b, new ClickableComponent(queueButtonBounds, "queue", ModText.Get("craftingMonitor.button.queue")));
        }
        else if (this.caskPipelineButton is not null && this.caskPipelineItem is not null)
        {
            var caskText = ModText.Format("craftingMonitor.caskLine", this.caskPipelineItem.DisplayName);
            b.DrawString(Game1.smallFont, TrimTo(caskText, 58), new Vector2(this.xPositionOnScreen + 56, this.yPositionOnScreen + this.height - 134), Game1.textColor);
            this.DrawBottomButton(b, this.caskPipelineButton);
        }

        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        foreach (var button in this.cancelButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (!this.EnsureActionAllowed())
                return;

            if (Guid.TryParse(button.name, out var jobId)
                && this.executionService.TryCancelJob(this.network, jobId, out var cancelMessage))
            {
                Game1.addHUDMessage(new HUDMessage(cancelMessage, HUDMessage.newQuest_type));
                Game1.playSound("trashcan");
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage(ModText.Get("craftingMonitor.cancelFailed"), HUDMessage.error_type));
                Game1.playSound("cancel");
            }
            return;
        }

        foreach (var button in this.pipelineButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (!this.EnsureActionAllowed())
                return;

            var parts = button.name.Split('|');
            if (parts.Length == 2
                && Guid.TryParse(parts[1], out var pipelineId)
                && this.executionService.TryUpdatePipeline(this.network, pipelineId, parts[0], out var pipelineMessage))
            {
                Game1.addHUDMessage(new HUDMessage(pipelineMessage, HUDMessage.newQuest_type));
                Game1.playSound("smallSelect");
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage(ModText.Get("craftingMonitor.pipelineUpdateFailed"), HUDMessage.error_type));
                Game1.playSound("cancel");
            }
            return;
        }

        if (this.queuePattern is null)
        {
            if (this.caskPipelineButton is not null
                && this.caskPipelineItem is not null
                && this.caskPipelineButton.containsPoint(x, y))
            {
                this.ToggleCaskPipeline();
            }
            return;
        }

        foreach (var button in this.amountButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            this.queueAmount = int.Parse(button.name);
            this.ResetLongJobConfirmation();
            Game1.playSound("smallSelect");
            return;
        }

        if (this.pipelineButton is not null && this.pipelineButton.containsPoint(x, y))
        {
            if (!this.EnsureActionAllowed())
                return;

            if (this.executionService.TryTogglePipeline(this.network, this.queuePattern, out var pipelineMessage))
            {
                this.ResetLongJobConfirmation();
                Game1.addHUDMessage(new HUDMessage(pipelineMessage, HUDMessage.newQuest_type));
                Game1.playSound("coin");
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage(pipelineMessage, HUDMessage.error_type));
                Game1.playSound("cancel");
            }
            return;
        }

        if (this.caskPipelineButton is not null
            && this.caskPipelineItem is not null
            && this.caskPipelineButton.containsPoint(x, y))
        {
            this.ToggleCaskPipeline();
            return;
        }

        var queueButtonBounds = new Rectangle(this.xPositionOnScreen + this.width - 176, this.yPositionOnScreen + this.height - 96, 120, 42);
        if (!queueButtonBounds.Contains(x, y))
            return;

        if (!this.EnsureActionAllowed())
            return;

        if (this.NeedsLongJobConfirmationForCurrentQueue(forceRefresh: true) && !this.longJobConfirmationArmed)
        {
            this.longJobConfirmationArmed = true;
            Game1.addHUDMessage(new HUDMessage(ModText.Get("craftingMonitor.longJob.confirmHud"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        if (this.executionService.TryQueuePatternJob(this.network, this.queuePattern, this.queueAmount, out var message))
        {
            this.ResetLongJobConfirmation();
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
            Game1.playSound("coin");
        }
        else
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            Game1.playSound("cancel");
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

        var jobs = this.executionService.GetVisibleJobs(this.network);
        var pipelines = this.executionService.GetVisiblePipelines(this.network);
        this.ClampScrolls(jobs.Count, pipelines.Count);

        var delta = direction > 0 ? -1 : 1;
        if (pipelines.Count > 0 && this.IsMouseInPipelineArea(jobs.Count, pipelines.Count))
            this.pipelineScrollOffset += delta;
        else
            this.jobScrollOffset += delta;

        this.ClampScrolls(jobs.Count, pipelines.Count);
    }

    private void DrawPipelineButton(SpriteBatch b, ProductionPipelineData pipeline, string action, string label, int x, int y, int width, Color tint)
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
        SVSAPMenuWidgets.DrawButton(b, button);
    }

    private bool NeedsLongJobConfirmationForCurrentQueue(bool forceRefresh = false)
    {
        if (this.queuePattern is null)
            return false;

        if (forceRefresh
            || this.longJobConfirmationNeeded is null
            || this.longJobConfirmationCachedAmount != this.queueAmount)
        {
            this.longJobConfirmationNeeded = this.executionService.NeedsLongJobConfirmation(this.network, this.queuePattern, this.queueAmount);
            this.longJobConfirmationCachedAmount = this.queueAmount;
        }

        return this.longJobConfirmationNeeded.Value;
    }

    private void ResetLongJobConfirmation()
    {
        this.longJobConfirmationArmed = false;
        this.longJobConfirmationNeeded = null;
    }

    private void ToggleCaskPipeline()
    {
        if (this.caskPipelineItem is null)
            return;

        if (!this.EnsureActionAllowed())
            return;

        if (this.executionService.TryToggleCaskPipeline(this.network, this.caskPipelineItem, out var caskMessage))
        {
            this.ResetLongJobConfirmation();
            Game1.addHUDMessage(new HUDMessage(caskMessage, HUDMessage.newQuest_type));
            Game1.playSound("coin");
        }
        else
        {
            Game1.addHUDMessage(new HUDMessage(caskMessage, HUDMessage.error_type));
            Game1.playSound("cancel");
        }
    }

    private int GetMaxJobRows(int pipelineCount)
    {
        return pipelineCount > 0
            ? this.queuePattern is null ? 6 : 5
            : this.queuePattern is null ? 10 : 8;
    }

    private int GetMaxPipelineRows()
    {
        return this.queuePattern is null ? 5 : 3;
    }

    private bool IsMouseInPipelineArea(int jobCount, int pipelineCount)
    {
        var maxJobRows = this.GetMaxJobRows(pipelineCount);
        var shownJobs = Math.Min(maxJobRows, Math.Max(0, jobCount - this.jobScrollOffset));
        var pipelineTop = this.yPositionOnScreen + 86 + shownJobs * 46;
        if (jobCount == 0)
            pipelineTop += 38;
        else if (this.jobScrollOffset + shownJobs < jobCount)
            pipelineTop += 32;

        pipelineTop += 16;
        return Game1.getMouseY() >= pipelineTop;
    }

    private bool EnsureActionAllowed()
    {
        var message = this.getActionBlockMessage();
        if (string.IsNullOrWhiteSpace(message))
            return true;

        Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
        Game1.playSound("cancel");
        return false;
    }

    private void ClampScrolls(int jobCount, int pipelineCount)
    {
        var maxJobOffset = Math.Max(0, jobCount - this.GetMaxJobRows(pipelineCount));
        this.jobScrollOffset = Math.Clamp(this.jobScrollOffset, 0, maxJobOffset);

        var maxPipelineOffset = Math.Max(0, pipelineCount - this.GetMaxPipelineRows());
        this.pipelineScrollOffset = Math.Clamp(this.pipelineScrollOffset, 0, maxPipelineOffset);
    }

    private static string TrimTo(string value, int max)
    {
        if (value.Length <= max)
            return value;

        return value.Substring(0, Math.Max(0, max - 3)) + "...";
    }

    private static string FormatCpuSlot(CraftingJob job)
    {
        return job.AssignedCpuEndpointId.HasValue
            ? ModText.Format("craftingMonitor.cpuSlot", job.AssignedCpuEndpointId.Value.ToString("N").Substring(0, 8))
            : string.Empty;
    }

    private static string FormatNodeCount(CraftingJob job)
    {
        return job.NodeCount > 0 ? ModText.Format("craftingMonitor.nodeCount", job.NodeCount) : string.Empty;
    }

    private static string FormatReservations(CraftingJob job)
    {
        var total = job.Reservations.Sum(reservation => Math.Max(0, reservation.Count));
        if (total <= 0)
            return string.Empty;

        var remaining = job.Reservations.Sum(reservation => Math.Max(0, reservation.Count - reservation.ConsumedCount));
        return ModText.Format("craftingMonitor.reservations", remaining, total);
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
