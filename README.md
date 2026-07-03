# Stardew Valley Storage and Automation Project (SVSAP)

版本：1.2.0-alpha.2  
适配：Stardew Valley 1.6.15 / SMAPI 4.5.2+  
源码：https://github.com/yuzeis/Stardew-Valley-Storage-and-Automation-Project

SVSAP 为星露谷物语加入网络存储、数字存储元件、导入/导出总线、样板编码、机器处理流水线和自动合成系统。SVSAPME 是可选扩展，提供电力机器、发电、储能与 powered 设备。

## 下载与安装

正式发布包由 ModBuildConfig 在 Release 编译时自动生成，文件名为 `SVSAP 1.2.0-alpha.2.zip`。

安装方法：

1. 安装 Stardew Valley 1.6.15 与 SMAPI 4.5.2 或更新版本。
2. 解压 `SVSAP 1.2.0-alpha.2.zip`。
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

语言文件位于 `Lang/zh.json` 与 `Lang/en.json`。如果安装了 Generic Mod Config Menu，也可以在配置菜单里切换语言。

## 多人联机

所有玩家必须安装相同版本的 SVSAP。使用 SVSAPME 时，主机与客机也必须安装相同版本的 SVSAPME。网络数据由主机保存，客机的网络操作会发送给主机执行。

## 源码目录

- `src/`：模组源码
- `assets/`：物品与机器贴图
- `Lang/`：语言文件
- `使用教程与各机器作用.txt`：玩家教程和机器说明

## 控制台命令

- `svsap_m1_ids`：列出 SVSAP 物品 ID 和存储元件容量。
- `svsap_selftest`：运行运行时自测。

## 注意

SVSAP 使用模组 ID `Koizumi.SVSAP`。旧测试版 ID 不做迁移；公开游玩前建议备份存档。
