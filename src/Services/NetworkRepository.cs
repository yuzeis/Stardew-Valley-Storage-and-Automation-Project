using SVSAP.Models;
using StardewModdingAPI;

namespace SVSAP.Services;

internal sealed class NetworkRepository
{
    private const string SaveKey = "networks";
    private const int CurrentSchemaVersion = 3;

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private NetworkSaveData data = new();

    public NetworkRepository(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
    }

    public NetworkSaveData Data => this.data;

    public void Load()
    {
        this.data = this.helper.Data.ReadSaveData<NetworkSaveData>(SaveKey) ?? CreateNewSaveData();
        var migrated = this.NormalizeLoadedData(this.data);
        this.monitor.Log($"Loaded {this.data.Networks.Count} SVSAP network(s), {this.data.PendingTransactions.Count} pending transaction(s), and {this.data.PendingRemoteDeliveries.Count} pending remote delivery item(s).", LogLevel.Trace);
        if (migrated)
            this.Save();
    }

    public void Save()
    {
        // Network data is host-authoritative. Farmhands never persist local copies,
        // and startup/self-test paths can run before a save is loaded.
        if (!Context.IsMainPlayer || !Context.IsWorldReady)
            return;

        this.helper.Data.WriteSaveData(SaveKey, this.data);
    }

    public NetworkData GetOrCreateNetwork(Guid networkId)
    {
        if (!this.data.Networks.TryGetValue(networkId, out var network))
        {
            network = new NetworkData
            {
                NetworkId = networkId,
                Name = ModText.Format("network.defaultName", this.data.Networks.Count + 1)
            };

            this.data.Networks[networkId] = network;
        }

        return network;
    }

    public bool TryGetNetwork(Guid networkId, out NetworkData network)
    {
        return this.data.Networks.TryGetValue(networkId, out network!);
    }

    private static NetworkSaveData CreateNewSaveData()
    {
        return new NetworkSaveData
        {
            SchemaVersion = CurrentSchemaVersion
        };
    }

    private bool NormalizeLoadedData(NetworkSaveData loaded)
    {
        var changed = false;
        if (loaded.SchemaVersion <= 0)
        {
            loaded.SchemaVersion = 1;
            changed = true;
        }

        loaded.Networks ??= new();
        loaded.PendingTransactions ??= new();
        loaded.PendingRemoteDeliveries ??= new();
        loaded.ExecutedTerminalDeposits ??= new();
        loaded.ExecutedStructuralConsumptions ??= new();
        changed |= ExecutedRemoteActionLedger.NormalizeTerminal(loaded.ExecutedTerminalDeposits);
        changed |= ExecutedRemoteActionLedger.NormalizeStructural(loaded.ExecutedStructuralConsumptions);

        // Chests are storage targets of an adjacent Storage Interface, not
        // wireless endpoints of their own. Remove legacy direct links so one
        // network can no longer expose every chest that was linked on the map.
        foreach (var network in loaded.Networks.Values)
        {
            network.Endpoints ??= new();
            if (network.Endpoints.RemoveAll(endpoint => endpoint.Type == EndpointType.Chest) > 0)
                changed = true;
        }

        if (loaded.SchemaVersion < CurrentSchemaVersion)
        {
            loaded.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }

        var removedRemoteDeliveries = loaded.PendingRemoteDeliveries.RemoveAll(delivery =>
            delivery.DeliveryId == Guid.Empty
            || delivery.PlayerId <= 0
            || delivery.TransactionId == Guid.Empty
            || string.IsNullOrWhiteSpace(delivery.ReturnedSerializedItem)
            || delivery.ReturnedCount <= 0);
        if (removedRemoteDeliveries > 0)
            changed = true;

        return changed;
    }

    public void UpsertEndpoint(Guid networkId, NetworkEndpoint endpoint)
    {
        var network = this.GetOrCreateNetwork(networkId);
        var index = network.Endpoints.FindIndex(entry => entry.EndpointId == endpoint.EndpointId);
        if (index >= 0)
            network.Endpoints[index] = endpoint;
        else
            network.Endpoints.Add(endpoint);

        this.Save();
    }
}
