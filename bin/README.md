# bin\ — 第三方程序放这里

本仓库**不包含**这两个二进制文件（体积大、且属于第三方 GPL/LGPL 程序），首次使用请运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\get-binaries.ps1
```

脚本会自动下载并放进本目录：

| 文件 | 来源 | 用途 | 必需 |
| --- | --- | --- | --- |
| `HandBrakeCLI.exe` | <https://github.com/HandBrake/HandBrake/releases>（`HandBrakeCLI-<ver>-win-x86_64.zip`） | 转码引擎 | 是 |
| `ffmpeg.exe` | <https://www.gyan.dev/ffmpeg/builds/>（essentials build） | 仅 ASS 保样式烧录时做无损封装 | 否* |

\* 没有 `ffmpeg.exe` 时，ASS/SSA 字幕会被转成纯文本 SRT 烧录，样式会丢失；其余功能不受影响。

想手动准备的话，把官方 zip 里的这两个 exe 复制到本目录即可。也可以在 `data\config.json` 里用
`handbrake` / `ffmpeg` 字段指向别处的绝对路径。

> 版本参考：本项目在 HandBrakeCLI **1.11.2** 上开发并测试。
