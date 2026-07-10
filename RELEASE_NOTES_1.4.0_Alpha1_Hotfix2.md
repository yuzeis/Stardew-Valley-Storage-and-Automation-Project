# SVSAP / SVSAPME 1.4.0-alpha.1.2

Reliability and interface update for the 1.4 Alpha test line.

This update is intended for the next round of in-game testing. It improves multiplayer item safety, prevents one player's terminal actions from changing another player's page or crafting options, reduces repeated GUI work, and corrects locked-plot planting in single-block farms.

## Fixed

### SVSAP

- Remote terminal deposits and inserted structural items are now kept in durable client escrow until the host confirms the final result.
- Interrupted remote actions replay the same transaction after reconnect instead of silently losing or immediately duplicating the held item.
- Remote terminal push updates no longer replace another player's current page.
- Remote crafting updates no longer replace another player's batch count or quality strategy.
- Storage Drive and Pattern Provider menus now cache expensive slot views and refresh immediately after an interaction.

### SVSAPME

- Pending farmhand machine inputs are reconciled with the host after reconnect instead of being returned unconditionally.
- Host-side machine action exceptions now return a failure response, so the client is not left permanently waiting.
- Pending machine actions use bounded timeout retries and retain their escrowed item while reconciliation is unresolved.
- Single-block farms now reserve matching seeds for locked plots before filling unlocked plots.
- Production-path self-tests now cover mixed crops, locked plots, regrowth, and harvest output.

## Requirements

- Stardew Valley 1.6.15 or later.
- SMAPI 4.5.2 or later.
- SVSAPME requires SVSAP 1.4.0-alpha.1.2 or later.

## Installation

Install or update SVSAP first, then install or update SVSAPME. Replace both mod folders so every multiplayer participant uses the same version.

This remains an alpha test release. Back up important saves before testing.

---

# SVSAP / SVSAPME 1.4.0-alpha.1.2

这是面向 1.4 Alpha 测试线的可靠性与界面修复更新。

本次更新用于下一轮实机测试，重点改进多人联机物品安全、终端多人状态隔离、GUI 性能，以及单方块农场锁定地块的补种逻辑。

## 修复内容

### SVSAP

- 远程终端存入物品和远程结构操作使用持久化暂存，直至主机返回最终结果。
- 操作被断线打断后会使用同一事务重新向主机核对，不再直接丢失或无条件返还物品。
- 其他玩家的终端操作不会再改变当前玩家所在的物品页。
- 其他玩家的合成操作不会再覆盖当前玩家选择的批量数量和品质策略。
- 存储驱动器与样板供应器界面会缓存高开销槽位数据，并在实际操作后立即刷新。

### SVSAPME

- 农场工人断线重连后，待处理的机器输入会先与主机核对，不再无条件返还。
- 主机处理机器操作发生异常时会明确返回失败结果，避免客户端永久卡在等待状态。
- 待处理机器操作加入有限次数的超时重试；核对完成前，相关物品会继续安全保留。
- 单方块农场会优先为锁定地块保留并使用匹配种子，再填充未锁定地块。
- 自测新增混种、锁定地块、再生作物和收获输出的生产路径覆盖。

## 运行要求

- 《星露谷物语》1.6.15 或更高版本。
- SMAPI 4.5.2 或更高版本。
- SVSAPME 需要 SVSAP 1.4.0-alpha.1.2 或更高版本。

## 安装说明

请先安装或更新 SVSAP，再安装或更新 SVSAPME。请同时替换两个 mod 文件夹，并确保所有联机玩家使用完全相同的版本。

本版本仍属于 Alpha 测试版本，建议在重要存档中使用前先备份存档。
