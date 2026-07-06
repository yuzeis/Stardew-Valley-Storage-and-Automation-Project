# SVSAP / SVSAPME 1.3.0「Mühenlohn」

这是 SVSAP 与 SVSAPME 的 1.3.0 正式发布版，版本代号 `Mühenlohn`。文件名中使用 `Muehenlohn` 是为了兼容部分站点和压缩工具，展示名称仍为 `Mühenlohn`。

## 更新内容

- 修复 farmhand 远程终端存入/取出链路：客户端托管真实物品 payload，主机只处理托管 payload，取出由主机返回序列化物品后客户端本地入包或掉落。
- 修复远程终端、远程合成、远程监控 GUI：分页快照、本地化渲染、pending 状态、Stardew TextBox 搜索输入。
- 修复 SVSAP 存储元件防复制/防丢失守卫：非空/有状态存储元件不可堆叠合并，不允许普通网络存入导致内容湮灭或复制。
- 修复 SVSAPME 带状态机器的多人托管、持有、消耗、回收与拆屋链路。
- 明确拆卸能量规则：除 Energy Cell 外，机器拆卸后内部能量清零；Energy Cell 保留电量。
- 改进 host-authoritative 协议、动作幂等、失败返还、host 离线降级与多人读数同步。
- 完善 i18n：中文/英文键值 parity，避免 host 语言直接泄漏到 farmhand HUD。
- 扩展自测与 E2E：SVSAP selftest 28/28，SVSAPME selftest 35/35，FullMatrix 45/45，P0P1 single/multi 与 SVSAP RouteA multi 均通过。

## 安装方法

1. 安装 Stardew Valley 1.6.15 与 SMAPI 4.5.2 或更新版本。
2. 安装 `SVSAP 1.3.0.zip`。
3. 如果需要机器与能源扩展，再安装 `SVSAPME 1.3.0.zip`。
4. 多人游戏中主机与所有 farmhand 必须安装相同版本的 SVSAP/SVSAPME。

## 注意

- 从测试版或旧版升级前建议备份存档。
- 旧版本升级存档仍需要更多真实旧存档样本继续扩大覆盖；当前没有已证实的待修 mod 本体漏洞。
- SVSAPME 依赖 SVSAP，请先安装 SVSAP。
