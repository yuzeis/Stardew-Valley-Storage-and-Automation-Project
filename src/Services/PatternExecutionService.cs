using System.Text.Json;
using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Models;
using SVSAP.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Inventories;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class PatternExecutionService
{
    public const string PipelineActionToggle = "toggle";
    public const string PipelineActionPriorityUp = "priority_up";
    public const string PipelineActionPriorityDown = "priority_down";
    public const string PipelineActionTargetUp = "target_up";
    public const string PipelineActionTargetDown = "target_down";
    public const string PipelineActionCycleUp = "cycle_up";
    public const string PipelineActionCycleDown = "cycle_down";

    private const uint TickInterval = 120;
    private const int PipelineTargetStep = 25;
    private const int PipelineMaxTargetKeep = 999999;
    private const int PipelineMaxItemsPerCycle = 64;
    private const string CaskMachineQualifiedItemId = "(BC)163";

    private static readonly HashSet<string> CaskAgeableQualifiedItemIds = new(StringComparer.Ordinal)
    {
        "(O)303",
        "(O)346",
        "(O)348",
        "(O)424",
        "(O)426",
        "(O)459"
    };

    private static readonly HashSet<string> PreservedParentOutputQualifiedItemIds = new(StringComparer.Ordinal)
    {
        "(O)342",
        "(O)344",
        "(O)348",
        "(O)350",
        "(O)DriedFruit",
        "(O)DriedMushrooms",
        "(O)Raisins",
        "(O)SmokedFish"
    };

    private static readonly Vector2[] AdjacentOffsets =
    {
        new Vector2(0, -1),
        new Vector2(1, 0),
        new Vector2(0, 1),
        new Vector2(-1, 0)
    };

    private readonly NetworkRepository repository;
    private readonly InventoryTransactionService transactionService;
    private readonly Func<ModConfig> getConfig;
    private readonly TickOperationBudget operationBudget;
    private readonly IMonitor monitor;

    public PatternExecutionService(
        NetworkRepository repository,
        InventoryTransactionService transactionService,
        Func<ModConfig> getConfig,
        TickOperationBudget operationBudget,
        IMonitor monitor)
    {
        this.repository = repository;
        this.transactionService = transactionService;
        this.getConfig = getConfig;
        this.operationBudget = operationBudget;
        this.monitor = monitor;
    }

    public bool TryHandleCraftingMonitorAction(SObject monitorObject, Func<string?>? getActionBlockMessage = null)
    {
        if (monitorObject.QualifiedItemId != "(BC)" + ModItemCatalog.CraftingMonitor)
            return false;

        if (!Guid.TryParse(monitorObject.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId)
            || !this.repository.TryGetNetwork(networkId, out var network))
        {
            Game1.addHUDMessage(new HUDMessage("请先把这个合成监视器连接到网络。", HUDMessage.error_type));
            return true;
        }

        PatternData? queuePattern = null;
        var held = Game1.player.CurrentItem;
        if (held is not null && PatternCodec.IsPatternItem(held) && !PatternCodec.TryRead(held, out queuePattern))
        {
            Game1.addHUDMessage(new HUDMessage("这个样板物品没有编码数据。", HUDMessage.error_type));
            return true;
        }

        if (held is not null && queuePattern is null)
        {
            queuePattern = this.GetStoredPatterns(network)
                .FirstOrDefault(pattern => OutputMatchesPattern(pattern, held));
        }

        var caskPipelineItem = held is not null && this.CanToggleCaskPipeline(held)
            ? held.getOne()
            : null;

        Game1.activeClickableMenu = new CraftingMonitorMenu(network, this, queuePattern, caskPipelineItem, getActionBlockMessage);
        return true;
    }

    public List<CraftingJob> GetVisibleJobs(NetworkData network)
    {
        return network.Jobs
            .OrderBy(job => IsOpenState(job.State) ? 0 : 1)
            .ThenByDescending(job => job.CreatedTick)
            .Take(30)
            .ToList();
    }

    public List<ProductionPipelineData> GetVisiblePipelines(NetworkData network)
    {
        return network.ProductionPipelines.Values
            .OrderBy(pipeline => pipeline.Enabled ? 0 : 1)
            .ThenBy(pipeline => pipeline.Priority)
            .ThenBy(pipeline => pipeline.PipelineId)
            .Take(10)
            .ToList();
    }

    public bool TryCancelJob(NetworkData network, Guid jobId, out string message)
    {
        var job = network.Jobs.FirstOrDefault(candidate => candidate.JobId == jobId);
        if (job is null)
        {
            message = "CPU 作业已不存在。";
            this.LogGameplay($"action=cpu_cancel result=fail network={ShortId(network.NetworkId)} job={ShortId(jobId)} reason={Quote(message)}");
            return false;
        }

        if (job.State is CraftingJobState.Completed or CraftingJobState.Cancelled)
        {
            message = "CPU 作业已经结束。";
            this.LogGameplay($"action=cpu_cancel result=fail network={ShortId(network.NetworkId)} job={ShortId(jobId)} state={job.State} reason={Quote(message)}");
            return false;
        }

        job.State = CraftingJobState.Cancelled;
        job.AssignedCpuEndpointId = null;
        job.StatusMessage = job.WaitingMachineLocationName.Length > 0
            ? "已取消；机器内物品会保留在原处。"
            : "已取消。";
        this.ReleaseUnconsumedReservations(job);
        this.repository.Save();

        message = $"已取消 CPU 作业：{job.Pattern.DisplayName}。";
        this.LogGameplay($"action=cpu_cancel result=success network={ShortId(network.NetworkId)} job={ShortId(job.JobId)} pattern={Quote(job.Pattern.DisplayName)} state={job.State} remainingReservations={job.Reservations.Sum(reservation => Math.Max(0, reservation.Count - reservation.ConsumedCount)):N0}");
        return true;
    }

    public bool TryQueuePatternJob(NetworkData network, PatternData pattern, int batches, out string message)
    {
        if (!this.getConfig().EnableAutocrafting)
        {
            message = "自动合成已禁用。";
            this.LogGameplay($"action=cpu_queue result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} batches={batches:N0} reason={Quote(message)}");
            return false;
        }

        if (pattern.Kind == PatternKind.Processing && !this.getConfig().EnableProcessingPatterns)
        {
            message = "加工样板已禁用。";
            this.LogGameplay($"action=cpu_queue result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} batches={batches:N0} reason={Quote(message)}");
            return false;
        }

        var targetOutputId = pattern.Outputs.FirstOrDefault(output => !string.IsNullOrWhiteSpace(output.QualifiedItemId))?.QualifiedItemId;
        if (string.IsNullOrWhiteSpace(targetOutputId))
        {
            message = "样板没有具体产物。";
            this.LogGameplay($"action=cpu_queue result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} batches={batches:N0} reason={Quote(message)}");
            return false;
        }

        if (pattern.MachineQualifiedItemId == CaskMachineQualifiedItemId)
        {
            message = "陈酿使用生产流水线，不使用 CPU 作业。";
            this.LogGameplay($"action=cpu_queue result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} batches={batches:N0} reason={Quote(message)}");
            return false;
        }

        var matrixNodeLimit = this.GetMatrixNodeLimit(network);
        if (matrixNodeLimit <= 0)
        {
            message = "这个网络没有连接合成矩阵。";
            this.LogGameplay($"action=cpu_queue result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} batches={batches:N0} reason={Quote(message)}");
            return false;
        }

        if (!this.TryCreatePlan(network, Guid.Empty, pattern, Math.Max(1, batches), out var steps, out var reservations, out var nodeCount, out message))
        {
            this.LogGameplay($"action=cpu_queue result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} batches={batches:N0} reason={Quote(message)}");
            return false;
        }

        if (steps.Count == 0)
        {
            message = $"网络中已有足够的 {pattern.DisplayName}。";
            this.LogGameplay($"action=cpu_queue result=success_no_job network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} batches={batches:N0} reason={Quote(message)}");
            return true;
        }

        if (nodeCount > matrixNodeLimit)
        {
            message = $"合成矩阵容量不足：{nodeCount:N0}/{matrixNodeLimit:N0}。";
            this.LogGameplay($"action=cpu_queue result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} batches={batches:N0} nodes={nodeCount:N0} matrixLimit={matrixNodeLimit:N0} reason={Quote(message)}");
            return false;
        }

        var isLongRunning = steps.Any(step => IsLongRunning(step.Pattern, this.getConfig()));
        var job = new CraftingJob
        {
            JobId = Guid.NewGuid(),
            TargetQualifiedItemId = targetOutputId,
            RequestedCount = steps.Sum(step => step.RequestedBatches),
            CompletedCount = 0,
            State = CraftingJobState.Planning,
            Pattern = ClonePattern(pattern),
            Steps = steps,
            Reservations = reservations,
            CurrentStepIndex = 0,
            NodeCount = nodeCount,
            StatusMessage = isLongRunning
                ? $"已规划 {steps.Count:N0} 步，{nodeCount:N0} 节点；最长加工约 {FormatProcessingDuration(GetMaxProcessingMinutes(steps))}。"
                : $"已规划 {steps.Count:N0} 步，{nodeCount:N0} 节点。",
            CreatedTick = Game1.ticks,
            IsLongRunning = isLongRunning
        };

        network.Jobs.Add(job);
        this.repository.Save();

        message = $"已排队 CPU 作业：{pattern.DisplayName}，共 {steps.Count:N0} 步。";
        this.LogGameplay($"action=cpu_queue result=success network={ShortId(network.NetworkId)} job={ShortId(job.JobId)} pattern={Quote(pattern.DisplayName)} kind={pattern.Kind} batches={batches:N0} requested={job.RequestedCount:N0} steps={steps.Count:N0} nodes={nodeCount:N0} matrixLimit={matrixNodeLimit:N0} longJob={isLongRunning}");
        return true;
    }

    public bool NeedsLongJobConfirmation(PatternData pattern)
    {
        return this.getConfig().RequireConfirmForLongCpuJobs
            && IsLongRunning(pattern, this.getConfig());
    }

    public bool NeedsLongJobConfirmation(NetworkData network, PatternData pattern, int batches)
    {
        if (!this.getConfig().RequireConfirmForLongCpuJobs)
            return false;

        if (IsLongRunning(pattern, this.getConfig()))
            return true;

        return this.TryCreatePlan(
                network,
                Guid.Empty,
                pattern,
                Math.Max(1, batches),
                out var steps,
                out _,
                out _,
                out _)
            && steps.Any(step => IsLongRunning(step.Pattern, this.getConfig()));
    }

    public bool CanToggleCaskPipeline(Item item)
    {
        return this.getConfig().EnableProcessingPatterns && IsCaskAgeable(item);
    }

    public bool TryTogglePipeline(NetworkData network, PatternData? pattern, out string message)
    {
        if (!this.getConfig().EnableProcessingPatterns)
        {
            message = "加工样板已禁用。";
            this.LogGameplay($"action=pipeline_toggle result=fail network={ShortId(network.NetworkId)} reason={Quote(message)}");
            return false;
        }

        if (pattern is null || pattern.Kind != PatternKind.Processing)
        {
            message = "请先手持或选择一个加工样板。";
            this.LogGameplay($"action=pipeline_toggle result=fail network={ShortId(network.NetworkId)} reason={Quote(message)}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(pattern.MachineQualifiedItemId))
        {
            message = "加工样板没有机器 ID。";
            this.LogGameplay($"action=pipeline_toggle result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} reason={Quote(message)}");
            return false;
        }

        if (pattern.MachineQualifiedItemId == CaskMachineQualifiedItemId)
        {
            message = "陈酿请使用陈酿流水线按钮。";
            this.LogGameplay($"action=pipeline_toggle result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} machine={Quote(pattern.MachineQualifiedItemId)} reason={Quote(message)}");
            return false;
        }

        var outputKey = GetPrimaryPatternOutputIdentityKey(pattern);
        if (string.IsNullOrWhiteSpace(outputKey))
        {
            message = "加工样板没有具体产物。";
            this.LogGameplay($"action=pipeline_toggle result=fail network={ShortId(network.NetworkId)} pattern={Quote(pattern.DisplayName)} reason={Quote(message)}");
            return false;
        }

        var existing = network.ProductionPipelines.Values.FirstOrDefault(pipeline =>
            pipeline.Mode == ProductionPipelineMode.StandardProcessing
            && pipeline.MachineQualifiedItemId == pattern.MachineQualifiedItemId
            && GetPrimaryPatternOutputIdentityKey(pipeline.Pattern) == outputKey);

        if (existing is not null)
        {
            existing.Enabled = !existing.Enabled;
            existing.Pattern = ClonePattern(pattern);
            existing.StatusMessage = existing.Enabled ? "已启用。" : "已禁用。";
            this.repository.Save();
            message = existing.Enabled
                ? $"已启用流水线：{pattern.DisplayName}。"
                : $"已禁用流水线：{pattern.DisplayName}。";
            this.LogGameplay($"action=pipeline_toggle result=success network={ShortId(network.NetworkId)} pipeline={ShortId(existing.PipelineId)} pattern={Quote(pattern.DisplayName)} machine={Quote(pattern.MachineQualifiedItemId)} enabled={existing.Enabled}");
            return true;
        }

        var pipeline = new ProductionPipelineData
        {
            PipelineId = Guid.NewGuid(),
            Enabled = true,
            Priority = network.ProductionPipelines.Count,
            Mode = ProductionPipelineMode.StandardProcessing,
            Pattern = ClonePattern(pattern),
            MachineQualifiedItemId = pattern.MachineQualifiedItemId,
            ItemsPerCycle = 1,
            TargetKeep = 0,
            TickInterval = 240,
            StatusMessage = "已启用。"
        };
        network.ProductionPipelines[pipeline.PipelineId] = pipeline;
        this.repository.Save();

        message = $"已启用流水线：{pattern.DisplayName}。";
        this.LogGameplay($"action=pipeline_toggle result=success network={ShortId(network.NetworkId)} pipeline={ShortId(pipeline.PipelineId)} pattern={Quote(pattern.DisplayName)} machine={Quote(pattern.MachineQualifiedItemId)} enabled=true created=true");
        return true;
    }

    public bool TryToggleCaskPipeline(NetworkData network, Item inputItem, out string message)
    {
        if (!this.getConfig().EnableProcessingPatterns)
        {
            message = "加工样板已禁用。";
            this.LogGameplay($"action=cask_pipeline_toggle result=fail network={ShortId(network.NetworkId)} item={Quote(inputItem.DisplayName)} itemId={Quote(inputItem.QualifiedItemId)} reason={Quote(message)}");
            return false;
        }

        if (!IsCaskAgeable(inputItem))
        {
            message = "请先手持可陈酿的物品：酒、啤酒、淡啤酒、蜂蜜酒、奶酪或山羊奶酪。";
            this.LogGameplay($"action=cask_pipeline_toggle result=fail network={ShortId(network.NetworkId)} item={Quote(inputItem.DisplayName)} itemId={Quote(inputItem.QualifiedItemId)} reason={Quote(message)}");
            return false;
        }

        var serializedInput = SerializedItemCodec.SerializePrototype(inputItem.getOne());
        var existing = network.ProductionPipelines.Values.FirstOrDefault(pipeline =>
            pipeline.Mode == ProductionPipelineMode.CaskAging
            && pipeline.Pattern.Inputs.Any(input => string.Equals(input.SerializedItemPrototype, serializedInput, StringComparison.Ordinal)));

        if (existing is not null)
        {
            existing.Enabled = !existing.Enabled;
            existing.StatusMessage = existing.Enabled
                ? $"已启用；目标品质 q{NormalizeCaskTargetQuality(this.getConfig().CaskTargetQuality)}。"
                : "已禁用。";
            this.repository.Save();
            message = existing.Enabled
                ? $"已启用陈酿流水线：{inputItem.DisplayName}。"
                : $"已禁用陈酿流水线：{inputItem.DisplayName}。";
            this.LogGameplay($"action=cask_pipeline_toggle result=success network={ShortId(network.NetworkId)} pipeline={ShortId(existing.PipelineId)} item={Quote(inputItem.DisplayName)} itemId={Quote(inputItem.QualifiedItemId)} enabled={existing.Enabled} targetQuality={NormalizeCaskTargetQuality(this.getConfig().CaskTargetQuality)}");
            return true;
        }

        var request = new NetworkItemRequest
        {
            QualifiedItemId = inputItem.QualifiedItemId,
            SerializedItemPrototype = serializedInput,
            Count = 1
        };
        var pipeline = new ProductionPipelineData
        {
            PipelineId = Guid.NewGuid(),
            Enabled = true,
            Priority = network.ProductionPipelines.Count,
            Mode = ProductionPipelineMode.CaskAging,
            Pattern = new PatternData
            {
                Kind = PatternKind.Processing,
                DisplayName = $"陈酿：{inputItem.DisplayName}",
                MachineQualifiedItemId = CaskMachineQualifiedItemId,
                Inputs = new List<NetworkItemRequest> { CloneRequest(request, 1) },
                Outputs = new List<NetworkItemRequest>
                {
                    new NetworkItemRequest
                    {
                        QualifiedItemId = inputItem.QualifiedItemId,
                        Count = 1
                    }
                },
                ProcessingMinutes = 0,
                SpeedClass = ProcessingSpeedClass.Slow
            },
            MachineQualifiedItemId = CaskMachineQualifiedItemId,
            ItemsPerCycle = 1,
            TargetKeep = 0,
            TickInterval = 240,
            StatusMessage = $"已启用；目标品质 q{NormalizeCaskTargetQuality(this.getConfig().CaskTargetQuality)}。"
        };

        network.ProductionPipelines[pipeline.PipelineId] = pipeline;
        this.repository.Save();

        message = $"已启用陈酿流水线：{inputItem.DisplayName}。";
        this.LogGameplay($"action=cask_pipeline_toggle result=success network={ShortId(network.NetworkId)} pipeline={ShortId(pipeline.PipelineId)} item={Quote(inputItem.DisplayName)} itemId={Quote(inputItem.QualifiedItemId)} enabled=true created=true targetQuality={NormalizeCaskTargetQuality(this.getConfig().CaskTargetQuality)}");
        return true;
    }

    public bool TryUpdatePipeline(NetworkData network, Guid pipelineId, string action, out string message)
    {
        if (!network.ProductionPipelines.TryGetValue(pipelineId, out var pipeline))
        {
            message = "流水线已不存在。";
            this.LogGameplay($"action=pipeline_update result=fail network={ShortId(network.NetworkId)} pipeline={ShortId(pipelineId)} requestAction={Quote(action)} reason={Quote(message)}");
            return false;
        }

        switch (action)
        {
            case PipelineActionToggle:
                pipeline.Enabled = !pipeline.Enabled;
                pipeline.StatusMessage = pipeline.Enabled ? "已启用。" : "已禁用。";
                message = pipeline.Enabled
                    ? $"已启用流水线：{pipeline.Pattern.DisplayName}。"
                    : $"已禁用流水线：{pipeline.Pattern.DisplayName}。";
                break;

            case PipelineActionPriorityUp:
                if (!this.TryMovePipelinePriority(network, pipelineId, -1, out message))
                    return false;
                pipeline.StatusMessage = "优先级已提高。";
                break;

            case PipelineActionPriorityDown:
                if (!this.TryMovePipelinePriority(network, pipelineId, 1, out message))
                    return false;
                pipeline.StatusMessage = "优先级已降低。";
                break;

            case PipelineActionTargetUp:
                pipeline.TargetKeep = Math.Min(PipelineMaxTargetKeep, Math.Max(0, pipeline.TargetKeep) + PipelineTargetStep);
                pipeline.StatusMessage = pipeline.TargetKeep > 0
                    ? $"目标保有：{pipeline.TargetKeep:N0}。"
                    : "未设置目标保有。";
                message = $"流水线目标保有：{pipeline.TargetKeep:N0}。";
                break;

            case PipelineActionTargetDown:
                pipeline.TargetKeep = Math.Max(0, pipeline.TargetKeep - PipelineTargetStep);
                pipeline.StatusMessage = pipeline.TargetKeep > 0
                    ? $"目标保有：{pipeline.TargetKeep:N0}。"
                    : "未设置目标保有。";
                message = pipeline.TargetKeep > 0
                    ? $"流水线目标保有：{pipeline.TargetKeep:N0}。"
                    : "流水线目标保有已关闭。";
                break;

            case PipelineActionCycleUp:
                pipeline.ItemsPerCycle = Math.Min(PipelineMaxItemsPerCycle, Math.Max(1, pipeline.ItemsPerCycle) + 1);
                pipeline.StatusMessage = $"每轮处理：{pipeline.ItemsPerCycle:N0}。";
                message = $"流水线每轮处理：{pipeline.ItemsPerCycle:N0}。";
                break;

            case PipelineActionCycleDown:
                pipeline.ItemsPerCycle = Math.Max(1, pipeline.ItemsPerCycle - 1);
                pipeline.StatusMessage = $"每轮处理：{pipeline.ItemsPerCycle:N0}。";
                message = $"流水线每轮处理：{pipeline.ItemsPerCycle:N0}。";
                break;

            default:
                message = "未知流水线操作。";
                return false;
        }

        this.repository.Save();
        this.LogGameplay($"action=pipeline_update result=success network={ShortId(network.NetworkId)} pipeline={ShortId(pipeline.PipelineId)} requestAction={Quote(action)} enabled={pipeline.Enabled} priority={pipeline.Priority:N0} targetKeep={pipeline.TargetKeep:N0} itemsPerCycle={pipeline.ItemsPerCycle:N0} message={Quote(message)}");
        return true;
    }

    public void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || e.Ticks % TickInterval != 0)
            return;

        var changed = false;
        var maxOperations = Math.Max(1, this.getConfig().MaxOperationsPerTick);
        var operations = this.operationBudget.GetUsed(e.Ticks);
        foreach (var network in this.repository.Data.Networks.Values)
        {
            if (this.getConfig().EnableAutocrafting)
            {
                changed |= this.ReconcileAssignments(network);
                changed |= this.AssignPlanningJobs(network);
                if (operations < maxOperations)
                    changed |= this.RunAssignedJobs(network, maxOperations, ref operations);
            }

            if (operations < maxOperations)
                changed |= this.RunProductionPipelines(network, e.Ticks, maxOperations, ref operations);
        }

        this.operationBudget.SetUsed(e.Ticks, operations, maxOperations);

        if (changed)
            this.repository.Save();
    }

    private bool RunAssignedJobs(NetworkData network, int maxOperations, ref int operations)
    {
        var changed = false;
        var jobDispatchBudget = this.GetCoProcessorDispatchBudget(network);

        foreach (var job in network.Jobs.Where(job => job.State is CraftingJobState.Running or CraftingJobState.WaitingForMachine or CraftingJobState.WaitingForOutput).ToList())
        {
            if (operations >= maxOperations)
                break;

            var beforeState = job.State;
            var beforeCompleted = job.CompletedCount;
            var beforeStatus = job.StatusMessage;
            var dispatches = 0;

            while (operations < maxOperations && dispatches < jobDispatchBudget)
            {
                var step = this.GetCurrentStep(job);
                if (step is null)
                {
                    var didComplete = this.CompleteJob(job);
                    if (didComplete)
                    {
                        operations++;
                        dispatches++;
                        changed = true;
                    }
                    break;
                }

                var stateBeforeDispatch = job.State;
                var completedBeforeDispatch = job.CompletedCount;
                var statusBeforeDispatch = job.StatusMessage;

                var didWork = step.Pattern.Kind == PatternKind.Crafting
                    ? this.RunCraftingJob(network, job, step)
                    : this.RunProcessingJob(network, job, step);

                if (didWork)
                {
                    operations++;
                    dispatches++;
                }

                if (job.State == CraftingJobState.Failed)
                {
                    this.ReleaseUnconsumedReservations(job);
                    changed = true;
                    break;
                }

                if (stateBeforeDispatch != job.State || completedBeforeDispatch != job.CompletedCount || statusBeforeDispatch != job.StatusMessage)
                    changed = true;

                if (!didWork || job.State is CraftingJobState.WaitingForMachine or CraftingJobState.WaitingForOutput or CraftingJobState.Completed or CraftingJobState.Cancelled)
                    break;
            }

            if (beforeState != job.State || beforeCompleted != job.CompletedCount || beforeStatus != job.StatusMessage)
                changed = true;
        }

        return changed;
    }

    private int GetCoProcessorDispatchBudget(NetworkData network)
    {
        var coProcessors = this.GetActiveEndpoints(network, EndpointType.CoProcessor).Count();
        if (coProcessors <= 0)
            return 1;

        return Math.Min(8, 1 << Math.Min(3, coProcessors));
    }

    private bool RunProductionPipelines(NetworkData network, uint tick, int maxOperations, ref int operations)
    {
        if (!this.getConfig().EnableProcessingPatterns)
            return false;

        if (network.ProductionPipelines.Count == 0)
            return false;

        var changed = false;
        var claimed = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var touchedPipelines = new HashSet<Guid>();
        var pipelines = network.ProductionPipelines.Values
            .Where(pipeline => pipeline.Enabled)
            .OrderBy(pipeline => pipeline.Priority)
            .ThenBy(pipeline => pipeline.PipelineId)
            .ToList();

        foreach (var pipeline in pipelines)
        {
            if (operations >= maxOperations)
                break;

            if (!this.ShouldRunPipeline(pipeline, tick))
                continue;

            if (pipeline.Mode == ProductionPipelineMode.CaskAging)
            {
                if (this.RunCaskPipeline(network, pipeline, claimed, maxOperations, ref operations))
                {
                    touchedPipelines.Add(pipeline.PipelineId);
                    changed = true;
                }
                continue;
            }

            var pipelineOperations = 0;
            var pipelineLimit = Math.Max(1, pipeline.ItemsPerCycle);
            foreach (var target in this.GetPipelineMachines(network, pipeline))
            {
                if (operations >= maxOperations || pipelineOperations >= pipelineLimit)
                    break;

                var key = GetMachineKey(target);
                if (claimed.TryGetValue(key, out var owner) && owner != pipeline.PipelineId)
                    continue;

                if (!this.TryCollectPipelineOutput(network, pipeline, target))
                    continue;

                claimed[key] = pipeline.PipelineId;
                touchedPipelines.Add(pipeline.PipelineId);
                operations++;
                pipelineOperations++;
                changed = true;
            }
        }

        foreach (var pipeline in pipelines)
        {
            if (operations >= maxOperations)
                break;

            if (!this.ShouldRunPipeline(pipeline, tick))
                continue;

            if (pipeline.Mode != ProductionPipelineMode.StandardProcessing)
                continue;

            if (pipeline.TargetKeep > 0 && this.OutputCount(network, pipeline) >= pipeline.TargetKeep)
            {
                pipeline.StatusMessage = $"已达到目标保有：{pipeline.TargetKeep:N0}。";
                pipeline.LastRunTick = tick;
                touchedPipelines.Add(pipeline.PipelineId);
                changed = true;
                continue;
            }

            var pipelineOperations = 0;
            var pipelineLimit = Math.Max(1, pipeline.ItemsPerCycle);
            foreach (var target in this.GetPipelineMachines(network, pipeline))
            {
                if (operations >= maxOperations || pipelineOperations >= pipelineLimit)
                    break;

                var key = GetMachineKey(target);
                if (claimed.TryGetValue(key, out var owner) && owner != pipeline.PipelineId)
                    continue;

                if (target.Machine.heldObject.Value is not null)
                    continue;

                if (!this.TryFeedPipelineInput(network, pipeline, target))
                    continue;

                claimed[key] = pipeline.PipelineId;
                touchedPipelines.Add(pipeline.PipelineId);
                operations++;
                pipelineOperations++;
                changed = true;
            }
        }

        foreach (var pipeline in pipelines.Where(pipeline => touchedPipelines.Contains(pipeline.PipelineId)))
            pipeline.LastRunTick = tick;

        return changed;
    }

    private bool RunCaskPipeline(
        NetworkData network,
        ProductionPipelineData pipeline,
        Dictionary<string, Guid> claimed,
        int maxOperations,
        ref int operations)
    {
        var changed = false;
        var pipelineOperations = 0;
        var pipelineLimit = Math.Max(1, pipeline.ItemsPerCycle);

        foreach (var target in this.GetPipelineMachines(network, pipeline))
        {
            if (operations >= maxOperations || pipelineOperations >= pipelineLimit)
                break;

            var key = GetMachineKey(target);
            if (claimed.TryGetValue(key, out var owner) && owner != pipeline.PipelineId)
                continue;

            var didWork = false;
            if (this.TryCollectCaskOutput(network, pipeline, target))
            {
                operations++;
                pipelineOperations++;
                didWork = true;
            }

            if (operations < maxOperations
                && pipelineOperations < pipelineLimit
                && this.TryFeedCaskInput(network, pipeline, target))
            {
                operations++;
                pipelineOperations++;
                didWork = true;
            }

            if (!didWork)
                continue;

            claimed[key] = pipeline.PipelineId;
            changed = true;
        }

        return changed;
    }

    private bool TryMovePipelinePriority(NetworkData network, Guid pipelineId, int direction, out string message)
    {
        var ordered = network.ProductionPipelines.Values
            .OrderBy(pipeline => pipeline.Priority)
            .ThenBy(pipeline => pipeline.PipelineId)
            .ToList();
        var index = ordered.FindIndex(pipeline => pipeline.PipelineId == pipelineId);
        if (index < 0)
        {
            message = "流水线已不存在。";
            return false;
        }

        var targetIndex = index + Math.Sign(direction);
        if (targetIndex < 0 || targetIndex >= ordered.Count)
        {
            this.NormalizePipelinePriorities(ordered);
            message = direction < 0
                ? "流水线已经是最高优先级。"
                : "流水线已经是最低优先级。";
            return true;
        }

        var moving = ordered[index];
        ordered[index] = ordered[targetIndex];
        ordered[targetIndex] = moving;
        this.NormalizePipelinePriorities(ordered);
        message = direction < 0
            ? $"已提高流水线优先级：{moving.Pattern.DisplayName}。"
            : $"已降低流水线优先级：{moving.Pattern.DisplayName}。";
        return true;
    }

    private void NormalizePipelinePriorities(IReadOnlyList<ProductionPipelineData> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Priority = i;
    }

    private bool ShouldRunPipeline(ProductionPipelineData pipeline, uint tick)
    {
        return tick >= (uint)Math.Max(0, pipeline.LastRunTick)
            && tick - (uint)Math.Max(0, pipeline.LastRunTick) >= Math.Max(1, pipeline.TickInterval);
    }

    private bool TryCollectPipelineOutput(NetworkData network, ProductionPipelineData pipeline, MachineTarget target)
    {
        var held = target.Machine.heldObject.Value;
        if (held is null || !target.Machine.readyForHarvest.Value)
            return false;

        if (!OutputMatchesPattern(pipeline.Pattern, held))
            return false;

        if (!this.TryDepositWholeMachineOutput(
                network,
                target.Machine,
                held,
                "等待网络存储空间。",
                "收取时网络存储发生变化。",
                out var depositMessage))
        {
            pipeline.StatusMessage = depositMessage;
            return false;
        }

        MachineStateHelper.ResetAfterAutomatedCollect(target.Machine);
        pipeline.StatusMessage = $"已收取 {held.DisplayName}。";
        return true;
    }

    private bool TryFeedPipelineInput(NetworkData network, ProductionPipelineData pipeline, MachineTarget target)
    {
        if (pipeline.Pattern.Inputs.Count == 0)
        {
            pipeline.StatusMessage = "流水线样板没有输入。";
            return false;
        }

        if (!TryProbeSingleMachineInput(target.Machine, pipeline.Pattern.Inputs, out var probeMessage))
        {
            pipeline.StatusMessage = probeMessage;
            return false;
        }

        if (!this.TryExtractInputs(network, pipeline.Pattern.Inputs, Guid.Empty, target, out var extractedItems, out var extractMessage))
        {
            pipeline.StatusMessage = extractMessage;
            return false;
        }

        var scratchInventory = new Inventory();
        scratchInventory.AddRange(extractedItems);
        try
        {
            target.Machine.AttemptAutoLoad(scratchInventory, Game1.player);
        }
        catch (Exception ex)
        {
            this.ReturnScratchInventory(network, scratchInventory, target);
            pipeline.StatusMessage = $"机器自动投料失败：{ex.Message}";
            return false;
        }

        if (target.Machine.heldObject.Value is null)
        {
            this.ReturnScratchInventory(network, scratchInventory, target);
            pipeline.StatusMessage = "机器拒绝流水线输入。";
            return false;
        }

        this.ReturnScratchInventory(network, scratchInventory, target);
        pipeline.StatusMessage = $"已向 {target.Machine.DisplayName} 投料。";
        return true;
    }

    private bool TryCollectCaskOutput(NetworkData network, ProductionPipelineData pipeline, MachineTarget target)
    {
        var held = target.Machine.heldObject.Value;
        if (held is null)
            return false;

        if (!this.CaskOutputMatchesPipeline(pipeline, held))
            return false;

        var targetQuality = NormalizeCaskTargetQuality(this.getConfig().CaskTargetQuality);
        var currentQuality = held is SObject obj ? obj.Quality : 0;
        if (currentQuality < targetQuality)
        {
            pipeline.StatusMessage = $"陈酿中 {held.DisplayName}：q{currentQuality}/q{targetQuality}。";
            return false;
        }

        if (!this.TryDepositWholeMachineOutput(
                network,
                target.Machine,
                held,
                "等待网络存储空间。",
                "收取时网络存储发生变化。",
                out var depositMessage))
        {
            pipeline.StatusMessage = depositMessage;
            return false;
        }

        MachineStateHelper.ResetAfterAutomatedCollect(target.Machine);
        pipeline.StatusMessage = $"已收取陈酿产物 {held.DisplayName} q{currentQuality}。";
        return true;
    }

    private bool TryFeedCaskInput(NetworkData network, ProductionPipelineData pipeline, MachineTarget target)
    {
        if (target.Machine.heldObject.Value is not null)
            return false;

        return this.TryFeedPipelineInput(network, pipeline, target);
    }

    private bool RunCraftingJob(NetworkData network, CraftingJob job, CraftingJobStep step)
    {
        if (step.CompletedBatches >= step.RequestedBatches)
            return this.AdvanceStep(job, step);

        var assembler = this.GetActiveEndpoints(network, EndpointType.MolecularAssembler)
            .FirstOrDefault(endpoint => this.FindPlacedObject(endpoint)?.QualifiedItemId == "(BC)" + ModItemCatalog.MolecularAssembler);
        if (assembler is null)
        {
            job.StatusMessage = "等待已连接的分子装配室。";
            return false;
        }

        var outputs = this.CreateOutputItems(step.Pattern, out var outputError);
        if (outputs is null)
        {
            job.State = CraftingJobState.Failed;
            job.StatusMessage = outputError;
            job.AssignedCpuEndpointId = null;
            return true;
        }

        foreach (var output in outputs)
        {
            if (!this.transactionService.CanAcceptNetworkItem(network, output, output.Stack))
            {
                job.StatusMessage = "等待网络存储空间。";
                return false;
            }
        }

        var consumedReservations = new List<ReservationConsume>();
        if (!this.TryConsumeReservations(job, step.Pattern.Inputs, consumedReservations, out var reserveMessage))
        {
            job.State = CraftingJobState.Failed;
            job.StatusMessage = reserveMessage;
            job.AssignedCpuEndpointId = null;
            return true;
        }

        if (!this.transactionService.TryConsumeIngredients(network, step.Pattern.Inputs, out var consumeMessage, job.JobId))
        {
            this.RollbackReservationConsumption(consumedReservations);
            job.StatusMessage = consumeMessage;
            return false;
        }

        foreach (var output in outputs)
        {
            var expected = output.Stack;
            if (!this.transactionService.TryDepositItem(network, output, out var moved) || moved < expected)
            {
                job.State = CraftingJobState.Failed;
                job.StatusMessage = "材料已消耗，但合成产物无法存入。";
                job.AssignedCpuEndpointId = null;
                this.DropLeftover(assembler, output);
                this.monitor.Log($"CPU job {job.JobId:N} failed to store crafted output {output.QualifiedItemId}; dropped leftover near assembler.", LogLevel.Warn);
                return true;
            }
        }

        step.CompletedBatches++;
        step.State = CraftingJobState.Running;
        this.SyncJobProgress(job);
        job.StatusMessage = $"步骤 {step.StepIndex + 1:N0}/{job.Steps.Count:N0}：已合成 {step.CompletedBatches:N0}/{step.RequestedBatches:N0}。";
        if (step.CompletedBatches >= step.RequestedBatches)
            return this.AdvanceStep(job, step);

        return true;
    }

    private bool RunProcessingJob(NetworkData network, CraftingJob job, CraftingJobStep step)
    {
        if (!this.getConfig().EnableProcessingPatterns)
        {
            job.State = CraftingJobState.Failed;
            job.StatusMessage = "加工样板已禁用。";
            job.AssignedCpuEndpointId = null;
            return true;
        }

        if (step.CompletedBatches >= step.RequestedBatches)
            return this.AdvanceStep(job, step);

        if (job.State == CraftingJobState.WaitingForOutput)
            return this.TryCollectProcessingJob(network, job, step);

        if (string.IsNullOrWhiteSpace(step.Pattern.MachineQualifiedItemId))
        {
            job.State = CraftingJobState.Failed;
            job.StatusMessage = "加工样板没有机器 ID。";
            job.AssignedCpuEndpointId = null;
            return true;
        }

        var lastProbeMessage = string.Empty;
        foreach (var machineTarget in this.GetIdleMachinesForPattern(network, step.Pattern))
        {
            if (!TryProbeSingleMachineInput(machineTarget.Machine, step.Pattern.Inputs, out var probeMessage))
            {
                lastProbeMessage = probeMessage;
                continue;
            }

            var consumedReservations = new List<ReservationConsume>();
            if (!this.TryConsumeReservations(job, step.Pattern.Inputs, consumedReservations, out var reserveMessage))
            {
                job.State = CraftingJobState.Failed;
                job.AssignedCpuEndpointId = null;
                job.StatusMessage = reserveMessage;
                return true;
            }

            if (!this.TryExtractInputs(network, step.Pattern.Inputs, job.JobId, machineTarget, out var extractedItems, out var extractMessage))
            {
                this.RollbackReservationConsumption(consumedReservations);
                job.State = CraftingJobState.MissingItems;
                job.AssignedCpuEndpointId = null;
                job.StatusMessage = extractMessage;
                return true;
            }

            var scratchInventory = new Inventory();
            scratchInventory.AddRange(extractedItems);

            try
            {
                machineTarget.Machine.AttemptAutoLoad(scratchInventory, Game1.player);
            }
            catch (Exception ex)
            {
                this.ReturnScratchInventory(network, scratchInventory, machineTarget);
                this.RollbackReservationConsumption(consumedReservations);
                job.StatusMessage = $"机器自动投料失败：{ex.Message}";
                return false;
            }

            if (machineTarget.Machine.heldObject.Value is null)
            {
                this.ReturnScratchInventory(network, scratchInventory, machineTarget);
                this.RollbackReservationConsumption(consumedReservations);
                job.StatusMessage = "机器拒绝抽取出的输入物。";
                return false;
            }

            this.ReturnScratchInventory(network, scratchInventory, machineTarget);
            job.State = CraftingJobState.WaitingForOutput;
            step.State = CraftingJobState.WaitingForOutput;
            job.WaitingMachineLocationName = machineTarget.Location.NameOrUniqueName;
            job.WaitingMachineTileX = machineTarget.Tile.X;
            job.WaitingMachineTileY = machineTarget.Tile.Y;
            var remainingMinutes = GetMachineRemainingMinutes(machineTarget.Machine, step.Pattern);
            job.StatusMessage = $"步骤 {step.StepIndex + 1:N0}/{job.Steps.Count:N0}：等待 {machineTarget.Machine.DisplayName}（剩余 {FormatRemainingProcessingTime(remainingMinutes)}）。";
            this.LogGameplay($"action=cpu_processing_wait result=success network={ShortId(network.NetworkId)} job={ShortId(job.JobId)} pattern={Quote(step.Pattern.DisplayName)} machine={Quote(machineTarget.Machine.DisplayName)} machineId={Quote(machineTarget.Machine.QualifiedItemId)} location={Quote(machineTarget.Location.NameOrUniqueName)} tile=({machineTarget.Tile.X:0},{machineTarget.Tile.Y:0}) remainingMinutes={remainingMinutes:N0}");
            return true;
        }

        job.State = CraftingJobState.WaitingForMachine;
        step.State = CraftingJobState.WaitingForMachine;
        job.StatusMessage = string.IsNullOrWhiteSpace(lastProbeMessage)
            ? "等待机器接口旁边出现空闲的匹配机器。"
            : lastProbeMessage;
        return false;
    }

    private bool TryCollectProcessingJob(NetworkData network, CraftingJob job, CraftingJobStep step)
    {
        var location = Game1.getLocationFromName(job.WaitingMachineLocationName);
        if (location is null
            || !location.objects.TryGetValue(new Vector2(job.WaitingMachineTileX, job.WaitingMachineTileY), out SObject? machine))
        {
            if (!this.TryFindProcessingOutputMachine(network, step.Pattern, out var recoveredTarget))
            {
                job.State = CraftingJobState.WaitingForOutput;
                step.State = CraftingJobState.WaitingForOutput;
                job.StatusMessage = "追踪的机器已被移除；等待重新连接带有匹配产物的机器。";
                return false;
            }

            location = recoveredTarget.Location;
            machine = recoveredTarget.Machine;
            job.WaitingMachineLocationName = recoveredTarget.Location.NameOrUniqueName;
            job.WaitingMachineTileX = recoveredTarget.Tile.X;
            job.WaitingMachineTileY = recoveredTarget.Tile.Y;
            job.StatusMessage = $"已找回移动后的机器：{job.WaitingMachineLocationName} ({job.WaitingMachineTileX:N0},{job.WaitingMachineTileY:N0})。";
        }

        var held = machine.heldObject.Value;
        if (held is null)
        {
            job.State = CraftingJobState.Failed;
            job.StatusMessage = "追踪的机器产物消失了。";
            job.AssignedCpuEndpointId = null;
            return true;
        }

        if (!machine.readyForHarvest.Value)
        {
            var remainingMinutes = GetMachineRemainingMinutes(machine, step.Pattern);
            job.StatusMessage = $"等待 {machine.DisplayName} 产出（剩余 {FormatRemainingProcessingTime(remainingMinutes)}）。";
            return false;
        }

        if (!OutputMatchesPattern(step.Pattern, held))
        {
            job.State = CraftingJobState.Failed;
            job.StatusMessage = $"机器产物不匹配：{held.QualifiedItemId}。";
            job.AssignedCpuEndpointId = null;
            return true;
        }

        if (!this.TryDepositWholeMachineOutput(
                network,
                machine,
                held,
                "等待网络空间存入机器产物。",
                "收取机器产物时网络存储发生变化。",
                out var depositMessage))
        {
            job.StatusMessage = depositMessage;
            return false;
        }

        MachineStateHelper.ResetAfterAutomatedCollect(machine);

        step.CompletedBatches++;
        step.State = CraftingJobState.Running;
        this.SyncJobProgress(job);
        job.WaitingMachineLocationName = string.Empty;
        job.WaitingMachineTileX = 0;
        job.WaitingMachineTileY = 0;
        job.StatusMessage = $"步骤 {step.StepIndex + 1:N0}/{job.Steps.Count:N0}：已收取 {step.CompletedBatches:N0}/{step.RequestedBatches:N0}。";

        if (step.CompletedBatches >= step.RequestedBatches)
            return this.AdvanceStep(job, step);

        job.State = CraftingJobState.Running;
        return true;
    }

    private bool TryFindProcessingOutputMachine(NetworkData network, PatternData pattern, out MachineTarget target)
    {
        foreach (var candidate in this.GetProcessingMachinesForPattern(network, pattern))
        {
            var held = candidate.Machine.heldObject.Value;
            if (held is not null && OutputMatchesPattern(pattern, held))
            {
                target = candidate;
                return true;
            }
        }

        target = default!;
        return false;
    }

    private bool AssignPlanningJobs(NetworkData network)
    {
        var changed = false;
        var cpuEndpoints = this.GetActiveEndpoints(network, EndpointType.CraftingCpuCore).ToList();
        var totalSlots = cpuEndpoints.Count;
        var matrixNodeLimit = this.GetMatrixNodeLimit(network);
        var activeJobs = network.Jobs
            .Where(job => IsCpuSlotState(job.State))
            .ToList();

        foreach (var job in network.Jobs.Where(job => job.State is CraftingJobState.Planning or CraftingJobState.MissingItems))
        {
            if (totalSlots <= 0)
            {
                changed |= this.SetStatus(job, "等待已连接的合成 CPU 核心。");
                continue;
            }

            var nodeCount = job.NodeCount > 0 ? job.NodeCount : GetNodeCount(job.Pattern);
            if (matrixNodeLimit <= 0 || nodeCount > matrixNodeLimit)
            {
                changed |= this.SetStatus(job, $"等待合成矩阵容量 {nodeCount:N0}/{Math.Max(0, matrixNodeLimit):N0}。");
                continue;
            }

            if (activeJobs.Count >= totalSlots)
            {
                changed |= this.SetStatus(job, "等待空闲 CPU 槽位。");
                continue;
            }

            if (job.IsLongRunning)
            {
                var reserve = totalSlots <= 1
                    ? 0
                    : Math.Clamp(this.getConfig().ReserveFastSlots, 0, totalSlots - 1);
                var maxLongSlots = totalSlots - reserve;
                var activeLongJobs = activeJobs.Count(active => active.IsLongRunning);
                if (activeLongJobs >= maxLongSlots)
                {
                    changed |= this.SetStatus(job, "等待未保留给快速作业的 CPU 槽位。");
                    continue;
                }
            }

            var usedCpuIds = activeJobs
                .Select(active => active.AssignedCpuEndpointId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToHashSet();
            var cpu = cpuEndpoints.FirstOrDefault(endpoint => !usedCpuIds.Contains(endpoint.EndpointId));
            if (cpu is null)
            {
                changed |= this.SetStatus(job, "等待空闲 CPU 槽位。");
                continue;
            }

            job.AssignedCpuEndpointId = cpu.EndpointId;
            var step = this.GetCurrentStep(job);
            var resumeState = step?.State is CraftingJobState.WaitingForMachine or CraftingJobState.WaitingForOutput
                ? step.State
                : CraftingJobState.Running;

            job.State = resumeState;
            if (step is not null && step.State is not (CraftingJobState.WaitingForMachine or CraftingJobState.WaitingForOutput))
                step.State = CraftingJobState.Running;
            job.StatusMessage = step is null
                ? $"正在 CPU {cpu.EndpointId:N} 上运行。"
                : resumeState == CraftingJobState.WaitingForOutput
                    ? $"已重新分配 CPU {cpu.EndpointId:N}；等待步骤 {step.StepIndex + 1:N0}/{job.Steps.Count:N0} 的机器产物：{step.Pattern.DisplayName}。"
                    : $"正在运行步骤 {step.StepIndex + 1:N0}/{job.Steps.Count:N0}：{step.Pattern.DisplayName}。";
            activeJobs.Add(job);
            changed = true;
        }

        return changed;
    }

    private bool ReconcileAssignments(NetworkData network)
    {
        var changed = false;
        var activeCpuIds = this.GetActiveEndpoints(network, EndpointType.CraftingCpuCore)
            .Select(endpoint => endpoint.EndpointId)
            .ToHashSet();
        var matrixNodeLimit = this.GetMatrixNodeLimit(network);

        foreach (var job in network.Jobs.Where(job => job.State is CraftingJobState.Running or CraftingJobState.WaitingForMachine or CraftingJobState.WaitingForOutput))
        {
            var nodeCount = job.NodeCount > 0 ? job.NodeCount : GetNodeCount(job.Pattern);
            if (matrixNodeLimit <= 0 || nodeCount > matrixNodeLimit)
            {
                job.State = CraftingJobState.Planning;
                job.AssignedCpuEndpointId = null;
                job.StatusMessage = $"合成矩阵不再有 {nodeCount:N0} 个节点容量。";
                changed = true;
                continue;
            }

            if (!job.AssignedCpuEndpointId.HasValue || activeCpuIds.Contains(job.AssignedCpuEndpointId.Value))
                continue;

            job.State = CraftingJobState.Planning;
            job.AssignedCpuEndpointId = null;
            job.StatusMessage = "已分配的 CPU 核心不再连接。";
            changed = true;
        }

        return changed;
    }

    private IEnumerable<MachineTarget> GetIdleMachinesForPattern(NetworkData network, PatternData pattern)
    {
        return this.GetProcessingMachinesForPattern(network, pattern)
            .Where(target => target.Machine.heldObject.Value is null);
    }

    private IEnumerable<MachineTarget> GetProcessingMachinesForPattern(NetworkData network, PatternData pattern)
    {
        foreach (var endpoint in this.GetActiveEndpoints(network, EndpointType.MachineInterface))
        {
            var location = Game1.getLocationFromName(endpoint.LocationName);
            if (location is null)
                continue;

            var origin = new Vector2(endpoint.TileX, endpoint.TileY);
            foreach (var offset in AdjacentOffsets)
            {
                var tile = origin + offset;
                if (!location.objects.TryGetValue(tile, out SObject? machine))
                    continue;

                if (machine.QualifiedItemId != pattern.MachineQualifiedItemId)
                    continue;

                yield return new MachineTarget(location, tile, machine);
            }
        }
    }

    private IEnumerable<MachineTarget> GetPipelineMachines(NetworkData network, ProductionPipelineData pipeline)
    {
        if (string.IsNullOrWhiteSpace(pipeline.MachineQualifiedItemId))
            yield break;

        foreach (var endpoint in this.GetActiveEndpoints(network, EndpointType.Machine))
        {
            var location = Game1.getLocationFromName(endpoint.LocationName);
            if (location is null)
                continue;

            var tile = new Vector2(endpoint.TileX, endpoint.TileY);
            if (!location.objects.TryGetValue(tile, out SObject? machine))
                continue;

            if (machine.QualifiedItemId == pipeline.MachineQualifiedItemId)
                yield return new MachineTarget(location, tile, machine);
        }
    }

    private static bool OutputMatchesPattern(PatternData pattern, Item output)
    {
        return pattern.Outputs.Any(expected => OutputRequestMatchesItem(pattern, expected, output));
    }

    private static NetworkItemRequest CreatePatternOutputRequest(PatternData pattern, NetworkItemRequest output, int count)
    {
        var request = CloneRequest(output, count);
        var preservedParentId = GetPreservedParentInputId(pattern, output.QualifiedItemId);
        if (!string.IsNullOrWhiteSpace(preservedParentId))
            request.PreservedParentQualifiedItemId = preservedParentId;

        return request;
    }

    private static string GetPrimaryPatternOutputIdentityKey(PatternData pattern)
    {
        var output = pattern.Outputs.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.QualifiedItemId));
        return output is null
            ? string.Empty
            : GetRequestIdentityKey(CreatePatternOutputRequest(pattern, output, 1));
    }

    private static string GetRequestIdentityKey(NetworkItemRequest request)
    {
        return string.Join(
            "|",
            request.QualifiedItemId ?? string.Empty,
            request.Category?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            request.SerializedItemPrototype ?? string.Empty,
            ItemKeyFactory.NormalizeItemId(request.PreservedParentQualifiedItemId));
    }

    private static string? GetPreservedParentInputId(PatternData pattern, string? outputQualifiedItemId)
    {
        if (string.IsNullOrWhiteSpace(outputQualifiedItemId)
            || !PreservedParentOutputQualifiedItemIds.Contains(outputQualifiedItemId))
        {
            return null;
        }

        foreach (var input in pattern.Inputs)
        {
            if (!string.IsNullOrWhiteSpace(input.QualifiedItemId))
                return input.QualifiedItemId;

            if (string.IsNullOrWhiteSpace(input.SerializedItemPrototype))
                continue;

            try
            {
                return SerializedItemCodec.CreateItem(input.SerializedItemPrototype, 1).QualifiedItemId;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool OutputRequestMatchesItem(PatternData pattern, NetworkItemRequest expected, Item output)
    {
        if (!string.IsNullOrWhiteSpace(expected.SerializedItemPrototype))
        {
            try
            {
                var prototype = SerializedItemCodec.CreateItem(expected.SerializedItemPrototype, 1);
                return ItemKeyFactory.SameStackBucket(ItemKeyFactory.FromItem(prototype), prototype, ItemKeyFactory.FromItem(output), output);
            }
            catch
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(expected.QualifiedItemId)
            || expected.QualifiedItemId != output.QualifiedItemId)
        {
            return false;
        }

        if (output is not SObject obj || IsEmptyPreservedParent(obj.preservedParentSheetIndex.Value))
            return true;

        return PatternInputsContainPreservedParent(pattern, obj.preservedParentSheetIndex.Value);
    }

    private static bool PatternInputsContainPreservedParent(PatternData pattern, string preservedParentId)
    {
        var normalizedParentId = NormalizeItemId(preservedParentId);
        foreach (var input in pattern.Inputs)
        {
            if (!string.IsNullOrWhiteSpace(input.QualifiedItemId)
                && NormalizeItemId(input.QualifiedItemId) == normalizedParentId)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(input.SerializedItemPrototype))
                continue;

            try
            {
                var prototype = SerializedItemCodec.CreateItem(input.SerializedItemPrototype, 1);
                if (NormalizeItemId(prototype.QualifiedItemId) == normalizedParentId)
                    return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsEmptyPreservedParent(string? preservedParentId)
    {
        return string.IsNullOrWhiteSpace(preservedParentId) || preservedParentId == "-1";
    }

    private static string NormalizeItemId(string itemId)
    {
        return ItemKeyFactory.NormalizeItemId(itemId);
    }

    private bool CaskOutputMatchesPipeline(ProductionPipelineData pipeline, Item output)
    {
        var input = pipeline.Pattern.Inputs.FirstOrDefault();
        if (input is null)
            return false;

        if (!string.IsNullOrWhiteSpace(input.QualifiedItemId)
            && input.QualifiedItemId != output.QualifiedItemId)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(input.SerializedItemPrototype))
            return true;

        try
        {
            var prototype = SerializedItemCodec.CreateItem(input.SerializedItemPrototype, 1);
            var outputKey = ItemKeyFactory.FromItem(output);
            var prototypeKey = ItemKeyFactory.FromItem(prototype);
            outputKey.Quality = 0;
            prototypeKey.Quality = 0;
            return ItemKeyFactory.SameDisplayBucket(outputKey, prototypeKey);
        }
        catch
        {
            return false;
        }
    }

    private int OutputCount(NetworkData network, ProductionPipelineData pipeline)
    {
        var output = pipeline.Pattern.Outputs.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.QualifiedItemId));
        if (output?.QualifiedItemId is null)
            return 0;

        return this.transactionService.GetAvailableCountMatching(network, entry => OutputMatchesPattern(pipeline.Pattern, entry.Prototype));
    }

    private static string GetMachineKey(MachineTarget target)
    {
        return $"{target.Location.NameOrUniqueName}:{target.Tile.X:0}:{target.Tile.Y:0}";
    }

    private IEnumerable<PatternData> GetStoredPatterns(NetworkData network)
    {
        foreach (var provider in network.PatternProviders.Values)
        {
            foreach (var slot in provider.Slots.OrderBy(slot => slot.SlotIndex))
            {
                var item = PatternCodec.CreateItem(slot);
                if (PatternCodec.TryRead(item, out var pattern))
                {
                    if (!this.getConfig().EnableProcessingPatterns && pattern.Kind == PatternKind.Processing)
                        continue;

                    yield return pattern;
                }
            }
        }
    }

    private IEnumerable<NetworkEndpoint> GetActiveEndpoints(NetworkData network, EndpointType type)
    {
        return network.Endpoints
            .Where(endpoint => endpoint.Active && endpoint.Type == type)
            .OrderBy(endpoint => endpoint.Priority)
            .ThenBy(endpoint => endpoint.LocationName, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.TileY)
            .ThenBy(endpoint => endpoint.TileX);
    }

    private SObject? FindPlacedObject(NetworkEndpoint endpoint)
    {
        var location = Game1.getLocationFromName(endpoint.LocationName);
        if (location is null)
            return null;

        return location.objects.TryGetValue(new Vector2(endpoint.TileX, endpoint.TileY), out SObject? placed)
            ? placed
            : null;
    }

    private int GetMatrixNodeLimit(NetworkData network)
    {
        var result = 0;
        foreach (var endpoint in network.Endpoints.Where(endpoint => endpoint.Active))
        {
            result = endpoint.Type switch
            {
                EndpointType.CraftingMatrix1K => Math.Max(result, 1000),
                EndpointType.CraftingMatrix4K => Math.Max(result, 4000),
                EndpointType.CraftingMatrix16K => Math.Max(result, 16000),
                EndpointType.CraftingMatrix64K => Math.Max(result, 64000),
                _ => result
            };
        }

        return result;
    }

    private bool TryCreatePlan(
        NetworkData network,
        Guid planningJobId,
        PatternData targetPattern,
        int targetBatches,
        out List<CraftingJobStep> steps,
        out List<CraftingReservation> reservations,
        out int nodeCount,
        out string message)
    {
        steps = new List<CraftingJobStep>();
        reservations = new List<CraftingReservation>();
        nodeCount = 0;

        var targetOutput = targetPattern.Outputs.FirstOrDefault(output => !string.IsNullOrWhiteSpace(output.QualifiedItemId));
        if (targetOutput?.QualifiedItemId is null)
        {
            message = "目标样板没有具体产物。";
            return false;
        }

        var patterns = this.GetStoredPatterns(network)
            .Concat(new[] { targetPattern })
            .Where(pattern => this.getConfig().EnableProcessingPatterns || pattern.Kind != PatternKind.Processing)
            .Where(pattern => pattern.Outputs.Any(output => !string.IsNullOrWhiteSpace(output.QualifiedItemId)))
            .Select(ClonePattern)
            .ToList();
        var patternsByOutput = patterns
            .GroupBy(pattern => pattern.Outputs.First(output => !string.IsNullOrWhiteSpace(output.QualifiedItemId)).QualifiedItemId!)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var context = new PlanContext(network, patterns, patternsByOutput, planningJobId);
        var requestedCount = Math.Max(1, targetBatches) * Math.Max(1, targetOutput.Count);
        if (!this.PlanPatternOutputNeed(context, targetPattern, targetOutput, requestedCount, 0))
        {
            message = "缺少：" + string.Join(", ", context.MissingLines);
            return false;
        }

        for (var i = 0; i < context.Steps.Count; i++)
            context.Steps[i].StepIndex = i;

        steps = context.Steps;
        reservations = context.Reservations;
        nodeCount = steps.Sum(step => GetNodeCount(step.Pattern) * Math.Max(1, step.RequestedBatches));
        message = $"已规划 {steps.Count:N0} 步。";
        return true;
    }

    private bool PlanPatternOutputNeed(PlanContext context, PatternData pattern, NetworkItemRequest targetOutput, int count, int depth)
    {
        if (targetOutput.QualifiedItemId is null)
        {
            context.MissingLines.Add($"{pattern.DisplayName}：目标样板没有具体产物");
            return false;
        }

        var targetRequest = CreatePatternOutputRequest(pattern, targetOutput, count);
        var available = this.GetVirtualRequestCount(context, targetRequest);
        if (available >= count)
        {
            context.RequestAvailable[GetRequestIdentityKey(targetRequest)] = available - count;
            this.AddReservation(context, targetRequest, count);
            return true;
        }

        context.RequestAvailable[GetRequestIdentityKey(targetRequest)] = 0;
        var deficit = count - available;
        if (available > 0)
            this.AddReservation(context, targetRequest, available);

        var outputPerBatch = pattern.Outputs
            .Where(output => SameRequest(CreatePatternOutputRequest(pattern, output, 1), CreatePatternOutputRequest(pattern, targetOutput, 1)))
            .Sum(output => Math.Max(1, output.Count));
        if (outputPerBatch <= 0)
        {
            context.MissingLines.Add($"{targetOutput.QualifiedItemId}：样板没有可用产物");
            return false;
        }

        var batches = (int)Math.Ceiling(deficit / (double)outputPerBatch);
        foreach (var input in pattern.Inputs)
        {
            var needed = Math.Max(1, input.Count) * batches;
            if (!this.PlanRequestNeed(context, input, needed, depth + 1))
                return false;
        }

        context.Steps.Add(new CraftingJobStep
        {
            StepIndex = context.Steps.Count,
            Pattern = ClonePattern(pattern),
            RequestedBatches = batches,
            CompletedBatches = 0,
            State = CraftingJobState.Planning
        });

        foreach (var output in pattern.Outputs.Where(output => !string.IsNullOrWhiteSpace(output.QualifiedItemId)))
        {
            var producedRequest = CreatePatternOutputRequest(pattern, output, 1);
            var producedKey = GetRequestIdentityKey(producedRequest);
            var producedCount = Math.Max(1, output.Count) * batches;
            context.RequestAvailable[producedKey] = this.GetVirtualRequestCount(context, producedRequest) + producedCount;

            var outputId = output.QualifiedItemId!;
            context.ConcreteAvailable[outputId] = this.GetVirtualConcreteCount(context, outputId) + producedCount;
        }

        var producedAvailable = this.GetVirtualRequestCount(context, targetRequest);
        if (producedAvailable < deficit)
        {
            context.MissingLines.Add($"{targetOutput.QualifiedItemId}：规划产物不足 {producedAvailable:N0}/{deficit:N0}");
            return false;
        }

        this.AddReservation(context, targetRequest, deficit);
        context.RequestAvailable[GetRequestIdentityKey(targetRequest)] = producedAvailable - deficit;
        return true;
    }

    private bool PlanConcreteNeed(PlanContext context, string qualifiedItemId, int count, int depth)
    {
        if (depth > 32)
        {
            context.MissingLines.Add($"{qualifiedItemId}：依赖链过深");
            return false;
        }

        var available = this.GetVirtualConcreteCount(context, qualifiedItemId);
        if (available >= count)
        {
            context.ConcreteAvailable[qualifiedItemId] = available - count;
            this.AddReservation(context, new NetworkItemRequest { QualifiedItemId = qualifiedItemId, Count = count }, count);
            return true;
        }

        context.ConcreteAvailable[qualifiedItemId] = 0;
        var deficit = count - available;
        if (available > 0)
            this.AddReservation(context, new NetworkItemRequest { QualifiedItemId = qualifiedItemId, Count = available }, available);

        if (!context.PatternsByOutput.TryGetValue(qualifiedItemId, out var pattern))
        {
            context.MissingLines.Add($"{qualifiedItemId}：缺少 {deficit:N0}");
            return false;
        }

        var outputPerBatch = pattern.Outputs
            .Where(output => output.QualifiedItemId == qualifiedItemId)
            .Sum(output => Math.Max(1, output.Count));
        if (outputPerBatch <= 0)
        {
            context.MissingLines.Add($"{qualifiedItemId}：样板没有可用产物");
            return false;
        }

        var batches = (int)Math.Ceiling(deficit / (double)outputPerBatch);
        foreach (var input in pattern.Inputs)
        {
            var needed = Math.Max(1, input.Count) * batches;
            if (!this.PlanRequestNeed(context, input, needed, depth + 1))
                return false;
        }

        context.Steps.Add(new CraftingJobStep
        {
            StepIndex = context.Steps.Count,
            Pattern = ClonePattern(pattern),
            RequestedBatches = batches,
            CompletedBatches = 0,
            State = CraftingJobState.Planning
        });

        foreach (var output in pattern.Outputs.Where(output => !string.IsNullOrWhiteSpace(output.QualifiedItemId)))
        {
            var outputId = output.QualifiedItemId!;
            context.ConcreteAvailable[outputId] = this.GetVirtualConcreteCount(context, outputId) + Math.Max(1, output.Count) * batches;
        }

        var producedAvailable = this.GetVirtualConcreteCount(context, qualifiedItemId);
        if (producedAvailable < deficit)
        {
            context.MissingLines.Add($"{qualifiedItemId}：规划产物不足 {producedAvailable:N0}/{deficit:N0}");
            return false;
        }

        this.AddReservation(context, new NetworkItemRequest { QualifiedItemId = qualifiedItemId, Count = deficit }, deficit);
        context.ConcreteAvailable[qualifiedItemId] = producedAvailable - deficit;
        return true;
    }

    private bool PlanRequestNeed(PlanContext context, NetworkItemRequest request, int count, int depth)
    {
        if (HasSpecificRequestIdentity(request))
            return this.PlanSpecificRequestNeed(context, request, count, depth);

        if (!string.IsNullOrWhiteSpace(request.QualifiedItemId))
            return this.PlanConcreteNeed(context, request.QualifiedItemId!, count, depth);

        if (request.Category is null)
        {
            context.MissingLines.Add($"未知输入：缺少 {count:N0}");
            return false;
        }

        var available = this.GetVirtualCategoryCount(context, request.Category.Value);
        if (available >= count)
        {
            context.CategoryAvailable[request.Category.Value] = available - count;
            this.AddReservation(context, new NetworkItemRequest { Category = request.Category.Value, Count = count }, count);
            return true;
        }

        context.CategoryAvailable[request.Category.Value] = 0;
        if (available > 0)
            this.AddReservation(context, new NetworkItemRequest { Category = request.Category.Value, Count = available }, available);

        context.MissingLines.Add($"分类 {request.Category.Value}：缺少 {count - available:N0}");
        return false;
    }

    private bool PlanSpecificRequestNeed(PlanContext context, NetworkItemRequest request, int count, int depth)
    {
        if (depth > 32)
        {
            context.MissingLines.Add($"{request.DisplayKey}：依赖链过深");
            return false;
        }

        var available = this.GetVirtualRequestCount(context, request);
        if (available >= count)
        {
            context.RequestAvailable[GetRequestIdentityKey(request)] = available - count;
            this.AddReservation(context, request, count);
            return true;
        }

        if (!this.TryFindPatternOutputForRequest(context, request, out var pattern, out var output))
        {
            context.MissingLines.Add($"{request.DisplayKey}：缺少 {count - available:N0}");
            return false;
        }

        return this.PlanPatternOutputNeed(context, pattern, output, count, depth);
    }

    private bool TryFindPatternOutputForRequest(PlanContext context, NetworkItemRequest request, out PatternData pattern, out NetworkItemRequest output)
    {
        foreach (var candidate in context.Patterns)
        {
            foreach (var candidateOutput in candidate.Outputs.Where(output => !string.IsNullOrWhiteSpace(output.QualifiedItemId)))
            {
                var candidateRequest = CreatePatternOutputRequest(candidate, candidateOutput, 1);
                if (!SameRequest(candidateRequest, CloneRequest(request, 1)))
                    continue;

                pattern = candidate;
                output = candidateOutput;
                return true;
            }
        }

        pattern = new PatternData();
        output = new NetworkItemRequest();
        return false;
    }

    private void AddReservation(PlanContext context, NetworkItemRequest request, int count)
    {
        if (count <= 0)
            return;

        var existing = context.Reservations.FirstOrDefault(candidate => SameRequest(candidate.Request, request));
        if (existing is null)
        {
            context.Reservations.Add(new CraftingReservation
            {
                Request = CloneRequest(request, count),
                Count = count,
                ConsumedCount = 0
            });
            return;
        }

        existing.Count += count;
        existing.Request.Count = existing.Count;
    }

    private int GetVirtualConcreteCount(PlanContext context, string qualifiedItemId)
    {
        if (!context.ConcreteAvailable.TryGetValue(qualifiedItemId, out var available))
        {
            available = this.transactionService.GetAvailableCount(
                context.Network,
                new NetworkItemRequest { QualifiedItemId = qualifiedItemId, Count = 1 },
                autoConsumableOnly: true);
            available = Math.Max(0, available - this.GetOpenReservedCount(context.Network, new NetworkItemRequest { QualifiedItemId = qualifiedItemId, Count = 1 }, context.PlanningJobId));
            context.ConcreteAvailable[qualifiedItemId] = available;
        }

        return available;
    }

    private int GetVirtualRequestCount(PlanContext context, NetworkItemRequest request)
    {
        var key = GetRequestIdentityKey(request);
        if (!context.RequestAvailable.TryGetValue(key, out var available))
        {
            available = this.transactionService.GetAvailableCount(
                context.Network,
                request,
                autoConsumableOnly: true);
            available = Math.Max(0, available - this.GetOpenReservedCount(context.Network, request, context.PlanningJobId));
            context.RequestAvailable[key] = available;
        }

        return available;
    }

    private int GetVirtualCategoryCount(PlanContext context, int category)
    {
        if (!context.CategoryAvailable.TryGetValue(category, out var available))
        {
            available = this.transactionService.GetAvailableCount(
                context.Network,
                new NetworkItemRequest { Category = category, Count = 1 },
                autoConsumableOnly: true);
            available = Math.Max(0, available - this.GetOpenReservedCount(context.Network, new NetworkItemRequest { Category = category, Count = 1 }, context.PlanningJobId));
            context.CategoryAvailable[category] = available;
        }

        return available;
    }

    private int GetOpenReservedCount(NetworkData network, NetworkItemRequest request, Guid excludedJobId)
    {
        return network.Jobs
            .Where(job => job.JobId != excludedJobId && IsOpenState(job.State))
            .SelectMany(job => job.Reservations)
            .Where(reservation => RequestsOverlap(reservation.Request, request))
            .Sum(reservation => Math.Max(0, reservation.Count - reservation.ConsumedCount));
    }

    private CraftingJobStep? GetCurrentStep(CraftingJob job)
    {
        if (job.Steps.Count == 0 && job.Pattern.Outputs.Count > 0)
        {
            job.Steps.Add(new CraftingJobStep
            {
                StepIndex = 0,
                Pattern = ClonePattern(job.Pattern),
                RequestedBatches = Math.Max(1, job.RequestedCount),
                CompletedBatches = Math.Max(0, job.CompletedCount),
                State = job.State
            });
            job.CurrentStepIndex = 0;
        }

        if (job.CurrentStepIndex < 0)
            job.CurrentStepIndex = 0;

        while (job.CurrentStepIndex < job.Steps.Count
            && job.Steps[job.CurrentStepIndex].CompletedBatches >= job.Steps[job.CurrentStepIndex].RequestedBatches)
        {
            job.Steps[job.CurrentStepIndex].State = CraftingJobState.Completed;
            job.CurrentStepIndex++;
        }

        return job.CurrentStepIndex >= 0 && job.CurrentStepIndex < job.Steps.Count
            ? job.Steps[job.CurrentStepIndex]
            : null;
    }

    private bool AdvanceStep(CraftingJob job, CraftingJobStep step)
    {
        step.State = CraftingJobState.Completed;
        this.SyncJobProgress(job);
        job.WaitingMachineLocationName = string.Empty;
        job.WaitingMachineTileX = 0;
        job.WaitingMachineTileY = 0;
        job.CurrentStepIndex = Math.Max(job.CurrentStepIndex, step.StepIndex + 1);

        if (job.CurrentStepIndex >= job.Steps.Count)
            return this.CompleteJob(job);

        var next = job.Steps[job.CurrentStepIndex];
        job.State = CraftingJobState.Running;
        next.State = CraftingJobState.Running;
        job.StatusMessage = $"正在运行步骤 {next.StepIndex + 1:N0}/{job.Steps.Count:N0}：{next.Pattern.DisplayName}。";
        return true;
    }

    private void SyncJobProgress(CraftingJob job)
    {
        job.CompletedCount = job.Steps.Count == 0
            ? job.CompletedCount
            : job.Steps.Sum(step => Math.Min(step.CompletedBatches, step.RequestedBatches));
        job.RequestedCount = job.Steps.Count == 0
            ? job.RequestedCount
            : job.Steps.Sum(step => step.RequestedBatches);
    }

    private bool TryExtractInputs(
        NetworkData network,
        IReadOnlyList<NetworkItemRequest> requests,
        Guid jobId,
        MachineTarget fallbackTarget,
        out List<Item> extractedItems,
        out string message)
    {
        extractedItems = new List<Item>();
        if (!this.transactionService.HasIngredients(network, requests, out var missingLines, jobId, autoConsumableOnly: true))
        {
            message = "缺少：" + string.Join(", ", missingLines);
            return false;
        }

        foreach (var request in requests)
        {
            if (!this.transactionService.TryExtractItem(network, request, request.Count, out var extracted, out message, jobId) || extracted is null)
            {
                this.ReturnExtractedInputs(network, extractedItems, fallbackTarget);
                extractedItems.Clear();
                return false;
            }

            if (extracted.Stack < request.Count)
            {
                extractedItems.Add(extracted);
                message = $"只抽取到 {request.DisplayKey} x{extracted.Stack:N0}/{request.Count:N0}。";
                this.ReturnExtractedInputs(network, extractedItems, fallbackTarget);
                extractedItems.Clear();
                return false;
            }

            extractedItems.Add(extracted);
        }

        message = "已抽取机器输入物。";
        return true;
    }

    private void ReturnExtractedInputs(NetworkData network, List<Item> extractedItems, MachineTarget fallbackTarget)
    {
        foreach (var item in extractedItems.Where(item => item is not null && item.Stack > 0).ToList())
        {
            if (this.transactionService.TryReturnItemToNetwork(network, item) && item.Stack <= 0)
                continue;

            this.monitor.Log($"Could not return extracted input {item.QualifiedItemId} x{item.Stack:N0}; dropping near the target machine.", LogLevel.Warn);
            this.DropLeftover(fallbackTarget, item);
        }
    }

    private static bool TryProbeSingleMachineInput(SObject machine, IReadOnlyList<NetworkItemRequest> requests, out string message)
    {
        message = string.Empty;
        if (requests.Count != 1)
            return true;

        var request = requests[0];
        Item probeItem;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.SerializedItemPrototype))
            {
                probeItem = SerializedItemCodec.CreateItem(request.SerializedItemPrototype, Math.Max(1, request.Count));
            }
            else if (!string.IsNullOrWhiteSpace(request.QualifiedItemId))
            {
                probeItem = ItemRegistry.Create(request.QualifiedItemId);
                probeItem.Stack = Math.Max(1, request.Count);
            }
            else
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            message = $"无法创建机器输入探测物品：{ex.Message}";
            return false;
        }

        try
        {
            if (machine.performObjectDropInAction(probeItem, true, Game1.player, false))
                return true;
        }
        catch (Exception ex)
        {
            message = $"机器输入探测失败：{ex.Message}";
            return false;
        }

        message = $"机器拒绝 {probeItem.DisplayName}。";
        return false;
    }

    private bool TryConsumeReservations(CraftingJob job, IReadOnlyList<NetworkItemRequest> requests, List<ReservationConsume> consumed, out string message)
    {
        message = "已消耗预留材料。";
        if (job.Reservations.Count == 0)
            return true;

        foreach (var request in requests)
        {
            var remaining = request.Count;
            foreach (var reservation in job.Reservations.Where(reservation => SameRequest(reservation.Request, request)))
            {
                if (remaining <= 0)
                    break;

                var available = Math.Max(0, reservation.Count - reservation.ConsumedCount);
                if (available <= 0)
                    continue;

                var take = Math.Min(remaining, available);
                reservation.ConsumedCount += take;
                consumed.Add(new ReservationConsume(reservation, take));
                remaining -= take;
            }

            if (remaining > 0)
            {
                this.RollbackReservationConsumption(consumed);
                message = $"预留材料不足：{request.DisplayKey} 需要 {request.Count:N0}，缺 {remaining:N0}。";
                return false;
            }
        }

        return true;
    }

    private void RollbackReservationConsumption(List<ReservationConsume> consumed)
    {
        foreach (var entry in consumed)
            entry.Reservation.ConsumedCount = Math.Max(0, entry.Reservation.ConsumedCount - entry.Count);

        consumed.Clear();
    }

    private void ReleaseUnconsumedReservations(CraftingJob job)
    {
        foreach (var reservation in job.Reservations)
            reservation.Count = Math.Min(reservation.Count, Math.Max(0, reservation.ConsumedCount));
    }

    private void ReturnScratchInventory(NetworkData network, Inventory scratchInventory, MachineTarget target)
    {
        foreach (var leftover in scratchInventory.Where(item => item is not null && item.Stack > 0).ToList())
        {
            if (leftover is null || leftover.Stack <= 0)
                continue;

            var before = leftover.Stack;
            if (this.transactionService.TryReturnItemToNetwork(network, leftover) && leftover.Stack <= 0)
                continue;

            this.monitor.Log($"Could not return {leftover.QualifiedItemId} x{before:N0} from a machine input bridge; dropping near the machine.", LogLevel.Warn);
            this.DropLeftover(target, leftover);
        }
    }

    private bool TryDepositWholeMachineOutput(
        NetworkData network,
        SObject machine,
        Item held,
        string noSpaceMessage,
        string changedMessage,
        out string message)
    {
        var expectedCount = Math.Max(0, held.Stack);
        if (expectedCount <= 0)
        {
            message = "机器产物堆叠为空。";
            return false;
        }

        var moving = held.getOne();
        moving.Stack = expectedCount;
        if (!this.transactionService.CanAcceptNetworkItem(network, moving, expectedCount))
        {
            message = noSpaceMessage;
            return false;
        }

        this.transactionService.TryDepositItem(network, moving, out var moved);
        if (moved >= expectedCount)
        {
            message = string.Empty;
            return true;
        }

        if (moved > 0
            && !this.TryRollbackPartialMachineOutputDeposit(network, held, moved, out var rolledBack, out var rollbackMessage))
        {
            var unrolled = Math.Max(0, moved - rolledBack);
            if (unrolled > 0)
                TrimMachineOutputAfterPartialDeposit(machine, held, unrolled);
            message = $"{changedMessage}；部分存入回滚不完整（{rollbackMessage}），机器产物减少了 {unrolled:N0}。";
            return false;
        }

        message = changedMessage;
        return false;
    }

    private bool TryRollbackPartialMachineOutputDeposit(NetworkData network, Item output, int count, out int rolledBack, out string message)
    {
        rolledBack = 0;
        try
        {
            var request = new NetworkItemRequest
            {
                QualifiedItemId = output.QualifiedItemId,
                SerializedItemPrototype = SerializedItemCodec.SerializePrototype(output.getOne()),
                Count = count
            };

            if (this.transactionService.TryExtractItem(network, request, count, out var extracted, out var extractMessage)
                && extracted is not null
                && extracted.Stack >= count)
            {
                rolledBack = extracted.Stack;
                message = "已回滚部分机器产物存入。";
                return true;
            }

            rolledBack = Math.Max(0, extracted?.Stack ?? 0);
            message = rolledBack > 0
                ? $"只回滚了 {rolledBack:N0}/{count:N0}；{extractMessage}"
                : extractMessage;
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        this.monitor.Log($"Could not roll back partial machine output deposit for {output.QualifiedItemId} x{count:N0}: {message}", LogLevel.Error);
        return false;
    }

    private static void TrimMachineOutputAfterPartialDeposit(SObject machine, Item held, int moved)
    {
        var remaining = held.Stack - Math.Max(0, moved);
        if (remaining <= 0)
        {
            MachineStateHelper.ResetAfterAutomatedCollect(machine);
            return;
        }

        held.Stack = remaining;
    }

    private List<Item>? CreateOutputItems(PatternData pattern, out string message)
    {
        var result = new List<Item>();
        foreach (var output in pattern.Outputs)
        {
            if (output.QualifiedItemId is null)
            {
                message = "样板产物必须是具体物品。";
                return null;
            }

            Item item;
            try
            {
                item = ItemRegistry.Create(output.QualifiedItemId);
            }
            catch (Exception ex)
            {
                message = $"无法创建产物 {output.QualifiedItemId}：{ex.Message}";
                return null;
            }

            item.Stack = Math.Max(1, output.Count);
            result.Add(item);
        }

        message = string.Empty;
        return result;
    }

    private bool CompleteJob(CraftingJob job)
    {
        foreach (var step in job.Steps)
        {
            step.CompletedBatches = Math.Max(step.CompletedBatches, step.RequestedBatches);
            step.State = CraftingJobState.Completed;
        }

        this.SyncJobProgress(job);
        job.State = CraftingJobState.Completed;
        job.AssignedCpuEndpointId = null;
        this.ReleaseUnconsumedReservations(job);
        job.StatusMessage = "已完成。";
        this.LogGameplay($"action=cpu_complete result=success job={ShortId(job.JobId)} pattern={Quote(job.Pattern.DisplayName)} requested={job.RequestedCount:N0} completed={job.CompletedCount:N0} steps={job.Steps.Count:N0}");
        return true;
    }

    private bool SetStatus(CraftingJob job, string status)
    {
        if (job.StatusMessage == status)
            return false;

        job.StatusMessage = status;
        return true;
    }

    private void ShowJobStatus(NetworkData network)
    {
        var active = network.Jobs
            .Where(job => job.State is CraftingJobState.Planning or CraftingJobState.Running or CraftingJobState.WaitingForMachine)
            .Take(3)
            .ToList();

        if (active.Count == 0)
        {
            Game1.addHUDMessage(new HUDMessage("没有活跃的 SVSAP CPU 作业。", HUDMessage.newQuest_type));
            return;
        }

        var summary = string.Join(" | ", active.Select(job => $"{job.Pattern.DisplayName}：{FormatJobState(job.State)} {job.CompletedCount}/{job.RequestedCount}"));
        Game1.addHUDMessage(new HUDMessage(summary, HUDMessage.newQuest_type));
    }

    private void DropLeftover(NetworkEndpoint endpoint, Item item)
    {
        if (item.Stack <= 0)
            return;

        var location = Game1.getLocationFromName(endpoint.LocationName) ?? Game1.currentLocation;
        if (location is null)
            return;

        Game1.createItemDebris(item, new Vector2(endpoint.TileX + 0.5f, endpoint.TileY + 0.5f) * Game1.tileSize, -1, location);
    }

    private void DropLeftover(MachineTarget target, Item item)
    {
        if (item.Stack <= 0)
            return;

        Game1.createItemDebris(item, (target.Tile + new Vector2(0.5f, 0.5f)) * Game1.tileSize, -1, target.Location);
    }

    private static int GetNodeCount(PatternData pattern)
    {
        return Math.Max(1, pattern.Inputs.Count + pattern.Outputs.Count);
    }

    private static NetworkItemRequest CloneRequest(NetworkItemRequest request, int count)
    {
        return new NetworkItemRequest
        {
            QualifiedItemId = request.QualifiedItemId,
            Category = request.Category,
            SerializedItemPrototype = request.SerializedItemPrototype,
            PreservedParentQualifiedItemId = request.PreservedParentQualifiedItemId,
            Count = count
        };
    }

    private static bool SameRequest(NetworkItemRequest left, NetworkItemRequest right)
    {
        if (!string.Equals(left.SerializedItemPrototype, right.SerializedItemPrototype, StringComparison.Ordinal))
            return false;

        if (!string.Equals(left.QualifiedItemId, right.QualifiedItemId, StringComparison.Ordinal))
            return false;

        if (left.Category != right.Category)
            return false;

        return ItemKeyFactory.NormalizeItemId(left.PreservedParentQualifiedItemId) == ItemKeyFactory.NormalizeItemId(right.PreservedParentQualifiedItemId);
    }

    private static bool HasSpecificRequestIdentity(NetworkItemRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.SerializedItemPrototype)
            || !string.IsNullOrWhiteSpace(request.PreservedParentQualifiedItemId);
    }

    private static bool RequestsOverlap(NetworkItemRequest left, NetworkItemRequest right)
    {
        var leftHasSerialized = !string.IsNullOrWhiteSpace(left.SerializedItemPrototype);
        var rightHasSerialized = !string.IsNullOrWhiteSpace(right.SerializedItemPrototype);
        if (leftHasSerialized && rightHasSerialized
            && !string.Equals(left.SerializedItemPrototype, right.SerializedItemPrototype, StringComparison.Ordinal))
        {
            return false;
        }

        var leftHasQualifiedId = !string.IsNullOrWhiteSpace(left.QualifiedItemId);
        var rightHasQualifiedId = !string.IsNullOrWhiteSpace(right.QualifiedItemId);
        if (leftHasQualifiedId && rightHasQualifiedId
            && !string.Equals(left.QualifiedItemId, right.QualifiedItemId, StringComparison.Ordinal))
        {
            return false;
        }

        if (left.Category is not null && right.Category is not null && left.Category.Value != right.Category.Value)
            return false;

        var leftParent = ItemKeyFactory.NormalizeItemId(left.PreservedParentQualifiedItemId);
        var rightParent = ItemKeyFactory.NormalizeItemId(right.PreservedParentQualifiedItemId);
        if (leftParent.Length > 0 && rightParent.Length > 0 && leftParent != rightParent)
            return false;

        return (leftHasSerialized && rightHasSerialized)
            || (leftHasQualifiedId && rightHasQualifiedId)
            || (left.Category is not null && right.Category is not null);
    }

    private static bool IsLongRunning(PatternData pattern, ModConfig config)
    {
        return pattern.Kind == PatternKind.Processing
            && pattern.ProcessingMinutes > Math.Max(0, config.LongJobThresholdMinutes);
    }

    private static bool IsCaskAgeable(Item item)
    {
        return CaskAgeableQualifiedItemIds.Contains(item.QualifiedItemId)
            && item.Stack > 0;
    }

    private static int NormalizeCaskTargetQuality(int value)
    {
        if (value >= 4)
            return 4;

        if (value >= 2)
            return 2;

        if (value >= 1)
            return 1;

        return 0;
    }

    private static int GetMaxProcessingMinutes(IEnumerable<CraftingJobStep> steps)
    {
        return steps
            .Where(step => step.Pattern.Kind == PatternKind.Processing)
            .Select(step => Math.Max(0, step.Pattern.ProcessingMinutes))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static int GetMachineRemainingMinutes(SObject machine, PatternData pattern)
    {
        var remaining = Math.Max(0, machine.MinutesUntilReady);
        return remaining > 0
            ? remaining
            : Math.Max(0, pattern.ProcessingMinutes);
    }

    private static string FormatRemainingProcessingTime(int minutes)
    {
        return minutes > 0
            ? "~" + FormatProcessingDuration(minutes)
            : "即将完成";
    }

    private static string FormatProcessingDuration(int minutes)
    {
        var safeMinutes = Math.Max(0, minutes);
        if (safeMinutes >= 1600)
            return $"{Math.Ceiling(safeMinutes / 1600d):N0} 天";

        if (safeMinutes >= 60)
            return $"{Math.Ceiling(safeMinutes / 60d):N0} 小时";

        return $"{safeMinutes:N0} 分钟";
    }

    private static string FormatJobState(CraftingJobState state)
    {
        return state switch
        {
            CraftingJobState.Planning => "规划中",
            CraftingJobState.MissingItems => "缺材料",
            CraftingJobState.Reserved => "已预留",
            CraftingJobState.Running => "运行中",
            CraftingJobState.WaitingForMachine => "等机器",
            CraftingJobState.WaitingForOutput => "等产出",
            CraftingJobState.Completed => "已完成",
            CraftingJobState.Cancelled => "已取消",
            CraftingJobState.Failed => "失败",
            _ => state.ToString()
        };
    }

    private static bool IsOpenState(CraftingJobState state)
    {
        return state is CraftingJobState.Planning
            or CraftingJobState.MissingItems
            or CraftingJobState.Reserved
            or CraftingJobState.Running
            or CraftingJobState.WaitingForMachine
            or CraftingJobState.WaitingForOutput;
    }

    private static bool IsCpuSlotState(CraftingJobState state)
    {
        return state is CraftingJobState.Reserved
            or CraftingJobState.Running
            or CraftingJobState.WaitingForMachine
            or CraftingJobState.WaitingForOutput;
    }

    private static PatternData ClonePattern(PatternData pattern)
    {
        var raw = JsonSerializer.Serialize(pattern);
        return JsonSerializer.Deserialize<PatternData>(raw) ?? new PatternData();
    }

    private void LogGameplay(string message)
    {
        if (this.getConfig().DetailedGameplayLogs)
            this.monitor.Log("SVSAP_GAMELOG " + message, LogLevel.Info);
    }

    private static string ShortId(Guid id)
    {
        var raw = id.ToString("N");
        return raw.Length <= 8 ? raw : raw[..8];
    }

    private static string Quote(string? value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private sealed record MachineTarget(GameLocation Location, Vector2 Tile, SObject Machine);

    private sealed record ReservationConsume(CraftingReservation Reservation, int Count);

    private sealed class PlanContext
    {
        public PlanContext(NetworkData network, IReadOnlyList<PatternData> patterns, Dictionary<string, PatternData> patternsByOutput, Guid planningJobId)
        {
            this.Network = network;
            this.Patterns = patterns;
            this.PatternsByOutput = patternsByOutput;
            this.PlanningJobId = planningJobId;
        }

        public NetworkData Network { get; }
        public IReadOnlyList<PatternData> Patterns { get; }
        public Dictionary<string, PatternData> PatternsByOutput { get; }
        public Guid PlanningJobId { get; }
        public Dictionary<string, int> RequestAvailable { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ConcreteAvailable { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, int> CategoryAvailable { get; } = new();
        public List<CraftingJobStep> Steps { get; } = new();
        public List<CraftingReservation> Reservations { get; } = new();
        public List<string> MissingLines { get; } = new();
    }
}
