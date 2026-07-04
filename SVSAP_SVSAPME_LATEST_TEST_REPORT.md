# SVSAP / SVSAPME 最新完整测试报告

生成时间：2026-07-04 15:45 +08:00  
适用仓库：`F:\Project\Stardew\SVSAP-ver1.3Alpha1`  
版本口径：`1.3.0-alpha.1`  
判定规则：`NEED REPAIR` 只表示已通过测试发现 mod 本体代码漏洞。缺样本、缺旧存档、无法在当前环境复现，不再写作 `NEED REPAIR`。

## 一、总览结论

| 项目 | 状态 | 证据 |
|---|---:|---|
| SVSAP 自测 | PASS | `e2e-runs\full-20260704T152139\SMAPI-latest.txt`：`SVSAP_SELFTEST_OK 19/19` |
| SVSAPME 自测 | PASS | `e2e-runs\full-20260704T152139\SMAPI-latest.txt`：`SVSAPME selftest completed: 34 implemented case(s) passed.` |
| FullMatrix 单人 E2E | PASS | `e2e-runs\full-20260704T152139\full-matrix-complete.json`：`pass=true`、`passed=45`、`total=45` |
| P0P1 单人 E2E | PASS | `e2e-runs\p0p1-single-20260704T154434\single-complete.json`：R1-R6 全 PASS |
| P0P1 多人 E2E | PASS | `e2e-runs\p0p1-multi-20260704T154251\host-complete.json`：M1-M5 全 PASS |
| host 断连降级 | PASS | `e2e-runs\p0p1-multi-20260704T154251\client-host-offline.json`：`hostConnected=false`、`reportSent=false` |
| 依赖缺失降级 | PASS | `e2e-runs\missing-svsap-20260704T150012\SMAPI-latest.txt`：缺 `Koizumi.SVSAP` 时 SVSAPME 被 SMAPI 跳过且进程不崩 |
| Debug/Release 构建 | PASS | `SVSAP`、`SVSAPME` Debug 与 Release 均 0 warning / 0 error |
| 当前 NEED REPAIR | PASS | 0 项。当前没有测试证明 mod 本体存在待修漏洞 |
| 唯一未判 PASS 项 | NEED SAMPLE | ID 82 旧版本升级存档，需真实旧存档样本或固定迁移夹具 |

## 二、E2E 实测明细

| 套件 | 覆盖 | 状态 | 证据文件 |
|---|---|---:|---|
| FullMatrix | 36-71、81、83、84、88-93 | PASS 45/45 | `F:\Project\Stardew\SVSAP-ver1.3Alpha1\e2e-runs\full-20260704T152139\full-matrix-complete.json` |
| P0P1 single | R1-R6：单机防复制、回收、网络插入、L7 断电/有电 | PASS | `F:\Project\Stardew\SVSAP-ver1.3Alpha1\e2e-runs\p0p1-single-20260704T154434\single-complete.json` |
| P0P1 multi | M1-M5：farmhand 移动/持有/消耗、host-authoritative、读数同步 | PASS | `F:\Project\Stardew\SVSAP-ver1.3Alpha1\e2e-runs\p0p1-multi-20260704T154251\host-complete.json` |
| P0P1 offline | host 断连后 farmhand 上报静默失败 | PASS | `F:\Project\Stardew\SVSAP-ver1.3Alpha1\e2e-runs\p0p1-multi-20260704T154251\client-host-offline.json` |
| 缺依赖启动 | 仅装 SVSAPME、缺 SVSAP | PASS | `F:\Project\Stardew\SVSAP-ver1.3Alpha1\e2e-runs\missing-svsap-20260704T150012\SMAPI-latest.txt` |

关键新增断言：

```json
{
  "FullMatrix": "pass=true, passed=45, total=45",
  "P0P1Multi": ["M1 PASS", "M2 PASS", "M3 PASS", "M4 PASS", "M5 PASS"],
  "HostOffline": {
    "hostConnected": false,
    "reportSent": false
  }
}
```

## 三、最新修补后的覆盖变化

| 原风险项 | 最新判定 | 覆盖方式 |
|---|---:|---|
| 72 host-authoritative | PASS | P0P1 M4：host 可写入 1000 Wh，farmhand 写入返回 `NotHost` |
| 77 host 离线/无 mod 降级 | PASS | P0P1 offline：host 断连后 farmhand movement report `reportSent=false`；缺 SVSAP 依赖启动也已实测跳过不崩 |
| 78 多人 Crafting Terminal 并发 | PASS | SVSAP selftest 新增 `crafting-terminal-contention-no-dupe`，验证精确材料只产出一次、二次竞争失败、无复制 |
| 79 日结/host 结算同步 | PASS | P0P1 M5：host 写入后的能源读数经 host debug response 同步到 farmhand，`4210/10000 Wh` 一致 |
| 81 全机器存档往返 | PASS | FullMatrix 81：29 个 SVSAPME BigCraftable 全类型 repository save/load、网络 modData 保持 |
| 83 Debug 模式 | PASS | FullMatrix 83：40/40 本 mod 配方 Debug 0 成本、全解锁、非 SVSAPME 配方不污染 |
| 84 Casual vs Normal | PASS | FullMatrix 84：Normal→Casual→Normal 切换后材料正确且无缓存残留 |
| 82 旧存档升级 | NEED SAMPLE | 需要旧版真实存档；当前没有证据显示代码漏洞，因此不列为 NEED REPAIR |

## 四、已过时口径

| 旧口径 | 最新口径 |
|---|---|
| 两 mod 版本均为 `1.2.0-alpha.2` | 当前为 `1.3.0-alpha.1` |
| `NEED REPAIR` 可表示 E2E 未覆盖 | 已废止。`NEED REPAIR` 只表示已证实的 mod 本体漏洞 |
| FullMatrix 只覆盖 42 项 | 已扩展为 45 项，新增 81/83/84 |
| SVSAP selftest 18/18 | 已扩展为 19/19，新增 Crafting Terminal contention no-dupe |
| P0P1 multi 只覆盖 M1-M3 | 已扩展为 M1-M5，并补 host offline 降级证据 |
| Debug 下不能出现 `(O)388 0` | 已改为：Debug 仅本 mod 配方 0 成本，Normal 与非 SVSAPME 配方不得被污染 |

## 五、完整测试矩阵 1-101

| ID | 测试项 | 状态 | 最新证据 |
|---:|---|---:|---|
| 1 | `svsap_selftest` 全绿 | PASS | `SVSAP_SELFTEST_OK 19/19` |
| 2 | `svsapme_selftest` 全绿 | PASS | `SVSAPME selftest completed: 34 implemented case(s) passed.` |
| 3 | `svsapme_claim` force 门控 | PASS | `claim-force-gate`、FullMatrix 69、P0P1 R2/R3/R5/M2/M3 |
| 4 | 版本与默认配置 | PASS | `1.3.0-alpha.1`；默认 Normal / false |
| 5 | wh-roundtrip | PASS | `wh-roundtrip` |
| 6 | cell-stack-guard | PASS | `cell-stack-guard`、FullMatrix 67 |
| 7 | machine-guid-reconcile | PASS | `machine-guid-reconcile` |
| 8 | content-table / tier-table | PASS | `content-table`、`tier-table` |
| 9 | config-surface | PASS | `config-surface` |
| 10 | orphan-reclaim | PASS | `orphan-reclaim` |
| 11 | missing-machine-reclaim | PASS | `missing-machine-reclaim` |
| 12 | building-demolish-reclaim | PASS | `building-demolish-reclaim` |
| 13 | location-cache-full-enum | PASS | 背包/箱子/冰箱/迷你冰箱枚举自测 |
| 14 | no-arbitrage-audit | PASS | `no-arbitrage-audit` |
| 15 | b10-parity | PASS | `b10-parity` + FullMatrix 93 |
| 16 | multiplayer-protocol | PASS | `multiplayer-protocol` |
| 17 | action-idempotent | PASS | `action-idempotent` |
| 18 | escrow-restore | PASS | `escrow-restore` |
| 19 | host-action-dispatch | PASS | `host-action-dispatch` |
| 20 | energy-production-rules | PASS | `energy-production-rules`、FullMatrix 37/38 |
| 21 | synth-atomic | PASS | `synth-atomic`、FullMatrix 40 |
| 22 | daily-order-storage-gate | PASS | `daily-order-storage-gate` |
| 23 | farm-crop-set / farm-single-crop-budget | PASS | 两项自测 |
| 24 | farm-daily-progress | PASS | `farm-daily-progress`、FullMatrix 48 |
| 25 | farm-power-freeze | PASS | `farm-power-freeze` |
| 26 | farm-module-economy / farm-fertilizer-quality | PASS | 两项自测 |
| 27 | farm-locked-output | PASS | `farm-locked-output` |
| 28 | powered-prescan-refund | PASS | `powered-prescan-refund`、FullMatrix 55 |
| 29 | powered-degrade-parity | PASS | `powered-degrade-parity`、FullMatrix 89 |
| 30 | powered-interface-range | PASS | `powered-interface-range`、FullMatrix 51 |
| 31 | battery-discharge-gate | PASS | `battery-discharge-gate`、FullMatrix 41 |
| 32 | electric-machine-rules | PASS | `electric-machine-rules`、FullMatrix 52/53 |
| 33 | consumed-charged 退役 | PASS | `consumed-charged-retire`、FullMatrix 65 |
| 34 | demolish held 排除 | PASS | FullMatrix 70 |
| 35 | Debug 配方可见 | PASS | `debug-addon-vanilla-material-recipes-visible` |
| 36 | Carbon Generator | PASS | FullMatrix 36：投煤 +350 Wh |
| 37 | Solar Network Panel | PASS | FullMatrix 37：sunny=1500、rainy=200、winter=1000、indoor=0 |
| 38 | Lightning Capacitor | PASS | FullMatrix 38：storm=6000 |
| 39 | 四级 Energy Cell | PASS | FullMatrix 39：10/40/160/640 kWh |
| 40 | Battery Synthesizer | PASS | FullMatrix 40 |
| 41 | Battery Discharger | PASS | FullMatrix 41 |
| 42 | Energy Monitor Terminal | PASS | FullMatrix 42 |
| 43 | 四级 Farm | PASS | FullMatrix 43：16/64/144/256 |
| 44 | Farm Chamber 升级链 | PASS | FullMatrix 44 |
| 45 | Growth Light 三级 | PASS | FullMatrix 45 |
| 46 | Thermostat 三级 | PASS | FullMatrix 46 |
| 47 | Slow Release 三级 | PASS | FullMatrix 47 |
| 48 | 模块热插拔 | PASS | FullMatrix 48 |
| 49 | Powered Importer 四级 | PASS | FullMatrix 49 |
| 50 | Powered Exporter 四级 | PASS | FullMatrix 50 |
| 51 | Powered Machine Interface 四级 | PASS | FullMatrix 51 |
| 52 | Electric Furnace | PASS | FullMatrix 52 |
| 53 | Electric Geode Crusher | PASS | FullMatrix 53 |
| 54 | L7 核心判据 | PASS | FullMatrix 54 |
| 55 | 扣电原子性 | PASS | FullMatrix 55 |
| 56 | 网络连接 | PASS | FullMatrix 56 |
| 57 | Crafting Terminal | PASS | FullMatrix 57 |
| 58 | PatternProvider 编解码 | PASS | FullMatrix 58 |
| 59 | 品质/低质优先取料策略 | PASS | FullMatrix 59 |
| 60 | 跨位置网络 | PASS | FullMatrix 60 |
| 61 | MachineGuid 唯一 | PASS | FullMatrix 61 |
| 62 | 带电 Cell 拾取/重放 | PASS | FullMatrix 62 |
| 63 | 背包与普通容器复制链 | PASS | FullMatrix 63 |
| 64 | Junimo/global inventory | PASS | FullMatrix 64 |
| 65 | 合成消耗带电机器 | PASS | FullMatrix 65：`retired=True pending=False hudAdded=True` |
| 66 | digitize 守卫 | PASS | FullMatrix 66 |
| 67 | 堆叠守卫 | PASS | FullMatrix 67 |
| 68 | PendingReclaim 生命周期 | PASS | FullMatrix 68 |
| 69 | `svsapme_claim` 判活门控 | PASS | FullMatrix 69 |
| 70 | demolish + 持有态 | PASS | FullMatrix 70：`claim=False machines=0` |
| 71 | 存读复制向量 | PASS | FullMatrix 71 |
| 72 | 多人 host-authoritative | PASS | P0P1 M4：hostDeposit=True/1000，clientCode=NotHost |
| 73 | farmhand 放置/拾取 | PASS | P0P1 M1 |
| 74 | farmhand 消耗带状态机器 | PASS | P0P1 M3：retired=True、noNaturalPending=True |
| 75 | farmhand observe 不误回收 | PASS | P0P1 M1 `noPending=True`，M2 held 无自然 pending |
| 76 | 多人动作幂等 | PASS | `action-idempotent` + P0P1 M1/M2/M3 |
| 77 | host 离线/无 mod 降级 | PASS | `client-host-offline.json`：hostConnected=false、reportSent=false；缺 SVSAP 启动跳过不崩 |
| 78 | 多人 Crafting Terminal 并发 | PASS | SVSAP selftest `crafting-terminal-contention-no-dupe` |
| 79 | 日结同步 / host 结算读数同步 | PASS | P0P1 M5：host 与 farmhand debug 读数均 4210/10000 Wh |
| 80 | M1/M2 场景 | PASS | P0P1 multi M1/M2/M3/M4/M5 |
| 81 | 存档往返全机器 | PASS | FullMatrix 81：types=29，repositoryRoundTrip=true，networkModData=true |
| 82 | 版本升级存档 | NEED SAMPLE | 缺旧版真实存档样本；未发现代码漏洞，不标 NEED REPAIR |
| 83 | Debug 模式行为 | PASS | FullMatrix 83：freeRecipes=40/40、debugUnlocks=True、nonSvsapUntouched=True |
| 84 | Casual vs Normal | PASS | FullMatrix 84：Normal/Casual/Normal 无缓存残留 |
| 85 | 依赖缺失降级 | PASS | 缺 SVSAP 时 SMAPI 跳过 SVSAPME 且不崩 |
| 86 | 跨 mod API 契约 | PASS | `api-shape`、FullMatrix 57/66/93 |
| 87 | 构建产物 | PASS | Debug/Release 四个 build 全绿 |
| 88 | 满网压力 | PASS | FullMatrix 88 |
| 89 | 零电/负载饱和 | PASS | FullMatrix 89 |
| 90 | 电量边界 | PASS | FullMatrix 90 |
| 91 | 快速拾放循环 | PASS | FullMatrix 91 |
| 92 | 异常中断/半态复制 | PASS | FullMatrix 92 |
| 93 | SVSAP 配方运行时 parity | PASS | FullMatrix 93：330/2410/9820/20 |
| 94 | FullMatrix 自动建档 E2E | PASS | `full-20260704T152139` 自动建档并 complete |
| 95 | SVSAP structural failure 反射回归 | PASS | SVSAP selftest 19/19 |
| 96 | live config 生成默认值 | PASS | 实机 config Normal / false |
| 97 | Debug/Release build parity | PASS | 四个 build 全绿 |
| 98 | live mod manifest 启用状态 | PASS | SMAPI 已加载 `Koizumi.SVSAP` 与 `Koizumi.SVSAPME` |
| 99 | B10 表与当前 SVSAP 源配方同步 | PASS | `b10-parity` + FullMatrix 93 |
| 100 | E2E version label | PASS | FullMatrix/P0P1 默认 label 为 `ver1.3.0-alpha.1` |
| 101 | P0P1 M3 farmhand consumed reclaim | PASS | `host-complete.json` M3 PASS；`client-consumed.json` reportSent=True |

## 六、发布门槛

| 门槛 | 状态 |
|---|---:|
| 本地自测全绿 | PASS |
| 构建全绿 | PASS |
| FullMatrix E2E | PASS |
| P0P1 single E2E | PASS |
| P0P1 multi E2E | PASS |
| 本轮直接修复实证 65/70/74 | PASS |
| NEED REPAIR 清单 | 0 项 |

结论：除 ID 82 缺旧存档样本外，矩阵其余项目均已补测试并通过。当前没有测试结果指向 mod 本体漏洞。
