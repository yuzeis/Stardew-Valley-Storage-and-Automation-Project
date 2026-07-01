# Stardew Valley Storage and Automation Project (SVSAP)

版本：1.1  
版本代号：Gnadenempfang  
适配：Stardew Valley 1.6 / SMAPI

SVSAP 为星露谷物语加入网络存储、数字存储元件、导入/导出总线、样板编码、机器处理流水线和自动合成系统。

## 下载与安装

正式发布包在 `Release/SVSAP 1.1.0.zip`。

安装方法：

1. 安装 SMAPI。
2. 解压 `Release/SVSAP 1.1.0.zip`。
3. 将解压出的 `SVSAP` 文件夹放入 `Stardew Valley/Mods/`。
4. 通过 SMAPI 启动游戏。

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

所有玩家必须安装相同版本的 SVSAP。网络数据由主机保存，客机的网络操作会发送给主机执行。

## 源码目录

- `src/`：模组源码
- `assets/`：物品与机器贴图
- `Lang/`：语言文件
- `art_preview/`：美术预览
- `Release/`：编译好的发布压缩包
- `使用教程与各机器作用.txt`：玩家教程和机器说明

## 控制台命令

- `svsap_m1_ids`：列出 SVSAP 物品 ID 和存储元件容量。
- `svsap_selftest`：运行运行时自测。

## 注意

SVSAP 使用模组 ID `Koizumi.SVSAP`。旧测试版 ID 不做迁移；公开游玩前建议备份存档。
