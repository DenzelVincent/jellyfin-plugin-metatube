<h1 align="center">Jellyfin Plugin MetaTube</h1>
<p align="center"><b>English | <a href="README.ja.md">日本語</a></b></p>

> **本仓库是 [metatube-community/jellyfin-plugin-metatube](https://github.com/metatube-community/jellyfin-plugin-metatube) 的个人维护分支。**
> 在上游基础上补充了角标组合、无码/中文字幕检出以及翻译链路改造，详见下方[本地新增功能](#本地新增功能)。

<p align="center">
<img alt="Plugin Banner" src="https://metatube-community.github.io/images/banner-dark.png"/>
<br/>
<br/>

<a href="https://github.com/DenzelVincent/jellyfin-plugin-metatube/actions">
<img alt="GitHub Workflow Status" src="https://img.shields.io/github/actions/workflow/status/DenzelVincent/jellyfin-plugin-metatube/publish-plugin.yml?branch=main&logo=github">
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

按文件名分词匹配关键字 `U`、`UC`、`AI`、`uncensored`、`无码`、`破解`、`LTX`、`wm`、`restored`、`jasna`，例如 `SONE-263-U`、`ABC-123-UC`、`SSNI-830_restored_LTX_wm`，关键字列表可自定义。

番号提取由插件在客户端完成：刮削时优先从真实文件路径用番号正则抽出纯番号（`259LUXU-1795_jasna-0.11.3_ltx_wm` → `259LUXU-1795`、`SSNI-830_restored_LTX_wm` → `SSNI-830`），只把番号发给服务端。抽不到标准番号的冷门格式（FC2、maker 前缀、无横线番号等）不做猜测，仅去掉破解/字幕后缀后原样交给服务端自己的清洗逻辑兜底。内置标记清单不依赖已保存的旧配置。

### DMM 高清封面（可选）

JavBus / JAV321 等 DMM 系源的 `cover_url` 实际是 `pics.dmm.co.jp` 的 800×538 转存图，同一作品在 `awsimgsrc.dmm.co.jp` 上有 2184×1468 的版本。打开「Use DMM HD covers」后，插件把封面地址改写成高清版再交给服务端取图：Jellyfin 按 2:3 裁切后仍有约 978×1468，而不是 358×538。

- 默认关闭；只改写文件名恰为 `<cid>pl.jpg` 的 DMM 封面，剧照、缩略图与非 DMM 图床一律不动，取不到作品编号时保持原图。
- **打开开关后已有封面不会自动重下**：本地 `poster.jpg` 是按旧地址落盘的，需要对这些条目「刷新图片」（或删掉媒体目录里的 poster）才会重新取高清版。这是有意的——切换开关不触发整库重抓。
- 这是上游 [issue #653](https://github.com/metatube-community/jellyfin-plugin-metatube/issues/653) 的插件侧前置实现。若服务端用本 fork 的 `metatube-sdk-go` 自建，可以改在服务端开 `DMM_HD_COVER=true`（读取时改写 `cover_url`、不落库），插件的这个开关即可关掉。

### 翻译（多选）

目标语言固定 `zh-CN`。配置页勾选哪几项就翻译哪几项，没有「单选模式」，也没有标题/演员/类别替换表；全不勾表示不翻译。

| 勾选项 | 影响范围 |
| --- | --- |
| 翻译标题 | 条目页标题与 `{title}` 模板变量 |
| 翻译简介 | 条目页简介 |
| 翻译系列名 | 合集名、系列标签与 `{series}` 模板变量 |
| 翻译类别 | 影片类别 |
| 翻译演员名 | 演员条目显示名（pid 按原名搜索，头像取演员详情接口） |

- 不再有「原文已含汉字就跳过翻译」的判定：勾选即调用引擎，日文标题/简介不会因为含汉字被误判成中文。
- 标题、简介每次刮削都直译；系列、类别、演员属于术语，先查服务端字典，命中就不再调引擎，未命中才翻译并回写（首写优先，并发时以服务端返回的生效译文为准）。片商与厂牌不参与翻译，保持原文。
- 演员名按「A（B）」拆分：B 是含假名的艺名，**搜索先用 B**；A 是数据源已转好的汉字名，存在时直接作为 B 的译名入库，不存在才走翻译。页面显示受「翻译演员名」控制：开启显示译名，关闭显示艺名 B。原始字符串不删改，艺名、假名、拼音、译名与改名后的写法都通过服务端**演员别名表**挂到同一身份，同一部片只保留一位。多个来源命中时按数据质量选身份（MINNANO → AVBASE → AV-LEAGUE/ThePornDB → Gfriends → Wikipedia），Gfriends 只作图片兜底。新建演员合集时按显示名查表取合集名（开启翻译取译名），已有合集不自动改名。两个不同演员译成同一个名字时后出现者保留原名，避免被合并少人。该表需要新版服务端；旧服务端上自动退化为按演员搜索解析。
- 术语字典写在服务端 SQLite 的 `translation_metadata` 表。服务端需以文件库启动（`-dsn /data/metatube.db`，并把 `/data` 挂成持久卷）才能跨重启保留；默认的内存库重启即丢，翻译功能仍可用，只是会重新调用引擎。
- 服务端是旧版本（没有 `/v1/translations/term`）时只记一条 Warn，本进程内跳过字典、直接翻译且不落库，刮削不受影响。
- 演员显示译名后，Jellyfin 会按名字生成新的中文演员条目，旧日文条目由「清理无效演员」任务回收；演员合集也按译名分组。同名演员只按名字合并，不区分 provider。
- **已刮过的条目不会自动改名**，需要「替换所有元数据」或「识别」级别的刷新；默认的「扫描新增和更新文件」对已刮过且文件没变的条目不会调用元数据提供者。旧合集不会被自动重命名或合并，重新生成后按译名重建。
- 旧版的「翻译模式」下拉、系列译文对照表与三套替换表已移除，不迁移旧配置，也不做新旧名字归一。

免费翻译仍保留长文本分段（600 字符）、20 秒硬超时、失败自动降级到 OpenAI、OpenAI 模型自动探测与字段级容错；重试逻辑不吞 `OperationCanceledException`，服务关闭时可立即中断。

### 演员合集

开启「启用演员合集」后，由插件按 MetaTube 元数据里的演员维护合集：每位演员一个合集，一部影片可同时归属多个演员合集。合集有两条更新入口：单片元数据刷新/识别落库后几秒内，按该片涉及的演员做一次增量同步（新片会立刻进入对应演员合集）；每日 4:00 的计划任务「同步演员合集」仍全库兜底（成员移除、演员改名、停机期间的变化等）。

- 手动刷新单片时，选择「替换所有元数据」或「识别」会真正重跑刮削并触发增量同步；默认的「扫描新增和更新文件」对已刮过且文件没变的条目不会调用元数据提供者，因此既不会重新翻译，也不会触发合集同步。
- 增量同步只处理「本片当前演员」的新增与成员对齐；影片重刮后不再参演的演员，其合集里的旧成员要等每日任务移除（插件拿不到可靠的旧演职表，硬猜会误伤手建/改名合集）。
- 带 MetaTube id、但演职表里没有任何演员的影片会归入「无演员影片」合集；单片刷新后几秒内即时加入/移出，合集变空后立即删除。影片重刮后有了演员会自动移出，并进入对应演员合集；只处理 MetaTube 影片，手工加入的非 MetaTube 条目不动。
- 「演员合集最小影片数」默认 3，设为 1 即每位演员都建。**只门控新建**：跌破阈值不回收已有合集，已存在的合集继续对齐成员；除「影片被删光的空合集」外，合集不会被自动删除。
- 空合集会自动清理：删掉影片后几秒内，没有任何存活成员的合集连同自己的目录一起删除（日志 `Remove empty collection`）。**不区分是不是演员合集**——只要空了就清。
- 合集成员取该演员在库里的全部参演影片，与演员详情页同一口径，不受本次刮削范围限制；**移除**只限本次任务扫到的 MetaTube 影片，你手工加进合集的其它条目一律不动。
- 合集名取演员显示名原样，因此会跟随「启用真实演员名」与演员翻译的结果；演员改名后旧合集会被对齐到剩余成员，留下的空壳需手工删除。
- 合集封面**对齐该演员在 Jellyfin「演员」页显示的那张主图**：演员页换图后几秒内自动跟随（不必等任务），每轮任务也会再对齐一次兜底；一致则跳过。演员条目没有本地封面时，才在合集缺图时回落到 MetaTube 相册第一张。
- 相册回落与「搜索网络图片」列表依赖演员条目上的 MetaTube 编号：任务先读演员条目记录的编号（本插件刮削影片时已经顺带写入），读不到时按演员名现搜一次并写回条目，之后不再搜。
- 仅 Jellyfin 提供该能力，Emby 端不出现这两个配置项。

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
