# SVSAP / SVSAPME Ver1.4.1-rc1.0

Internal version: `1.4.1-rc1.0`

This is the first release candidate for the 1.4.1 line. It is intended for broad player testing before the final release. Back up important saves before updating, and make sure every multiplayer participant uses the same SVSAP and SVSAPME versions.

## Highlights

- Rebuilt the custom menus around a consistent Stardew Valley and AE2-inspired layout, with compact red/yellow/green status indicators, corrected slot alignment, vanilla-style item icons and quality stars, and K/M abbreviations only for large stack counts.
- Improved network terminal, storage drive, transfer bus, crafting terminal, confirmation, and monitoring workflows. Crafting now presents expected materials before confirmation, including missing quantities for the selected batch size.
- Restricted storage interfaces and chest readers to their intended adjacent container instead of scanning unrelated containers across the map.
- Completed practical 3x3 filters, direction handling, and vanilla chest interaction for importers and exporters.
- Added durable multiplayer action reconciliation, idempotent request handling, authoritative chest locking, and safer reconnect recovery for remote inventory and machine actions.
- Reduced repeated terminal, storage, and machine-menu calculations through bounded caches and invalidation on relevant state changes.

## SVSAPME Machines

- Completed single-block farm, keg, and cask consoles with real input, output, and upgrade slots; independent working slots; per-slot progress and ETA; network auto-input and auto-output; filters; and energy/economic summaries.
- Single-block processors accept different valid inputs at different times and retain each slot's recipe and progress independently.
- Single-block farms support mixed crops with independent growth schedules, plot locks, manual planting and harvesting, single-plot uprooting, and clear-all confirmation. Uprooting and clearing do not refund seeds, fertilizer, crops, or elapsed growth.
- Added processor speed, capacity, and quality upgrade handling, plus clearer energy storage, generation, consumption, and live power-state displays.
- Corrected coffee input batching, quality-card auto-pull behavior, malformed buffer recovery, and overnight processor accounting.

## Compatibility And Installation

- Requires Stardew Valley 1.6.15 or later and SMAPI 4.5.2 or later.
- SVSAPME requires SVSAP `1.4.1-rc1.0` or later.
- Remove the old `SVSAP` and `SVSAPME` folders before installing the new folders. Do not merge release files into an older installation.
- Keep personal `config.json` files only if you want to preserve existing settings.
- In multiplayer, update the host and every farmhand before loading the save.

## Release Candidate Notice

This is not the final 1.4.1 release. Preserve a backup of important saves and report reproducible issues with the SMAPI log, single-player or multiplayer role, affected machine or menu, and the exact action sequence.

---

# SVSAP / SVSAPME Ver1.4.1-rc1.0

内部版本：`1.4.1-rc1.0`

这是 1.4.1 版本线的首个发布候选版，用于正式版前的广泛实机测试。更新前请备份重要存档，并确保所有联机玩家使用完全相同的 SVSAP 与 SVSAPME 版本。

## 主要改进

- 统一重构全部自定义 GUI，采用星露谷原版框架与 AE2 式功能布局；加入紧凑的红黄绿状态灯，修正槽位与物品图标错位，恢复原版品质星显示，并仅对大数量使用 K/M 缩写。
- 改进网络终端、存储驱动器、传输总线、合成终端、合成确认框与监视器。合成开始前会按照所选批量显示全部预期材料及缺失数量。
- 存储接口与箱子读取逻辑只处理预期的相邻容器，不再错误扫描地图上的无关箱子。
- 完成导入器与导出器的 3x3 过滤、方向定义和原版箱子交互。
- 为多人远程物品与机器操作补齐持久化对账、幂等请求、主机权威箱锁与重连恢复，降低丢物和重复结算风险。
- 对终端、存储和机器菜单加入有界缓存，并在相关状态变化时失效，减少大网络下的重复计算。

## SVSAPME 机器

- 完成单方块农场、酒桶与陈酿桶控制台：真实输入、输出和升级槽；独立工作格；单格进度与 ETA；网络自动输入输出；过滤；能源与经济统计。
- 单方块处理机可以在不同时间接收不同的合法原料，每个工作格独立保存配方与进度。
- 单方块农场支持混种、独立生长周期、地块锁定、手动播种与收获、单格铲除及清空全部确认。铲除和清空不会返还种子、肥料、作物或已累计生长进度。
- 加入处理机速度、容量和品质升级，并优化储能、发电、耗电与实时供电状态显示。
- 修复咖啡批量输入、品质卡自动拉取、畸形缓冲恢复与跨夜处理进度结算。

## 兼容性与安装

- 需要 Stardew Valley 1.6.15 或更高版本，以及 SMAPI 4.5.2 或更高版本。
- SVSAPME 需要 SVSAP `1.4.1-rc1.0` 或更高版本。
- 安装前删除旧的 `SVSAP` 与 `SVSAPME` 文件夹，再放入新文件夹；不要直接合并覆盖旧版文件。
- 仅在需要保留原有设置时保留个人 `config.json`。
- 多人联机必须在载入存档前同步更新主机和全部 farmhand。

## 发布候选说明

本版本并非 1.4.1 最终正式版。请保留重要存档备份；反馈可复现问题时，请一并提供 SMAPI 日志、单人或联机角色、涉及的机器或菜单以及完整操作步骤。
