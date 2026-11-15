# 更新日志 · Changelog

版本号与 GitHub Release 的 tag 一致，日期为本地时间（Asia/Shanghai）。

---

## 1.0.0 — 2026-11-15

首个公开版本。

**功能**

- 单文件 .NET Framework 4.8 服务，自带 HTTP 服务器与网页，不需要 Docker / Python / Node
- 网页上传、暂存确认、排队、实时进度 / 速度 / 剩余时间、任务日志、取消与删除
- 5 套编码方案：AMD VCE H.264/H.265、x264 veryfast、x265 ultrafast、x264 ultrafast
- 画质（CQ/CRF）、分辨率、帧率、音频、画面裁剪可调；高级参数原样透传给 HandBrakeCLI
- 画面裁剪：保持原画面（显式传 `--crop 0:0:0:0`）或自动裁黑边
- 字幕：不处理 / 烧录 / 软字幕；中文轨道优先；外挂 srt/ass/ssa 自动配对；
  ASS 经 ffmpeg 无损封装后由 libass 烧录以保留样式；PGS/VobSub 自动改为烧录
- 任务持久化（重启保留、中断标记）、inbox 目录监控、Range 下载、CPU/GPU 占用显示
- 一键脚本：防火墙放行、开机自启、启动 / 停止 / 状态

**发行包**

- `-full`：含 HandBrakeCLI 1.11.2 与 FFmpeg，解压即用（压缩包约 46 MB，解压后约 132 MB）
- `-lite`：仅本项目程序，首次运行用 `scripts/get-binaries.ps1` 下载依赖（约 350 KB）
- `SHA256SUMS.txt`：下载后可用 `Get-FileHash` 或 `certutil -hashfile` 校验

**许可**

- 项目自身代码由 MIT 改为 **GPL-3.0-or-later**
- 新增 `THIRD-PARTY-NOTICES.md` 与 `licenses/`（GPL-2.0、GPL-3.0 原文）

### English

First public release.

- Single-file .NET Framework 4.8 server with a built-in HTTP server and web UI; no Docker, Python or Node required
- Five encoding presets (AMD VCE H.264/H.265, x264 veryfast/ultrafast, x265 ultrafast)
- Quality / resolution / frame rate / cropping / audio controls, plus verbatim custom HandBrakeCLI arguments
- Cropping: keep the source picture (`--crop 0:0:0:0`) or auto-crop black bars
- Subtitles: none / burn-in / soft, Chinese track preference, external SRT/ASS/SSA pairing,
  ASS styles preserved through a lossless ffmpeg mux, PGS/VobSub switched to burn-in
- Persistent job queue, inbox watcher, Range downloads, live CPU/GPU meters
- Windows helper scripts for firewall, autostart, start/stop/status

Assets: `-full` bundle (HandBrakeCLI 1.11.2 + FFmpeg, ~46 MB download / ~132 MB unpacked),
`-lite` bundle (program only, ~350 KB) and `SHA256SUMS.txt`.

License: moved from MIT to **GPL-3.0-or-later**; third-party notices and license texts are
included in every bundle.
