# Jellyfin-videotools（服务端）

> **这是 [jellyfin/jellyfin](https://github.com/jellyfin/jellyfin) 的个人 fork，不是官方版本，与主线的区别是能简单处理视频，和对官方功能的少量优化。**
> 所有改动都在 `videotools-12.4` 分支上。

**基线：上游 Jellyfin `v12.4`（2026-10-26 稳定版）。**
本仓库版本号 `12.4.3`：12.4.x末位为本仓库迭代号，与官方不存在对应关系

一句话概括：给 Jellyfin 加了**视频转码**、**剪辑/旋转/画面裁切（抽帧预览）**、**后台任务队列**、**按文件大小、比特率排序**、**搜索词支持扩展名**、**出站代理**，另外补了个 **Emby 兼容层**让第三方 Emby 工具能直连。

> 🤖 **本仓库全部代码由 AI 编写。**

配套前端：[Dimerry/jellyfin-web](https://github.com/Dimerry/jellyfin-web)（分支 `videotools-12.4`，同样基于 `v12.4`；**服务端和前端必须用同一 tag 的一对**，混搭会白屏或接口 404）。

---

## 一、功能改动（按模块）

### 1. 视频工具 API — `Jellyfin.Api/Controllers/VideoToolsController.cs`

新增一整套 `/VideoTools` 接口，全部需要**管理员**权限。前端菜单调用这些接口。

**1.1转码**

- `POST /VideoTools/{itemId}/Transcode?codec=h265|av1&bitrateKbps=3000&resolution=1080p|4k&rateControl=quality|bitrate&quality=medium&encoder=auto`
  单条入队。**不带参数调用时使用存储的转码默认设置**（控制台 → 视频工具 → 转码默认设置）；带任一参数则全部按显式值处理。
- `POST /VideoTools/Transcode` + body `["guid","guid"]`
  批量入队，参数同上，规则同上。
- 编码策略（自动挑选，失败逐级回退）：
  - **H.265**：Intel QSV（`hevc_qsv`）→ NVIDIA NVENC（`hevc_nvenc`）→ 软件 `libx265`；
  - **AV1**：Intel QSV（`av1_qsv`）→ NVIDIA NVENC（`av1_nvenc`）→ 软件 `libsvtav1`（preset 8）；
  - 每个硬件编码器先试「硬解 + 硬编」，再试「软解 + 硬编」（显卡解不了这路视频时还能保住硬编），最后才是纯软件；
  - 硬件编码器连续失败 2 次后，本进程内不再尝试（比如核显没有 AV1 硬编，就不会每个任务都白试一遍）；
  - 实际用到的编码器会显示在「控制台 → 转码队列」的状态下方；回退到软件编码时文案是**「硬件编码不可用，已自动切换为软件编码」**；
  - 想强制指定：环境变量 `VIDEOTOOLS_ENCODER` = `auto`（默认）/ `qsv` / `nvenc` / `software`。
  - 音频统一 AAC 192k，文本字幕转 `mov_text`，位图字幕跳过，加 `+faststart`。
- 分辨率默认 1080p、可选 4K，**只缩不放**（源比目标小就保持原尺寸）。
- **码控模式可选**：默认「质量模式」按固定画质编码（码率随画面复杂度浮动，crf/qvbr/global_quality），可选「码率模式」按客户端请求的固定码率编码；质量档位最高/高/中/低/最低五档。剪辑（裁切）路径固定质量模式+高档，保证裁切后画面与源一致。
- **编码器偏好**：转码/剪辑对话框可手动指定优先编码器（QSV / NVENC / VA-API / AMF / 软件）；指定后仍走回退链，坏了的 GPU 不会让任务无输出。控制台 → 视频工具可存「转码默认设置」和「剪辑默认设置」，从媒体库直接入队（不弹对话框）时生效。
- **AI 插帧（可选）**：源视频低于 23fps 时对话框出现「AI 插帧至 30 帧」勾选，用 minterpolate 运动补偿插帧，观感明显更流畅但速度慢；高帧率源不显示该选项。插帧按片段并行处理，多段同时算，速度成倍提升。

**1.2.1剪辑**

- `POST /VideoTools/{itemId}/Cut?startSeconds=2&endSeconds=3&rotation=0|90|180|270&cropLeft=0&cropRight=0`
- `-ss` 输入端 + `-c copy` 流复制，秒完成、无损。
- **左右百分比裁切**（可选）：填 `cropLeft` / `cropRight`（0-99）按源宽度百分比从两侧裁掉画面；裁切需要重编码（自动走质量模式+高档，H.265/AV1 可选），纯剪断依然流复制。对话框里带实时裁切预览（蓝色框保留区）和裁切后宽高提示。
- **剪辑完成后自动重抽海报**：剪辑是原地顶替文件，旧封面还停留在未剪辑的画面上，所以任务成功后从新文件重新抓一张主图（走 ProviderManager 静默刷新，不触发媒体库扫描）；转码不重抽（内容和时长不变，海报依然准）。
- **旋转**：`rotation` 传 0 / 90 / 180 / 270（逆时针）。用 `-display_rotation`（放在 `-i` 之前）重写 display matrix，配合 `-c copy` 依然是流复制，**不重编码、秒完成**，画面无损。
  注意它是绝对设置：源文件自带旋转元数据（手机竖拍）时会被覆盖成所选角度，而不是叠加。

**1.2.2剪辑预览（抽帧）**

- `GET /VideoTools/{itemId}/Frame?timeSeconds=&maxWidth=480&rotation=0|90|180|270` → 单帧 JPEG。
- 不进任务队列，单帧 0.1~0.3s；`SemaphoreSlim(2)` 限流，避免拖慢实时转码。
- 按 `<itemId>_<源文件mtime>_<宽>_<旋转>_<时间>` 缓存到临时目录，命中约 2ms；超过 400 个自动清到 200 个。


**1.3任务队列**

- `GET /VideoTools/Jobs`、`GET /VideoTools/Jobs/{id}`
- `DELETE /VideoTools/Jobs/Finished`：清掉已完成 / 失败 / 已取消，保留运行中和排队。
- `POST /VideoTools/Jobs/{id}/Retry`：只允许失败任务，沿用原参数、输出名自动递增。
- `POST /VideoTools/Jobs/{id}/Cancel`：排队中直接移除；运行中会终止 ffmpeg 并删掉半成品。
- `GET /VideoTools/Jobs/{id}/Stream`：已完成任务的输出流地址，支持 Range，原生 `<video>` 用 `?ApiKey=<token>` 认证。
- **文件任务串行执行**：同一时间只跑一个转码/剪辑，其余 `Queued` 并带队列位置；播放时的实时转码不经过这个队列，不受限制。
- 队列清空后延迟 **2 秒**触发一次媒体库扫描（2 秒内又来新任务则取消本次扫描，等下一次清空）。

**1.4输出文件命名**

- **转码**：新文件沿用源文件主名 `<源名>.mp4`；成功后源文件改名为 `<源名>.old.<原扩展名>` 保留（重名用 `名字 (1).old.mp4`）。
- **剪辑**：新文件直接顶替源文件 `<源名>.<原扩展名>`，源文件同样改名成 `.old` 保留。
- 两种操作都先写隐藏临时文件 `.<源名>.videotools.*`，成功后才做改名交换，**中途失败不会破坏源文件**；失败/取消会删临时文件。

**1.5转码稳定性补丁**

- **帧率修正**：探测 `avg_frame_rate` / `r_frame_rate`，转码时固定输出 `-r <真实帧率>`（吸附到 23.976 / 24 / 25 / 29.97 / 30 / 48 / 50 / 59.94 / 60 / 100 / 119.88 / 120）。
  原因：很多手机/remux 文件的 `r_frame_rate` 是假的（30fps 报 `120/1`），编码器按假帧率分配码率预算，导致指定 3000k 实测只有 750k 且画面卡顿。
- **编码器回退链**：见上面「编码策略」。规划器 `Jellyfin.Api/Helpers/TranscodePlanner.cs` 先用 `ffmpeg -encoders` 查本机 ffmpeg 支持哪些编码器，再按 QSV → NVENC → 软件 排序，逐个实际执行，第一个成功就停。
  修的是两件事：一是核显没有 AV1 硬编、机器没有 N 卡时不再直接失败；二是 N 卡机器可以直接吃到 `hevc_nvenc` / `av1_nvenc`。
- **解码回退**：`h264 / hevc / av1 / vp8 / vp9 / mpeg2video / vc1 / mjpeg` 以外的一律走软件解码（`scale=` 而不是 `scale_qsv` / `scale_cuda`）。
  修的是 `.avi`(mpeg4/DivX) 直接失败的问题：QSV 解不了 mpeg4，会静默回退成 yuv420p，而 `scale_qsv` 只吃 QSV surface → 滤镜链整体失败。
- 剪辑时长优先用 ffmpeg 探测的真实时长，探测不到才用数据库里的 `RunTimeTicks`（刚剪过的文件数据库时长是旧的）。

### 2. 图片旋转

- `POST /ImageTools/{itemId}/Rotate?degrees=90|180|270`：单张旋转。
- `POST /ImageTools/Rotate?degrees=90` + body `["guid","guid"]`：批量旋转（多选菜单用这个），逐张串行处理并返回每张的结果。
- 只接受 `Photo` 类型；用 ffmpeg `transpose` 滤镜**原地重写文件**（JPEG 质量 2、PNG 无损、WebP libwebp 95），旋转后重新排一次刷新任务，让数据库里的分辨率和缩略图跟上新像素。
- 旋转和转码共用一把锁，避免同时改写同一个文件。

### 3. 孤儿文件清理任务（控制台 → 计划任务）

- `Emby.Server.Implementations/ScheduledTasks/Tasks/OrphanFileCleanupTask.cs`，默认 30 天跑一次。
- 遍历所有媒体库目录，把「已经没有对应媒体文件」的附属文件（poster / fanart / 字幕 / nfo 等）**移动到数据目录下按日期命名的隔离文件夹**，不直接删除，随时可以捞回来。
- 一天以内的新文件不动，目录级（文件夹本身）的图片永远不动。

### 4. Emby 兼容层

- Jellyfin 本身没有 `/emby` 路由前缀，而按 Emby 写的第三方工具（刮削器、统计脚本、Emby 客户端）所有请求都带这个前缀。`Jellyfin.Server/Startup.cs` 里加了一个中间件，路由前把 `/emby` 段剥掉（大小写不敏感），这类工具不用改配置就能直连。
- **注意**：Jellyfin 13 的迁移 `DisableLegacyAuthorization` 默认关闭了旧式认证，`X-Emby-Token` / `X-MediaBrowser-Token` / `api_key=` 查询参数会全部返回 401。这类工具要用起来，需要把数据目录 `config/system.xml` 里的 `EnableLegacyAuthorization` 改成 `true`（冒烟测试用 `api_key=` 查 `/emby/Persons` 即可）。
- 代价是 API key 允许通过 URL 查询参数传递，会出现在日志 / 代理日志里（Jellyfin 自己会把中间几位打码）。局域网自用够用，公网部署请自行权衡。

### 5. 时间戳损坏的 MP4 自动实时转码（`Jellyfin.Api/Helpers/Mp4TimingInspector.cs`）

- 问题：有些 MP4 的视频轨**没有 `ctts`（composition time offset）表**，但码流里明明有 B 帧重排（B-pyramid）。这种文件只能提供“解码顺序”的时间戳，播放器按容器时间戳出帧就会不停丢帧/等帧，表现为**直连播放卡顿掉帧**，而实时转码反而流畅（转码会重建时间轴）。`ffmpeg -c copy` 重封装修不了，必须重编码。
- 做法：生成播放信息时（`MediaInfoHelper.SetDeviceSpecificData`）解析 MP4 的 box 树，若「视频轨没有 `ctts`」且「ffprobe 报告 `has_b_frames > 0`」，就把 `SupportsDirectPlay` / `SupportsDirectStream` 设为 false 并加 `allowVideoStreamCopy=false`，**强制真正的视频重编码**（只重封装不解决问题）。
- 只探测一次并缓存（path + mtime + size），有 `ctts` 的文件直接返回不启动进程；分片 MP4（moof）和 mkv/其它容器不受影响。
- 命中时会打一条日志：`MP4 file ... has B-frames but no composition time offsets (ctts); direct play disabled`。
- 开关：环境变量 `VIDEOTOOLS_NO_CTTS_TRANSCODE=0` 可关闭这套自动转码。
- 扫描脚本：`scripts/scan-ctts.py` 可批量扫媒体库并统计这类文件（输出文件列表和总大小）。

### 6. 排序增强

- `Jellyfin.Data/Enums/ItemSortBy.cs` 新增 `Size = 30`。
- `OrderMapper.cs` 映射 `ItemSortBy.Size → e.Size`。
- 新增 `SizeComparer`、`BitrateComparer` 内存排序比较器（自动注册）。

### 7. 搜索词按扩展名匹配 — `Jellyfin.Library/Search/SqlSearchProvider.cs`

- 搜索词在匹配前会被去掉标点，形如 `.ts` 的词会退化成 `ts`，结果变成「所有标题含 t、s 的条目」。
- 现在形如扩展名的词（点 + 字母数字，支持 `.old.mp4` 这类多段后缀）改按**条目文件路径的后缀**匹配，搜 `.ts` 就真的返回 `.ts` 文件。

---

## 二、构建与运行

环境要求：

- .NET SDK **10.0**（仓库 `global.json` 要求 10.0.0）
- `ffmpeg`（转码/剪辑/抽帧全靠它），建议 6.0+；`ffmpeg -encoders` 用来看本机支持哪些编码器
- **Node.js 20+ / npm**（只在需要自己构建前端时用）
- 想用 QSV 硬编：Intel 核显 + `/dev/dri/renderD128` 权限（把运行用户加进 `render`、`video` 组，并用 `sg render -c ...` 启动）
- 想用 NVENC 硬编：NVIDIA 显卡 + 能正常工作的驱动（`nvidia-smi` 有输出）。**不需要** CUDA Toolkit，ffmpeg 自带 NVENC 接口即可；容器里跑要挂 `--gpus all`
- 两种显卡都没有也能跑：规划器会自动跳过硬件编码器，直接软件编码（H.265 用 `libx265`，AV1 用 `libsvtav1`）

```bash
dotnet restore Jellyfin.Server/Jellyfin.Server.csproj
dotnet build Jellyfin.Server/Jellyfin.Server.csproj -c Release --no-restore

dotnet run --project Jellyfin.Server -c Release -- \
  --datadir /path/to/data \
  --webdir /path/to/jellyfin-web/dist
```

`--webdir` **必须**指向配套前端 [Dimerry/jellyfin-web](https://github.com/Dimerry/jellyfin-web) 的 `dist`（`npm run build:production` 产物）。
不传、或者指向一个空目录 / 不存在的目录，服务端会**直接拒绝启动**并退出，日志里是：

```
The server is expected to host the web client, but the provided content directory is either invalid or empty
```

（默认值是这个 DLL 旁边的 `jellyfin-web` 目录；官方发行包里自带，自己编译的没有。只想跑 API、不需要网页界面时，可以加 `--nowebclient` 跳过这项检查。）

### 常见启动失败

- **`UnauthorizedAccessException: Access to the path '/home/<user>' is denied`（然后疯狂重启、日志暴涨）**
  用 cron / systemd / 某些容器跑的时候，`HOME` 指向了不存在或不可写的目录，Jellyfin 建缓存目录时直接崩。
  解决：显式指定 `HOME`、`XDG_DATA_HOME`、`XDG_CONFIG_HOME`、`XDG_CACHE_HOME` 到一个可写目录（本仓库根目录的 `run-jellyfin.sh` 就是这么干的，可以直接抄）。
- **`The server is expected to host the web client...`**：见上面 `--webdir`。
- **启动后转码立刻失败，日志里有 `Error while opening encoder`**：ffmpeg 这个编码器在编译时带了、但机器上没有对应硬件。这是正常的，规划器会自动跳到下一个编码器；如果连软件编码也失败，才需要看具体报错。
- **端口错误**：端口读的是数据目录里 `config/network.xml` 的 `InternalHttpPort`。

> 默认端口 8096。

### 打包成 .deb（推荐给不想装 .NET 的人）

```bash
export WEB_DIR=/path/to/jellyfin-web/dist   # 可选，默认 ../jellyfin-web/dist
./packaging/deb/build.sh                   # 产物：dist/jellyfin-videotools_<版本>_amd64.deb
```

包是**自包含**的（.NET 运行时一起打进去，约 84MB），目标机器不需要装 SDK/运行时；ffmpeg 用系统的（`Depends: ffmpeg`）。

安装后：

| 项目 | 路径 / 名称 |
| --- | --- |
| 程序 | `/opt/jellyfin-videotools/`（含内置 `jellyfin-web`） |
| 启动器 | `/usr/bin/jellyfin-videotools` |
| 服务 | `systemctl status jellyfin-videotools` |
| 配置 | `/etc/jellyfin-videotools`（`/etc/default/jellyfin-videotools` 改目录/参数） |
| 数据 | `/var/lib/jellyfin-videotools` |
| 缓存 / 日志 | `/var/cache/jellyfin-videotools`、`/var/log/jellyfin-videotools` |
| 端口 | 8096（首次启动后可在控制台改） |

安装时自动建 `jellyfin-videotools` 系统用户，并加入 `video`、`render` 组（存在才加），所以 QSV 开箱能用。
`dpkg --purge` 默认**保留**媒体库数据库（`JELLYFIN_PURGE_DATA=1 dpkg --purge ...` 才删）。

**系统要求**：x86-64，Debian 12+ / Ubuntu 22.04+（glibc ≥ 2.33）。
自包含包里的 `libe_sqlite3.so` 是拿新 glibc 编译的，在 Debian 11 这种老系统上会在启动时抦在 `DllNotFoundException: e_sqlite3`，不是配置问题。

---

## 三、免责声明

- 个人自用 fork，代码质量按"能跑就行"标准，**没有测试覆盖**，不保证数据安全。
- 转码/剪辑会**移动和改名你的媒体文件**（改成 `.old`），请先在小范围试。
- 与 Jellyfin 官方无关，问题不要提到上游。

## 四、许可

沿用上游 [GPLv2](LICENSE)。
