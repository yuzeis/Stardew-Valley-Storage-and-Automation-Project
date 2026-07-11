# SVSAP / SVSAPME 最新测试报告

更新时间：2026-07-11 21:14 CST

版本口径：`1.4.1-rc1.0`
展示名：`Ver1.4.1-rc1.0`

## 总结

当前进入 **Ver1.4.1-rc1.0 发布候选发布流程**。本轮已增加单格铲除与清空全部地块，两类破坏性操作均有本地/远程确认提示，明确不返还作物、肥料、生长进度与地块锁；清空操作保留输入缓冲、模块、过滤和 AutoPull 设置，并提示 AutoPull 可能重新播种。Fable 报告中可由源码确认的 P1/P2/P3 项已全部整改，包括跨会话持久化幂等账本、真实箱锁、咖啡与品质卡自动输入、畸形缓冲恢复、终端/扫描性能及响应缓存一致性。`1.4.1-rc1.0` 版本字段更新后，双项目 Debug/Release、i18n 与发布包静态检查已通过；用户于 2026-07-11 21:13 明确要求跳过本轮 SMAPI 自测、FullMatrix、P0/P1、RouteA 与 GUI 截图复跑并直接推送，因此不得把旧版本运行时证据表述为本版本的新验证结果。

## 2026-07-11 21:14 RC1 发布门禁豁免记录

| 项目 | 结果 | 说明 |
|---|---|---|
| Debug / Release | PASS | SVSAP 与 SVSAPME 两种配置均 0 warning / 0 error |
| i18n parity | PASS | SVSAP `611/611`、SVSAPME `559/559`；missing/extra、placeholder mismatch、literal key missing 与 `en.json` 中文字符均为 0 |
| Release zip 静态检查 | PASS | 两包均为单一正确 mod root，manifest/DLL/default+en i18n 完整，版本为 `1.4.1-rc1.0`，无 config、源码、构建、E2E 或备份污染 |
| SMAPI / E2E / GUI 复跑 | WAIVED | 用户明确要求无需验证、直接推送；本版本不新增运行时通过声明 |
| 前一版最终源码运行时证据 | REFERENCE ONLY | `1.4.0-alpha.1.2` 最终源码曾通过 selftest 35/35 与 43/43、FullMatrix 48/48、GUI 31/31、P0/P1 单人/多人及 RouteA；仅作风险背景，不替代 RC1 复跑 |

## 2026-07-11 19:56 农场铲除与 Fable 审查整改验证

| 项目 | 结果 | 证据 |
|---|---|---|
| 发布状态 | FROZEN | 未上传、未更新 live Mods、未复制桌面对外包；构建自动产生的 zip 仅为工作区中间产物 |
| 单格铲除 | PASS | 本地与远程菜单提供铲除模式；点击目标格后弹出确认框，仅删除该格作物与锁定，不返还投入物，不影响其他地块 |
| 清空全部 | PASS | 清除全部作物和地块锁；确认框列明已种植数、锁定数、无返还与 AutoPull 重播种风险；保留输入缓冲、模块、过滤、自动输入和自动输出设置 |
| 多人权限 | PASS | farmhand 只发送主机权威动作；主机校验机器、地块索引与当前状态，成功后广播新快照；重复清空无副作用 |
| P1 跨会话幂等 | PASS | SVSAPME schema 7 持久化已执行机器动作；SVSAP schema 3 分别持久化终端存入与结构消费；同 ActionId/RequestId 重放返回既有终局，不再次消费，载荷不匹配被拒绝 |
| P2 输入与恢复 | PASS | 处理机 AutoPull 使用足量探测栈，咖啡 5 件配方可启动；酒桶品质卡不再被自动输入品质限制抵消；畸形缓冲逐项清理并恢复可回收物，单台机器异常不阻断其余机器 |
| P2 箱子互斥 | PASS | 导入/导出改用真实 `NetMutex.RequestLock`，同一箱子 pending 去重并在 finally 释放；回调执行前重新校验机器状态、朝向与目标箱 |
| P3 性能与一致性 | PASS | 终端筛选/排序按版本缓存；库存扫描改字典桶索引；移除新存档 `DisplayName`；SDK 固定 8.0.416；响应缓存统一 first-write；离线酒桶更新时间归位 |
| Debug / Release | PASS | SVSAP 与 SVSAPME 两种配置均 0 warning / 0 error |
| i18n parity | PASS | SVSAP `611/611`、SVSAPME `559/559`；missing/extra 0、placeholder mismatch 0、literal key missing 0、`en.json` 中文字符 0 |
| 隔离 SMAPI 自测 | PASS | `e2e-runs/farm-clear-audit-exact-final-selftest-20260711-195637/SMAPI-latest-selftest.txt`：SVSAP `35/35`、SVSAPME `43/43` |
| FullMatrix | PASS | `e2e-runs/farm-clear-audit-exact-final-full-gui-20260711-194653/full-matrix-complete.json`：`pass=true`，`48/48`；case 94/95 分别覆盖铲除/清空与咖啡/品质卡 AutoPull |
| GUI 截图硬门禁 | PASS | 同目录 `gui-capture-complete.json`：`31/31`、31 个唯一 SHA256；17 张 SVSAP 与 14 张 SVSAPME 图片均逐张复核，无越界、遮挡、图标/槽位错位或异常放大文字 |
| P0/P1 单人 | PASS | `e2e-runs/farm-clear-audit-final2-p0p1-single-20260711-192355/single-complete.json`：`pass=true`，`7/7` |
| P0/P1 多人 | PASS | `e2e-runs/farm-clear-audit-final3-p0p1-multi-20260711-193457`：host `pass=true`、`5/5`，client complete |
| RouteA 多人 | PASS | `e2e-runs/farm-clear-audit-final3-routea-20260711-193956`：host/client complete，client `verifiedHandSwitches=4`、`stage=100` |

本轮行为口径：

- “铲除”是明确的放弃操作，不返还种子、肥料、作物或已累计进度；这避免把农场机器变成无损重置工具。
- 单格铲除同时解除该格作物锁，清空全部同时清除全部地块锁；仅有锁而没有作物的地块也可被清空。
- 自动输入保持开启时，空地块可能在后续农场循环中按当前过滤规则重新种植，确认框会在执行前明确提醒。
- 本轮 P0/P1 首次复跑发现旧测试夹具未推进真实箱子异步 `NetMutex`；修正夹具后重新执行，生产路径没有退回伪同步锁。

## 2026-07-11 17:13 处理机真实升级槽整改验证

| 项目 | 结果 | 证据 |
|---|---|---|
| 发布状态 | FROZEN | 未上传、未更新 live Mods、未复制桌面对外包；本轮只修改与验证工作区源码 |
| 槽位数量与界面 | PASS | 铜/钢/金/铱处理机分别为 `2/3/4/5` 槽；`SingleBlockProcessorMenu` 和 `RemoteMachineControlMenu` 均渲染实体卡、空槽幽灵图、状态线和 hover 说明，点击区域与绘制区域共用同一布局 |
| 本地/远程交互 | PASS | 本地可从背包安装或点击槽位移除；farmhand 安装走 escrow，主机成功后确认消费，移除由主机返回物品 payload；非法卡、满槽和失败动作不扣物 |
| 速度卡 | PASS | 每张速度卡令实际完成工作量增加 10%；余数跨 tick 保存，耗电按实际完成工作结算，不产生免费进度 |
| 容量卡 | PASS | 网络输出受阻时，每张容量卡增加一整机批次容量，即 `tier.Slots` 件；缓冲满时不清除完成槽 |
| 品质卡 | PASS | 仅酒桶可安装且最多一张；只使新装载的酒桶任务保留投入品质，避免中途安装追溯改写既有任务；陈酿桶因自身承担品质升级而拒绝品质卡 |
| 存档与回收 | PASS | `MachineStateRepository` schema 6 初始化并规范化卡槽与速度余数；已安装卡纳入 recoverable payload、退役阻断和 reclaim 清理，拆机不会吞卡或复制 |
| Debug / Release | PASS | SVSAP 与 SVSAPME 两种配置均 0 warning / 0 error |
| i18n parity | PASS | SVSAP `610/610`、SVSAPME `544/544`；missing/extra 0、placeholder mismatch 0、literal key missing 0、`en.json` 中文字符 0 |
| GUI 契约 | PASS | `GUI_CONTRACT_CHECK_OK session-ordering count-format compact-layout extended-backpack` |
| 隔离 SMAPI 自测 | PASS | `e2e-runs/processor-upgrades-selftest-20260711-165653/SMAPI-latest-selftest.txt`：SVSAP `34/34`、SVSAPME `41/41` |
| FullMatrix | PASS | `e2e-runs/processor-upgrades-full-gui-20260711-165818/full-matrix-complete.json`：`pass=true`，`46/46`；新增 case 72 验证实体槽位与三类卡片规则 |
| GUI 截图硬门禁 | PASS | 同目录 `gui-capture-complete.json`：`30/30`、30 个唯一 SHA256；酒桶为 `3/5`、陈酿桶为 `2/5`、远程处理机为 `3/5` 测试状态，全部槽位均可见且未与端口、工作格或背包重叠 |
| P0/P1 单人 | PASS | `e2e-runs/processor-upgrades-p0p1-single-20260711-170419/single-complete.json`：`pass=true`，`7/7` |
| P0/P1 多人 | PASS | `e2e-runs/processor-upgrades-p0p1-multi-20260711-170918`：host `pass=true`、`5/5`，client complete |
| RouteA 多人 | PASS | `e2e-runs/processor-upgrades-routea-20260711-171202`：host/client complete，client `verifiedHandSwitches=4`、`stage=100` |

本轮行为口径：

- 输入、输出、能源端口继续负责说明机器与网络的功能边界和实时状态；升级槽是另一组真实卡片库存，两者不再混为一谈。
- 速度卡与容量卡可在物理槽位允许范围内重复安装；品质卡在酒桶中最多一张，陈酿桶明确拒绝。
- 品质卡只影响安装后新进入工作槽的原料。已经开始的任务保持其锁定结果，避免热插拔套利。

## 2026-07-11 14:48 三项运行时缺陷整改验证

| 项目 | 结果 | 证据 |
|---|---|---|
| 发布状态 | FROZEN | 未上传、未更新 live Mods、未复制桌面对外包；构建自动产生的 zip 仅为本地中间产物 |
| 单方块处理机端口 | PASS | `SingleBlockProcessorMenu` 与 `RemoteMachineControlMenu` 均显示输入/输出/能源端口；状态灯读取真实网络与储能状态；端口是功能/状态入口，物品仍由左侧输入缓冲、中部工作槽、右侧输出缓冲承载 |
| 箱子读取边界 | PASS | `NetworkRepository` schema 2 清除 legacy `EndpointType.Chest`；`NetworkInteractionService` 拒绝直接绑定 Chest；扫描和事务层只接纳活动存储接口上下左右相邻箱子 |
| 导入/导出真实搬运 | PASS | `svsap-storage-transfer-preflight.json`：右向导入器从相邻箱子导入 `20` 件，右向导出器向相邻箱子导出 `20` 件；legacy 直连箱不可见 |
| Debug / Release | PASS | SVSAP 与 SVSAPME 两种配置均 0 warning / 0 error |
| i18n parity | PASS | SVSAP `610/610`、SVSAPME `527/527`；missing/extra 0、placeholder mismatch 0、`en.json` 中文字符 0 |
| 隔离 SMAPI 自测 | PASS | `e2e-runs/three-issues-port-status-selftest-20260711-142141/SMAPI-latest-selftest.txt`：SVSAP `34/34`、SVSAPME `41/41` |
| FullMatrix | PASS | `e2e-runs/three-issues-final-current-20260711-142440/full-matrix-complete.json`：`pass=true`，`45/45` |
| GUI 截图硬门禁 | PASS | 同目录 `gui-capture-complete.json`：`30/30`，30 个唯一 SHA256；当前处理机三张关键图已复核端口、星级、图标居中与状态色 |
| P0/P1 单人 | PASS | `e2e-runs/three-issues-current-p0p1-single-20260711-143100/single-complete.json`：`pass=true`，`7/7` |
| P0/P1 多人 | PASS | `e2e-runs/three-issues-current-p0p1-multi-20260711-143411`：host `pass=true`、`5/5`，client complete |
| RouteA 多人 | PASS | `e2e-runs/three-issues-current-routea-20260711-144627`：host/client complete，client `verifiedHandSwitches=4`、`stage=100` |

本轮行为口径：

- 普通箱子不再是无线网络端点。存储接口只读取自身上下左右四个相邻格，其他位置、其他地图和旧版直连箱子都不会被扫描。
- 导入器执行“相邻容器 -> SVSAP 网络”，导出器执行“SVSAP 网络 -> 相邻容器”。朝向决定目标格；导出器必须配置过滤目标，导入器无过滤时搬运全部合规物品。
- 单方块处理机顶部三个格是输入、输出和能源的可见功能端口/状态指示，不是三份独立库存。真实输入、逐格加工和产出仍分别位于左、中、右区域。

## 2026-07-11 02:40 GUI 关联问题深度复核

| 项目 | 结果 | 证据 |
|---|---|---|
| 发布状态 | FROZEN | 未上传、未更新 live Mods、未复制桌面对外包；构建自动生成的 zip 仅为本地中间产物 |
| SVSAP Debug / Release | PASS | 两种配置均 0 warning / 0 error |
| SVSAPME Debug / Release | PASS | 两种配置均 0 warning / 0 error |
| i18n parity | PASS | SVSAP `603/603`、SVSAPME `515/515`；missing/extra 0、placeholder mismatch 0、`en.json` 中文字符 0 |
| GUI 契约与尺寸扫描 | PASS | `GUI_CONTRACT_CHECK_OK session-ordering count-format compact-layout extended-backpack`；覆盖 640/720/800/960/1280 宽、720 高和 36/48/60 槽背包 |
| 隔离 SMAPI 自测 | PASS | `.codex_runtime/gui-audit-selftest-postcompact-20260711-023601/SMAPI-latest-selftest.txt`：SVSAP `31/31`、SVSAPME `41/41`，运行时错误命中 0 |
| FullMatrix / P0P1 / RouteA | PENDING | 本轮最后的紧凑布局改动后未重跑，不使用旧门禁解除冻结 |
| 实机视觉与鼠标验收 | PENDING | 仍需检查 640x720、800x720，尤其 48/60 槽背包与多人远程菜单 |

本轮继续确认并修复：

- 终端按实际背包行数动态缩放物品格和背包格；网络物品继续使用原版 `Item.drawInMenu`，小于 1000 的数量由原版绘制，只有大数在右下角显示 `K`/`M`。
- 本地/远程网络终端的九个分类按钮改为真实可用宽度均分；远程搜索框为翻页按钮预留固定空间，避免 640 宽时互相覆盖。
- 本地/远程样板供应器按可用宽高动态选择工作槽列数、行数和格子尺寸，绘制与命中区域共用同一布局。
- 本地/远程传输总线在 640x720 下扩大可用菜单区域并统一控制按钮、方向按钮和 3x3 过滤网格的布局计算。
- 远程供电控制台在短内容区使用紧凑遥测、44px 升级槽和自适应多列按钮；本地供电传输器改用 48px 视口边距，60 槽背包不再压入过滤/方向区。
- 关闭穿透与重复音效继续由会话 ID、单调请求序号和已消费打开请求约束；静态复核未发现新的 `update()` 循环播放点击音效路径。

## 2026-07-10 22:26 GUI 运行时回归深修

| 项目 | 结果 | 证据 |
|---|---|---|
| 发布状态 | FROZEN | 用户已明确要求停止发布；本轮未上传、未更新 live Mods、未生成对外发布结论 |
| SVSAP Debug / Release | PASS | 两种配置均 0 warning / 0 error |
| SVSAPME Debug / Release | PASS | 两种配置均 0 warning / 0 error |
| i18n parity | PASS | SVSAP `603/603`、SVSAPME `515/515`；placeholder mismatch 0、literal key missing 0、`en.json` 中文字符 0、Emoji 文件 0 |
| 纯逻辑 GUI 契约 | PASS | `GUI_CONTRACT_CHECK_OK`：会话匹配、迟到/重复顺序拒绝、已消费会话不重开、K/M 计数、800x720 + 60 槽背包布局 |
| Release zip 结构 | PASS | 两包均单一 mod root、4 entries、manifest/DLL 完整、版本 `1.4.0-alpha.1.2`、bad entry 0；仅作构建产物，不发布 |
| SMAPI 自测 | PENDING | 检测到用户正在运行的 `StardewModdingAPI`，为避免中断实机进程未启动第二实例；旧 31/31、41/41 不代表本轮最新源码 |
| FullMatrix / P0P1 / RouteA | PENDING | 本轮协议与 GUI 源码变更后必须重跑；旧结果仅保留历史证据 |

本轮关键修复：

- 所有远程终端/结构菜单/机器快照增加 `MenuSessionId` 与单调 `RequestSequence`；只有匹配且更新的响应可刷新，只有尚未消费的首次会话可打开菜单。迟到响应在已有菜单或已关闭菜单时不会重开，也不会重复播放 `bigSelect`。
- 网络终端、合成终端、监视器、存储驱动器、传输总线、样板供应器和 SVSAPME 远程机器统一采用有边界的标题、状态灯和文本省略；深色低对比内框改为浅冷灰工作区。
- 网络物品继续调用原版 `Item.drawInMenu`。数量小于 1000 时交给原版堆叠数字绘制；达到 1000 后仅将右下角数量缩写为 `K`，达到 1000000 后缩写为 `M`。
- 传输总线的 8 个实体升级槽移到过滤区上方，避免与 48/60 槽扩展背包重叠；单方块农场、酒桶/陈酿桶和远程机器会按实际背包槽数缩减工作分页，不再硬截 36 槽。
- 发布前仍需在用户关闭当前游戏后重跑 SMAPI 自测、FullMatrix、P0/P1 单人/多人、RouteA，并进行一次 800x720 实机截图验收。

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
