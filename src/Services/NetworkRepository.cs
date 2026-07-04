using SVSAP.Models;
using StardewModdingAPI;

namespace SVSAP.Services;

internal sealed class NetworkRepository
{
    private const string SaveKey = "networks";

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
        this.data = this.helper.Data.ReadSaveData<NetworkSaveData>(SaveKey) ?? new NetworkSaveData();
        this.monitor.Log($"Loaded {this.data.Networks.Count} SVSAP network(s) and {this.data.PendingTransactions.Count} pending transaction(s).", LogLevel.Trace);
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
