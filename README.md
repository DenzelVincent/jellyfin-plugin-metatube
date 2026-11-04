<h1 align="center">Jellyfin Plugin MetaTube</h1>
<p align="center"><b>English | <a href="README.ja.md">日本語</a></b></p>

> **本仓库是 [metatube-community/jellyfin-plugin-metatube](https://github.com/metatube-community/jellyfin-plugin-metatube) 的个人维护分支。**
> 在上游基础上补充了角标组合、无码/中文字幕检出以及翻译链路改造，详见下方[本地新增功能](#本地新增功能)。

<p align="center">
<img alt="Plugin Banner" src="https://metatube-community.github.io/images/banner-dark.png"/>
<br/>
<br/>

<a href="https://github.com/DenzelVincent/jellyfin-plugin-metatube/actions">
<img alt="GitHub Workflow Status" src="https://img.shields.io/github/actions/workflow/status/DenzelVincent/jellyfin-plugin-metatube/dotnetcore.yml?branch=main&logo=github">
</a>
<a href="https://github.com/DenzelVincent/jellyfin-plugin-metatube/search?l=c%23">
<img alt="GitHub top language" src="https://img.shields.io/github/languages/top/DenzelVincent/jellyfin-plugin-metatube?color=%23239120&label=.NET&logo=csharp">
</a>
<a href="https://github.com/DenzelVincent/jellyfin-plugin-metatube/blob/main/LICENSE">
<img alt="License" src="https://img.shields.io/github/license/DenzelVincent/jellyfin-plugin-metatube">
</a>
<a href="https://github.com/DenzelVincent/jellyfin-plugin-metatube">
<img alt="gitHub Stars" src="https://img.shields.io/github/stars/DenzelVincent/jellyfin-plugin-metatube?style=flat">
</a>
<a href="https://github.com/DenzelVincent/jellyfin-plugin-metatube">
<img alt="Downloads" src="https://img.shields.io/github/downloads/DenzelVincent/jellyfin-plugin-metatube/total">
</a>
<a href="https://github.com/DenzelVincent/jellyfin-plugin-metatube/releases">
<img alt="Releases" src="https://img.shields.io/github/v/release/DenzelVincent/jellyfin-plugin-metatube?include_prereleases&logo=smartthings">
</a>
</p>

## About

MetaTube Plugin for Jellyfin/Emby.

## Features

- Full Metadata: Including title, overview, genres, director, actors, and studio.
- Full Search: Support searching for movies and actors across various providers.
- Trailer Video: Support trailers without downloading the full trailer videos.
- Scheduled Task: Automatically organize metadata genres and update plugin.
- Face Detection: Cut primary image with face centered by face detection engine.
- Auto Translation: Support translate certain metadata to preferred language.

## Platforms

[![Jellyfin](https://img.shields.io/static/v1?color=%2300A4DC&style=for-the-badge&label=Jellyfin&logo=jellyfin&message=10.11.x)](https://jellyfin.org/)
[![Emby](https://img.shields.io/static/v1?color=%2352B54B&style=for-the-badge&label=Emby&logo=emby&message=4.9.x)](https://emby.media/)

_NOTE: This project will only support stable versions._

## Documentation

- [Plugin installation](https://metatube-community.github.io/wiki/plugin-installation/)
- [Server deployment](https://metatube-community.github.io/wiki/server-deployment/)
- [File naming rules](https://metatube-community.github.io/wiki/naming-rules/)
- [Auto translation](https://metatube-community.github.io/wiki/auto-translation/)
- [Build from source](https://metatube-community.github.io/wiki/build-from-source/)
- [Metadata providers](https://metatube-community.github.io/wiki/metadata-providers/)

Full documentation and examples can be found at [Wiki](https://metatube-community.github.io/wiki/).

## Community

Welcome and feel free to ask any questions at [Discussions](https://github.com/metatube-community/jellyfin-plugin-metatube/discussions).

## 联动项目

本插件是 MetaTube 链路的客户端，与另外两个项目配合使用：

| 项目 | 角色 | 地址 |
| --- | --- | --- |
| metatube-sdk-go | 刮削服务端，提供搜索、详情、图片与翻译 REST API；勾选「启用字段级聚合」需服务端 ≥ v1.7.0 | 源码 https://github.com/DenzelVincent/metatube-sdk-go <br> 镜像 https://ghcr.io/ming0feng/metatube-server |
| jellyfin-plugin-metatube | 本插件：Jellyfin / Emby 元数据与图片提供程序 | https://github.com/DenzelVincent/jellyfin-plugin-metatube |
| emby-subtitlecat | Emby 字幕插件，依赖本插件的刮削结果定位番号并补「中文字幕」角标 | https://github.com/Ming0feng/emby-subtitlecat |

联动方式：

1. 本插件在 `服务器地址` 指向的 MetaTube Server 上调用 `/v1/movies/search` 与 `/v1/movies/{provider}/{id}` 完成刮削，并把结果写入条目的 `ProviderIds["MetaTube"]`，格式为 `<Provider>:<Id>[:<Position>[:<Update>]]`。
2. 主图、缩略图、背景图均通过服务端 `/v1/images/primary|thumb|backdrop/{provider}/{id}` 按需生成，`badge` 参数决定角标（如 `zimu.png,tr:uncensored.png`）。
3. emby-subtitlecat 只处理带 `MetaTube` ProviderId 的条目，从该值解析 `<Provider>:<Id>` 得到番号去 subtitlecat.com 找字幕；下载完成后读取本插件的 `MetaTube.json` 配置（`Server`、`EnableBadges`、`BadgeUrl`、`EnableUncensoredBadges`、`PrimaryImageRatio`），直接请求 `/v1/images/primary/...` 重设主图以补上「中文字幕」角标。

因此三个项目需指向同一个 MetaTube Server 实例，并保持 `服务器地址` 一致。

## 本地新增功能

以下功能为个人分支新增，上游原版不包含。

### 角标组合

主图可同时叠加两个角标：左上「中文字幕」、右上「无码破解」。服务端的 badge 参数以逗号分隔，`tr:` 前缀表示右上角，例如 `zimu.png,tr:uncensored.png`。角标图片地址、无码关键字均可配置。

### 中文字幕检出

判断条目是否已有中文字幕，命中任一条件即视为有：

- 类型（Genre）中包含「中文字幕」
- 文件名带分隔符标记：`-C`、`-UC`、`-ch`、`中文字幕`
- 同目录存在 `ass/srt/ssa/smi/sub/idx/psb/vtt` 外挂字幕

### 无码 / 破解检出

按文件名分词匹配关键字 `U`、`UC`、`AI`、`uncensored`、`无码`、`破解`，例如 `SONE-263-U`、`ABC-123-UC`、`XYZ-AI`，关键字列表可自定义。

### DMM 高清封面（可选）

JavBus / JAV321 等 DMM 系源的 `cover_url` 实际是 `pics.dmm.co.jp` 的 800×538 转存图，同一作品在 `awsimgsrc.dmm.co.jp` 上有 2184×1468 的版本。打开「Use DMM HD covers」后，插件把封面地址改写成高清版再交给服务端取图：Jellyfin 按 2:3 裁切后仍有约 978×1468，而不是 358×538。

- 默认关闭；只改写文件名恰为 `<cid>pl.jpg` 的 DMM 封面，剧照、缩略图与非 DMM 图床一律不动，取不到作品编号时保持原图。
- 这是上游 [issue #653](https://github.com/metatube-community/jellyfin-plugin-metatube/issues/653) 的插件侧前置实现；服务端提供同名开关后应改回服务端配置。

### 翻译链路改造

| 改动 | 说明 |
| --- | --- |
| 长文本分段 | 超过 600 字符的文本按句末标点/`<br>` 边界切分后逐段翻译再合并，避免单次请求过长被拒 |
| 硬超时 | 免费翻译单次请求 20 秒硬超时（含内部重试），不再把刷新拖到分钟级 |
| 自动降级 | 免费翻译超时、429、5xx 等任意失败都会自动改用 OpenAI 兼容引擎重试一次 |
| 模型自动探测 | OpenAI 模型留空时，自动请求 `{api-url}/models` 选取首个可用对话模型并记录日志 |
| 字段级容错 | 标题与简介独立处理，任一字段失败只保留该字段原文，不影响另一个 |
| 取消语义修复 | 重试逻辑不再吞掉 `OperationCanceledException`，服务关闭时可立即中断 |

### 演员合集

开启「启用演员合集」后，计划任务「同步演员合集」按 MetaTube 元数据里的演员维护合集：每位演员一个合集，一部影片可同时归属多个演员合集。合集由任务维护而不是刮削链路，因此频次就是该任务的定时触发（默认每日 4:00）。

- 「演员合集最小影片数」默认 3，设为 1 即每位演员都建。**只门控新建**：跌破阈值不回收已有合集，已存在的合集继续对齐成员，合集永不被自动删除。
- 合集成员取该演员在库里的全部参演影片，与演员详情页同一口径，不受本次刮削范围限制；**移除**只限本次任务扫到的 MetaTube 影片，你手工加进合集的其它条目一律不动。
- 合集名取演员显示名原样，因此会跟随「启用真实演员名」与演员替换表的结果；演员改名后旧合集会被对齐到剩余成员，留下的空壳需手工删除。
- 合集封面**每轮对齐该演员在 Jellyfin「演员」页显示的那张主图**：任务把演员条目的本地封面复制成合集自己的封面，一致则跳过；演员换头像后下次运行自动跟随。演员条目没有本地封面时，才在合集缺图时回落到 MetaTube 相册第一张。
- 相册回落与「搜索网络图片」列表依赖演员条目上的 MetaTube 编号：任务先读演员条目记录的编号（本插件刮削影片时已经顺带写入），读不到时按演员名现搜一次并写回条目，之后不再搜。
- 仅 Jellyfin 提供该能力，Emby 端不出现这两个配置项。

### 系列译文

「翻译系列名」把 `m.Series` 整体翻译，合集名、系列标签与 `{series}` 模板变量三处一起变成译文。译文写进「系列译文对照表」（每行 `原文=译文`），命中即不再调用翻译引擎——合集靠名字匹配，同一系列换译法会在库里裂成两个合集。

- 等号右侧留空表示这个系列保留原文；误译直接在表里改一行，下一张影片即生效。
- 开启后**已刮过的影片不会自动改名**，需要全库覆盖刷新元数据才生效；旧的日文合集不会被自动删除或合并，需手工清理。
- 库内建议统一元数据语言：`zh-CN` 与 `zh-TW` 会各译一遍，同一系列可能裂成两个合集；目标语言为 `ja` 时该字段保留原文。
- Emby 端查表与调引擎都工作，但引擎新译的结果**不会自动写回表**，需要手工维护对照表。
- 首次全库刷新元数据期间**不要停留在插件设置页**：保存配置会把整份配置按页面打开时读到的值写回去，这段时间里刮削自动新增的对照表行会被旧内容覆盖。等刷新结束再打开页面即可避免。

### 插件存储库与分发指向

本分支的构建产物、Release、catalog 与程序集元数据全部指向 `DenzelVincent/jellyfin-plugin-metatube`，与上游仓库互不干扰；插件标识（GUID、名称、provider id）与上游保持一致，因此**服务端只保留这一个仓库条目**，上游 MetaTube 仓库条目必须删除，否则两边同 GUID 会互相覆盖版本。

仓库地址（控制台 → 插件 → 存储库）：

- 主：`https://raw.githubusercontent.com/DenzelVincent/jellyfin-plugin-metatube/dist/manifest.json`
- 备选（国内可达性）：`https://cdn.jsdelivr.net/gh/DenzelVincent/jellyfin-plugin-metatube@dist/manifest.json`

已知残留风险：插件 zip 仍从 `github.com/.../releases/download` 取，GitHub 下载不可用时安装会失败，换 catalog 地址解决不了这一步。

升级 Jellyfin 大版本前，先手跑工作流 `Jellyfin 预览编译`（填目标 SDK 版本与配套框架），编不过就先别升服务端。

## Licence

This plugin is released under the [MIT](https://github.com/DenzelVincent/jellyfin-plugin-metatube/blob/main/LICENSE) License.

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=DenzelVincent/jellyfin-plugin-metatube&type=Date)](https://star-history.com/#DenzelVincent/jellyfin-plugin-metatube&Date)
