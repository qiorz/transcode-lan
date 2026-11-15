# 第三方组件声明 · Third-Party Notices

发行包 `transcode-lan-<版本>-win-x64-full.zip` 中随附以下两个第三方程序。它们都是**官方发布、未做任何修改**的构建，
只通过命令行调用（`HandBrakeCLI.exe -i ... -o ...`、`ffmpeg.exe -i ... -c copy ...`），与本项目属于
**聚合（aggregation）** 关系，而不是链接成一个程序。

> The `-full` release bundle ships the two third-party programs below. Both are **unmodified
> official builds**, invoked only through the command line, and are aggregated with this
> project rather than linked into it.

---

## 1. HandBrakeCLI 1.11.2

| 项目 / Item | 内容 / Details |
| --- | --- |
| 许可 / License | GNU General Public License v2.0（原文见 `licenses/GPL-2.0.txt`） |
| 版权 / Copyright | © HandBrake contributors |
| 项目主页 / Home | <https://handbrake.fr/> |
| 二进制来源 / Binary | <https://github.com/HandBrake/HandBrake/releases/tag/1.11.2>，资产 `HandBrakeCLI-1.11.2-win-x86_64.zip` |
| 对应源码 / Source | <https://github.com/HandBrake/HandBrake/tree/1.11.2> |
| 修改 / Modifications | 无 / none |

## 2. FFmpeg（gyan.dev essentials 构建，`N-92722-gf22fcd4483`）

| 项目 / Item | 内容 / Details |
| --- | --- |
| 许可 / License | GNU General Public License v3.0 —— 该构建启用了 `--enable-gpl --enable-version3`（原文见 `licenses/GPL-3.0.txt`） |
| 版权 / Copyright | © FFmpeg developers |
| 项目主页 / Home | <https://ffmpeg.org/> |
| 二进制来源 / Binary | <https://www.gyan.dev/ffmpeg/builds/>，`ffmpeg-release-essentials.zip`；构建标识 `N-92722-gf22fcd4483`（2018 年） |
| 对应源码 / Source | <https://github.com/FFmpeg/FFmpeg/tree/f22fcd4483> 或 <https://ffmpeg.org/releases/> |
| 修改 / Modifications | 无 / none |

---

## 3. 本项目 · This project

Transcode LAN 自身代码以 **GPL-3.0-or-later** 发布（见仓库根目录 `LICENSE`），源码：
<https://github.com/qiorz/transcode-lan>。

The Transcode LAN code itself is licensed under the **GPL-3.0-or-later**
(see `LICENSE` in the repository root). Source: <https://github.com/qiorz/transcode-lan>.

## 4. 获取源码 · Obtaining the source

- 本项目源码：上述仓库，任意 tag 都能拿到对应版本的完整代码。
- 第三方源码：使用表格中的地址；`N-92722-gf22fcd4483` 这类历史构建的源码也可在
  <https://ffmpeg.org/releases/> 找到对应快照。
- 如果你需要与发行包中二进制**逐字节对应**的第三方源码副本，请在仓库提 issue，我们会提供打包下载
  （GPL 分发义务）。

If you need a copy of the exact third-party source corresponding to the binaries in a
release, open an issue in the repository and we will provide it (GPL distribution
obligation).
