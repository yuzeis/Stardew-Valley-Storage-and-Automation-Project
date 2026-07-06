# Stardew Valley Storage and Automation Project (SVSAP)

版本：1.3.0「Mühenlohn」  
适配：Stardew Valley 1.6.15 / SMAPI 4.5.2+  
源码：https://github.com/yuzeis/Stardew-Valley-Storage-and-Automation-Project
Nexus：https://www.nexusmods.com/stardewvalley/mods/48476
3DM Mod：https://mod.3dmgame.com/

SVSAP 为星露谷物语加入网络存储、数字存储元件、导入/导出总线、样板编码、机器处理流水线和自动合成系统。SVSAPME 是可选扩展，提供电力机器、发电、储能与 powered 设备。

## 下载与安装

正式发布包由 ModBuildConfig 在 Release 编译时自动生成，文件名为 `SVSAP 1.3.0.zip`。本次发布代号为 `Mühenlohn`，跨平台文件名中使用 `Muehenlohn`。

安装方法：

1. 安装 Stardew Valley 1.6.15 与 SMAPI 4.5.2 或更新版本。
2. 解压 `SVSAP 1.3.0.zip`。
3. 将解压出的 `SVSAP` 文件夹放入 `Stardew Valley/Mods/`。
4. 如果安装 SVSAPME，也将 `SVSAPME` 文件夹放入同一个 `Mods/` 目录。
5. 通过 SMAPI 启动游戏。

## 语言

`config.json` 中的 `Language` 控制玩家可见文本：

```json
"Language": "zh"
```

可用值：

- `zh`：中文
- `en`：English

语言文件位于 `i18n/default.json` 与 `i18n/en.json`。如果安装了 Generic Mod Config Menu，也可以在配置菜单里切换语言。

## 多人联机

所有玩家必须安装相同版本的 SVSAP。使用 SVSAPME 时，主机与客机也必须安装相同版本的 SVSAPME。网络数据由主机保存，客机的网络操作会发送给主机执行。1.3.0 已补齐远程终端 client escrow / response payload 协议，仍建议多人更新时所有玩家同时替换同一版本。

## 源码目录

- `src/`：模组源码
- `assets/`：物品与机器贴图
- `i18n/`：语言文件
- `使用教程与各机器作用.txt`：玩家教程和机器说明

## 控制台命令

- `svsap_m1_ids`：列出 SVSAP 物品 ID 和存储元件容量。
- `svsap_api_dump`：输出 SVSAP API 版本、modData key 与配置快照。
- `svsap_endpoint_probe [x y]`：检查指定或面向地块的网络端点绑定。
- `svsap_api_selftest [x y]`：运行 SVSAP API 契约自测，可选地验证指定端点。
- `svsap_selftest`：Debug 构建限定，运行完整运行时自测；Release 包不包含此命令。

## 注意

SVSAP 使用模组 ID `Koizumi.SVSAP`。从测试版更新到正式版前建议备份存档；旧存档升级场景仍依赖真实旧版样本继续扩大验证。
