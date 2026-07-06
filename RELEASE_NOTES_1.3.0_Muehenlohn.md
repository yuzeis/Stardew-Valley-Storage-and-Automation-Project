# SVSAP / SVSAPME 1.3.0 "Mühenlohn"

## 中文说明

这是 SVSAP 与 SVSAPME 的 1.3.0 正式发布版，版本代号 `Mühenlohn`。本次发布重点收束多人事务、远程终端、GUI 体验、存储元件防复制、SVSAPME 机器生命周期与完整 E2E 验证。

### 更新内容

- 修复 farmhand 远程终端存入/取出链路：客户端托管真实物品 payload，主机只处理托管 payload，取出由主机返回序列化物品后客户端本地入包或掉落。
- 修复远程终端、远程合成、远程监控 GUI：分页快照、本地化渲染、pending 状态、Stardew TextBox 搜索输入。
- 修复 SVSAP 存储元件防复制/防丢失守卫：非空/有状态存储元件不可堆叠合并，不允许普通网络存入导致内容湮灭或复制。
- 修复 SVSAPME 带状态机器的多人托管、持有、消耗、回收与拆屋链路。
- 明确拆卸能量规则：除 Energy Cell 外，机器拆卸后内部能量清零；Energy Cell 保留电量。
- 改进 host-authoritative 协议、动作幂等、失败返还、host 离线降级与多人读数同步。
- 完善 i18n：中文/英文键值 parity，避免 host 语言直接泄漏到 farmhand HUD。
- 扩展自测与 E2E：SVSAP selftest 28/28，SVSAPME selftest 35/35，FullMatrix 45/45，P0P1 single/multi 与 SVSAP RouteA multi 均通过。

### 安装

1. 安装 Stardew Valley 1.6.15 与 SMAPI 4.5.2 或更新版本。
2. 安装 `SVSAP 1.3.0.zip`。
3. 如果需要机器与能源扩展，再安装 `SVSAPME 1.3.0.zip`。
4. 多人游戏中主机与所有 farmhand 必须安装相同版本的 SVSAP/SVSAPME。

### 注意

- 从测试版或旧版升级前建议备份存档。
- ID 82 旧版本升级存档仍需要真实旧存档样本继续扩大覆盖；当前没有已证实的待修 mod 本体漏洞。
- 文件名使用 `Muehenlohn` 是为了兼容部分站点和压缩工具；展示名称仍为 `Mühenlohn`。

## English Notes

This is the 1.3.0 stable release of SVSAP and SVSAPME, code-named `Mühenlohn`. This release focuses on multiplayer transaction safety, remote terminal behavior, GUI polish, storage-cell anti-dupe guards, SVSAPME machine lifecycle handling, and expanded E2E validation.

### Changes

- Fixed farmhand remote terminal deposit/withdraw flows: clients escrow the real serialized item payload, hosts process only that payload, and withdrawals return serialized response items for local client delivery.
- Improved remote terminal, remote crafting, and remote monitor GUIs with paged snapshots, local-language rendering, pending indicators, and Stardew TextBox search input.
- Fixed SVSAP storage-cell anti-dupe/data-loss guards: stateful storage cells cannot be stacked or merged, and normal network insertion rejects them.
- Fixed SVSAPME multiplayer escrow, held/consumed state, reclaim, and building-demolish chains for stateful machines.
- Defined disassembly energy policy: non-cell machines lose internal energy when disassembled; Energy Cells keep their stored energy.
- Improved host-authoritative protocol behavior, action idempotency, failure refunds, host-offline fallback, and multiplayer energy read synchronization.
- Completed i18n parity for Chinese and English; farmhands no longer display host-localized HUD strings on reviewed remote paths.
- Expanded automated validation: SVSAP selftest 28/28, SVSAPME selftest 35/35, FullMatrix 45/45, P0P1 single/multi, and SVSAP RouteA multi all pass.

### Installation

1. Install Stardew Valley 1.6.15 and SMAPI 4.5.2 or newer.
2. Install `SVSAP 1.3.0.zip`.
3. Install `SVSAPME 1.3.0.zip` if you want the machine and energy extension.
4. In multiplayer, the host and every farmhand must use the same SVSAP/SVSAPME versions.

### Notes

- Back up your save before upgrading from older or test versions.
- Old-version save migration still needs more real legacy save samples; there is currently no confirmed remaining mod-body `NEED REPAIR` finding.
- Release filenames use `Muehenlohn` for site/tool compatibility; the display codename remains `Mühenlohn`.
