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
        // 网络数据是主机权威(数据存在服务端)。客机永不落盘:
        // 在客机上 WriteSaveData 会抛异常或只写本地缓存,导致客机内存与主机不一致。
        // 客机的一切结构性写操作必须通过 NetworkInteractionService 的 host action 请求由主机执行。
        // 自测/启动阶段还没有载入存档,也不能调用 WriteSaveData。
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
                Name = $"网络 {this.data.Networks.Count + 1}"
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
