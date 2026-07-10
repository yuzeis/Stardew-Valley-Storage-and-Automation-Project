# SVSAP / SVSAPME 最新测试报告

更新时间：2026-07-10 21:05 CST

版本口径：`1.4.0-alpha.1.2`
展示名：`Ver1.4.0Alpha1 Hotfix2`

## 总结

当前为 Ver1.4.0 Alpha1 Hotfix2 的审计整改与发布候选。已修复远程物品 escrow 的丢失/复制窗口、主机异常无响应、多人终端状态串扰、菜单逐帧高开销解码，以及单方块农场生产路径测试失真；当前源码已重新通过 Debug/Release 构建、i18n、SMAPI 自测、FullMatrix、P0/P1 单人/多人和 SVSAP RouteA 多人门禁。自动门禁已全绿，最终视觉与操作手感仍由用户在实际存档中验收。

## 当前验证

| 项目 | 结果 | 证据 |
|---|---|---|
| SVSAP Debug build | PASS | `dotnet build SVSAP/SVSAP.csproj -c Debug`，0 warning / 0 error |
| SVSAP Release build | PASS | `dotnet build SVSAP/SVSAP.csproj -c Release`，0 warning / 0 error |
| SVSAPME Debug build | PASS | `dotnet build SVSAPME/SVSAPME.csproj -c Debug`，0 warning / 0 error |
| SVSAPME Release build | PASS | `dotnet build SVSAPME/SVSAPME.csproj -c Release`，0 warning / 0 error |
| i18n parity | PASS | SVSAP `603/603`，SVSAPME `515/515`；placeholder mismatch 0、literal key missing 0、`en.json` 中文字符 0、源码/i18n Emoji 文件 0 |
| Release zip structure | PASS | 桌面两个 Hotfix2 验证包均为单 root、4 entries、manifest/DLL 完整、bad entry 0 |
| UI text static scan | PASS | `SVSAP/src` 与 `SVSAPME/src` 中 `Game1.tinyFont`、`TODO`、`NotImplementedException` 均为 0；地块锁定改为像素锁图标，详细说明留在 tooltip |
| Port/energy static contract | PASS | 新增 `MachinePortCatalog` 与 `EnergyTelemetryService`；能源监视器改为状态面板，显示存量、容量、实时净流量、今日累计、主要来源和失败原因 |
| manifest version | PASS | zip 内 manifest 均为 `1.4.0-alpha.1.2`；SVSAPME 依赖 SVSAP `1.4.0-alpha.1.2+` |
| SVSAP selftest | PASS | `.codex_runtime/hotfix2-selftest-20260710-205516/SMAPI-latest-selftest.txt`：`SVSAP_SELFTEST_OK 31/31` |
| SVSAPME selftest | PASS | 同一日志：`SVSAPME selftest completed: 41 implemented case(s) passed.`，包含 `farm-mixed-lock-production` |
| GUI bounds selftest | PASS | 本地/远程 Storage Drive、Transfer Bus、Powered Transfer、单方块农场/处理机覆盖 1280x720 与 800x720 紧凑布局边界 |
| FullMatrix single-player E2E | PASS | `e2e-runs/full-hotfix2-20260710-210111/full-matrix-complete.json`：`pass=true`，`45/45` |
| FullMatrix machine roundtrip | PASS | FullMatrix 81：`types=37 repositoryRoundTrip=true networkModData=true` |
| FullMatrix debug recipes | PASS | FullMatrix 83：`freeRecipes=48/48 debugUnlocks=True nonSvsapUntouched=True` |
| P0/P1 single-player E2E | PASS | `e2e-runs/p0p1-single-hotfix2-20260710-210136/single-complete.json`：`pass=true`，R1-R6 全 PASS |
| P0/P1 multiplayer E2E | PASS | `e2e-runs/p0p1-multi-hotfix2-20260710-210228/host-complete.json`：`pass=true`，M1-M5 全 PASS；`client-complete.json`：`ok=true` |
| SVSAP RouteA multiplayer E2E | PASS | `e2e-runs/svsap-routea-hotfix2-20260710-210349/client-complete.json`：`verifiedHandSwitches=4`，`stage=100`；host/client 均 complete |
| 16:26 riskfix SMAPI selftest | PASS | SMAPI log：`SVSAP_SELFTEST_OK 31/31`；`SVSAPME selftest completed: 37 implemented case(s) passed.` |
| 16:23 riskfix FullMatrix | PASS | `e2e-runs/full-riskfix-20260708-162308/full-matrix-complete.json`：`pass=true`，`45/45` |
| 16:23 riskfix P0/P1 single | PASS | `e2e-runs/p0p1-single-riskfix-20260708-162341/single-complete.json`：`pass=true` |
| 16:21 riskfix P0/P1 multiplayer | PASS | `e2e-runs/p0p1-multi-riskfix-20260708-162128/host-complete.json`：`pass=true`；`client-complete.json`：`ok=true` |
| 16:22 riskfix SVSAP RouteA multiplayer | PASS | `e2e-runs/svsap-routea-riskfix-20260708-162221/client-complete.json`：`verifiedHandSwitches=4`，`stage=100` |
| 18:16 GUI/energy/ports runtime | PASS | 最终 Debug DLL 已完成 SMAPI 自测、FullMatrix、P0/P1 单人/多人和 RouteA 多人重跑；无失败 JSON。人工视觉截图仍由用户实机验收。 |

## 2026-07-10 21:05 Hotfix2 审计整改验证

- SVSAP 与 SVSAPME 的远程物品暂存统一为“持久化 escrow + 原事务重放 + 主机幂等响应 + 有界超时重试”，不再分别偏向丢失或复制。
- SVSAPME 主机动作异常会返回失败响应；客户端重连会恢复玩法状态并继续核对，断线不再破坏主机的幂等缓存。
- 终端推送不再广播操作者的页码、合成批量或品质策略；Storage Drive 与 Pattern Provider 的高开销槽位视图改为按 tick 缓存并在操作后强制失效。
- 单方块农场生产逻辑优先满足锁定地块，并新增混种、锁定种子、成长、收获与再生作物的实际生产帮助函数自测。
- 桌面验证包：`SVSAP-1.4.0-alpha.1.2-Hotfix2-Verified-Release-20260710-210003.zip` 与 `SVSAPME-1.4.0-alpha.1.2-Hotfix2-Verified-Release-20260710-210003.zip`。

## 2026-07-10 18:16 完整 GUI 修复验证

- SVSAP 网络/合成/存储/总线菜单完成紧凑布局、懒刷新、真实元件与升级卡槽、3x3 幽灵过滤和远程主机权威交互；远程样板供应器也纳入结构快照与事务响应。
- SVSAPME 单方块农场、酒桶和陈酿桶完成真实输入/输出/模块区、逐格进度与 ETA、混种与地块锁定、黑白名单自动输入、经济估算和网络自动弹入/弹出状态展示。
- Powered Transfer 与能源监视界面显示真实在线状态、储能/容量、动作耗电、吞吐、方向和生产/消耗诊断；紧凑远程布局不再越出菜单。
- 实体质量卡/矿典卡/速度卡/容量卡由真实升级槽控制，不再把卡片当过滤物或无条件开关；FullMatrix 59 已按实体质量卡流程重写并通过。
- 最终桌面测试包：`SVSAP 1.4.0-alpha.1.1 GUI-Fix Release 20260710-181525.zip` 与 `SVSAPME 1.4.0-alpha.1.1 GUI-Fix Release 20260710-181525.zip`。

## 2026-07-08 22:36 UI/能源/端口整改确认

- 单方块农场、酒桶、陈酿桶菜单不再使用 `Game1.tinyFont` 绘制状态文本；左/右面板改用短中文标签、宽度适配和 hover tooltip，避免把 `AllEligible`、`Whitelist` 等内部枚举直接显示给玩家。
- 单方块农场补回左侧输入槽预览、右侧输出槽预览、上方模块槽和底部日产值/缓冲摘要；单方块酒桶/陈酿桶补回左侧输入缓冲槽、右侧完成/输出槽和底部日产值/运行摘要。
- 单方块农场/处理机格子 hover 增加输入、产出、进度和 ETA 信息；满载古代水果等状态下，格内只保留短进度，详细信息放到 tooltip。
- 新增机器端口目录，所有 SVSAPME 机器都有输入/输出/能源端口说明；煤炭发电器明确为燃料输入 + 能源输出。
- 能源监视器改为状态面板，显示网络、存储量、容量百分比、最近发电/耗电/净流量、今日累计、主要生产/消耗来源与最近失败原因；无能源元件时返回明确错误。
- 新增三条 SVSAPME 自测契约：`single-block-real-state-text-fit`、`machine-port-definition-contract`、`energy-telemetry-contract`。本轮只完成编译与静态检查，仍需用 Debug SMAPI 执行确认。

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

## 2026-07-08 16:26 剩余风险补修确认

- SVSAP pending remote delivery 新增 `CreatedDay`/`CreatedTick`，ACK 后的 client-side reconciled delivery id 持久化到玩家 `modData`，重启后收到重复 payload 会先 ACK 并跳过再次发物。
- SVSAP host 在 `DayStarted` 清理过期 pending delivery：已确认的记录删除；未确认且过期的记录移动到目标玩家 durable mailbox，目标玩家下次载入时恢复物品。
- SVSAPME processor/machine returned item 使用同类 durable mailbox 逻辑，避免永久不回归玩家的 pending 记录无限驻留 host 存档。
- Live Mods 已从当前 Release 输出重新部署，`SVSAP.dll` SHA256 `C271F80B5071C2E42B8E2B6A380ED048226EFC1D39DC981646F6EE84C74DA85F`，`SVSAPME.dll` SHA256 `563A3548EE216615FC3CE5ADF6FC5C51151CA1A75E287BC766BCD44F21DF41B4`。
- 本轮改动代码包已放到桌面：`C:\Users\Koizumi\Desktop\changed-code-riskfix-20260708-162537.zip`，6 个 `.cs` 文件，ForbiddenCount=0，SHA256 `9ECE9A78CD1ABE3F917B1F0EB537FBC8436C9BBC76BA9888821573F1BC99C084`。

## 非阻断建议

- 功能性 GUI 边界已由 SMAPI `gui-layout-bounds` 自测覆盖；后续仍可做一次人工截图抽查，用于纯审美微调。
- `GetChestSlotCapacity` 当前优先尝试真实容量，仍建议后续用更多 modded chest 实测确认非原版扩容箱兼容。
- Pending delivery 已有过期 mailbox 回收路径；长期仍可进一步设计 host 邮件或可视化回收箱，让玩家能主动查看跨玩家待恢复物。
