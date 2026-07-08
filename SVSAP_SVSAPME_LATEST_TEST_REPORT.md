# SVSAP / SVSAPME 最新测试报告

更新时间：2026-07-08 15:10 CST

版本口径：`1.4.0-alpha.1`  
展示名：`Ver1.4.0Alpha1`

## 总结

当前为 Ver1.4.0 Alpha1 版本审查整改结果，不是发布动作。第三轮审阅后的风险清理已完成，且已在当前源码/Debug 部署上重跑 Debug/Release 构建、i18n、SMAPI 自测、FullMatrix、P0/P1 单人、P0/P1 多人与 SVSAP RouteA 多人门禁。代码层面当前结论为 GO WITH RISKS；剩余风险主要是人工截图审美、modded chest 扩容兼容和长期 pending delivery 清理策略。

## 当前验证

| 项目 | 结果 | 证据 |
|---|---|---|
| SVSAP Debug build | PASS | `dotnet build SVSAP/SVSAP.csproj -c Debug`，0 warning / 0 error |
| SVSAP Release build | PASS | `dotnet build SVSAP/SVSAP.csproj -c Release`，0 warning / 0 error |
| SVSAPME Debug build | PASS | `dotnet build SVSAPME/SVSAPME.csproj -c Debug`，0 warning / 0 error |
| SVSAPME Release build | PASS | `dotnet build SVSAPME/SVSAPME.csproj -c Release`，0 warning / 0 error |
| i18n parity | PASS | SVSAP `570/570`，SVSAPME `371/371`，本轮静态 literal missing 为 0 |
| Release zip structure | PASS | `SVSAP 1.4.0-alpha.1.zip` 与 `SVSAPME 1.4.0-alpha.1.zip` 均为单 root、无 `config.json`、无 `bin/obj/.git/e2e/backups` |
| manifest version | PASS | zip 内 manifest 均为 `1.4.0-alpha.1`；SVSAPME 依赖 SVSAP `1.4.0-alpha.1+` |
| SVSAP selftest | PASS | SMAPI log：`SVSAP_SELFTEST_OK 31/31`，包含 `remote-structural-snapshot-contract` 与 `gui-layout-bounds` |
| SVSAPME selftest | PASS | SMAPI log：`SVSAPME selftest completed: 37 implemented case(s) passed.`，包含 `PASS gui-layout-bounds` |
| GUI bounds selftest | PASS | `StorageDriveMenu`、`TransferBusMenu`、`PoweredTransferMenu`、`SingleBlockFarmMenu`、`SingleBlockProcessorMenu` 覆盖 1280x720 与 800x720 等紧凑布局边界 |
| FullMatrix single-player E2E | PASS | `e2e-runs/full-1.4.0-alpha1-20260708-150141/full-matrix-complete.json`：`pass=true`，`passed=45`，`total=45` |
| FullMatrix machine roundtrip | PASS | FullMatrix 81：`types=37 repositoryRoundTrip=true networkModData=true` |
| FullMatrix debug recipes | PASS | FullMatrix 83：`freeRecipes=48/48 debugUnlocks=True nonSvsapUntouched=True` |
| P0/P1 single-player E2E | PASS | `e2e-runs/p0p1-single-1.4.0-alpha1-20260708-150210/single-complete.json`：`pass=true` |
| P0/P1 multiplayer E2E | PASS | `e2e-runs/p0p1-multi-1.4.0-alpha1-20260708-150412/host-complete.json`：`pass=true`，M1-M5 全 PASS；`client-complete.json`：`ok=true` |
| SVSAP RouteA multiplayer E2E | PASS | `e2e-runs/svsap-routea-1.4.0-alpha1-20260708-150613/client-complete.json`：`verifiedHandSwitches=4`，`stage=100`；`host-complete.json` 已生成 |

## Alpha1 修复确认

- 版本字段已统一为 `1.4.0-alpha.1`：`SVSAP/manifest.json`、`SVSAP/SVSAP.csproj`、`SVSAPME/manifest.json`、`SVSAPME/SVSAPME.csproj`。
- 新增单方块酒桶/陈酿桶只归属 `SVSAPME`。
- 单方块酒桶/陈酿桶未完成槽位回收改为返还投入物，完成后才返还产物。
- `single-block-processor-rules` 自测覆盖了未完成回收、防提前兑现、完成回收、容量、咖啡豆数量、酒桶果酒 parent preserve、陈酿到铱星等规则。

## 2026-07-08 增量修复确认

- SVSAPME/SVSAP 物品序列化补充 preserve type、价格、名称、颜色等保真字段。
- 单方块酒桶果酒/蔬菜汁产物写入 vanilla preserve type，避免只有 parent id 而缺少风味身份。
- 单方块酒桶修复跨日分钟推进，ETA 日换算改为 1200 分钟/日。
- 单方块酒桶/陈酿桶新增网络自动输入/输出，空槽按过滤规则自动拉取，完成槽和 overflow buffer 自动回写网络。
- 单方块酒桶/陈酿桶新增输入缓冲：手持原料交互会进入 left input buffer，再按空槽自动填入；咖啡豆这类多件配方允许先缓存不足数量，补齐后启动。
- 单方块农场改为每 plot 保存 seed/harvest/farming level，支持同一台机器内不同作物、不同进度、不同成熟时间。
- 单方块农场新增专用 GUI：左输入/过滤，中间 plot 进度格，右输出/经济估算。
- 单方块酒桶/陈酿桶 GUI 扩展为左输入/过滤，中间工作槽，右输出/经济估算。
- 单方块农场、单方块酒桶/陈酿桶、存储驱动器、导入/导出器 GUI 补充响应式布局，紧凑宽度下控制区换行或缩列，不再让右侧栏越出菜单。
- farmhand 收取处理机产物改为 host 返回 serialized item payload，client 本地落物/进包，避免 host 直接写远程 Farmer 副本。
- 供电导入/导出器的过滤模式、矿典模式、品质模式切换不再消耗卡片。
- 矿典匹配去掉裸 `category:*` 泛匹配，保留 `ore:fish`、矿物、材料、加工品和显式金属/煤/电池组，降低误匹配。

## 2026-07-08 13:08 二轮审计整改确认

- 单方块酒桶果酒/蔬菜汁改用 Stardew vanilla flavored output API 创建产物，风味名称与售价不再只靠 parent/preserve 字段推断。
- 农场 `InputBuffer` 已纳入 reclaim/retire 判断；处理机 `InputBuffer` 已纳入 retire 阻断，避免机器消失时吞输入缓冲。
- 处理机默认关闭网络自动输入，并通过 schema 3 迁移关闭旧存档中“全量可用物品自动拉取”的无过滤默认值。
- Farmhand 收取处理机产物改为 host 持久化 `PendingRemoteDeliveries`，client 成功交付后回 ACK；断线重连会重发未确认 payload。
- 处理机 farmhand 投料改为进入 host-side input buffer，不再要求一次交互就满足完整配方。
- 单方块酒桶在 `DayStarted` 结算上一日剩余分钟，避免夜间/跨日时间债丢失。
- 供电导出器对 Chest/Big Chest 追加缺失槽位容量计算，压缩 `Items` 列表时也能向空箱写入。
- SVSAP/SVSAPME 物品 codec 不再持久化本地化 `DisplayName`，避免跨语言/版本存档显示名漂移。
- `svsapme_selftest` 已补协议 ACK、默认自动输入关闭、vanilla 风味产物、输入缓冲 retire 阻断等静态契约覆盖。

## 2026-07-08 15:10 三轮风险清理确认

- Farmhand 空手打开 Storage Drive 或 Importer/Exporter 时不再进入本地空菜单，改为向 host 请求结构快照并打开远程只读/操作 GUI；取出元件、3x3 过滤、矿典、品质和方向切换均走 host-authoritative action。
- SVSAP 新增 `StructuralSnapshotRequest/Response`、远程存储驱动器/传输总线 snapshot DTO 与 `RemoteStorageDriveMenu`、`RemoteTransferBusMenu`，并由 `remote-structural-snapshot-contract` 自测覆盖。
- SVSAPME farmhand 发送会消费手持物的机器动作前，会把 escrow 写入玩家 `modData`；发送失败、存档重载或断线清理时可恢复，避免“先扣物再没发出去”的丢失窗口。
- Pending delivery 过期处理改为只清理 host 可确认 client 已持久化 reconciled transaction 的记录；不再盲目把 7 天未确认产物回灌机器输出，降低 ACK 在途崩溃后的复制窗口。
- 远程状态菜单的 `Collect All` 发送失败不再显示已发送成功，并会关闭/刷新旧快照菜单，避免玩家看到假成功。
- 本轮改动代码包已通过本地检查：14 个 `.cs` 文件，ForbiddenCount=0，SHA256 `406C3248E2BA4FEF94CF0B92AD5D12477F3B97C4889A613C3B9BA44558B04D85`。

## 非阻断建议

- 功能性 GUI 边界已由 SMAPI `gui-layout-bounds` 自测覆盖；后续仍可做一次人工截图抽查，用于纯审美微调。
- `GetChestSlotCapacity` 当前优先尝试真实容量，仍建议后续用更多 modded chest 实测确认非原版扩容箱兼容。
- Pending delivery 对永久不回归玩家仍可能长期留档；当前策略偏向“不复制、不丢失”，后续可设计 host 邮件或过期回收箱。
