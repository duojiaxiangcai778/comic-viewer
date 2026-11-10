# 变更记录

## 2026-10-31 — v1.0.0 首个可用版本

### 新增

- **`ComicViewer.exe`** 连续看图器
  - 漫画模式：识别 `%TEMP%\360zip$Pic\360$N\` 场景，解析 `config.ini` 拿完整清单，直接从源压缩包按需解压
  - 左右翻页 / 首尾循环 / 键盘导航 / 缩放 / 全屏 / 拖拽平移 / 信息条
  - 转发模式：非漫画场景图片透明交给原看图器（2345 看图王）
- **`--install` 接管安装**
  - 改写 `HKCU\Software\Classes\<ProgID>\shell\open\command`，不动 `UserChoice`
  - 自动备份原命令到 `ComicViewer.backup.ini`
  - 实测接管 5 项：`.png` `.jpg` `.jpeg` `.dib` `.webp`
- **`--uninstall` 还原** / **`--status` 状态查询**

### 修复

1. **PEB 读进程命令行失败**（`UNICODE_STRING.Buffer` 在 x86 下位于 +4，误按 +8 取值）
   → 主路径改为 WMI（`Win32_Process.CommandLine`），PEB 降为兜底。
   实测 WOW64 下读出的 `PEB+0x10` 指向 MZ 头（ImageBase），结构不可靠。
2. **小图不放大**：`fit` 缩放原先限制 `scale > 1` 不放大，改为上限 4 倍。
3. **重复安装导致"原看图器"指向自己**（严重）
   - 现象：第二次 `--install` 时 HKCU 里已经是本程序，被当成"原值"写进备份
     `viewer=E:\...\ComicViewer.exe`，此后转发普通图片会调用自己 → 自噬死循环。
   - 修复三处：
     1. 记录 `viewer` 时跳过自身（`IsSelf`）
     2. 读取时若 `viewer` 指向自己，从 `assoc:` 备份条目自动恢复
     3. `Forward` 再加一道 `IsSelf` 防呆，绝不自调用
   - 已验证：普通 jpg 转发后 2345 打开、ComicViewer 进程数 0。

### 增强（更新免疫）

- 模式判定同时识别目录名 `360zip$Pic` 与同目录 `config.ini`
- 清单不足 2 张时，直接扫描压缩包内全部图片（不依赖 360zip 的协议文件）
- `config.ini` 读取支持 UTF-16 / UTF-8 / ANSI 自动识别

### 验证结果

| 场景 | 结果 |
|---|---|
| 真实 360zip 双击 `02.png` | 弹出 `comic.zip [2/8] 02.png` ✓ |
| 连续翻页 `2/8 → 4/8 → 2/8` | 双向、回绕正常 ✓ |
| ShellExecute 走真实关联 | 自动识别 8 张 ✓ |
| 无 `config.ini` 只给 1 张 | 自动扫描压缩包，`5/8` ✓ |
| 普通 jpg 双击 | 转交 2345 看图王 ✓ |

### 已知限制

- `.gif` / `.bmp` 被系统照片 AppX 占用，无法通过 ProgID 覆盖
- `.rar` / `.7z` 代码路径已实现，本机缺 7-Zip/WinRAR，未真机验证

## 2026-10-31（二）打通 360zip + 2345 连续看图

### 新增
- IFEO 劫持：HKLM\...\Image File Execution Options\2345PicViewer.exe -> ComicViewer
- HandoffTo2345()：定位压缩包 -> 铺全部图片到 360zip 临时目录 -> 启动 2345.real.exe
- ExtractAllImages()：zip 逐条解压 / rar 7z 走 External 缓存
- IsGdiPlusImage()：webp/heic 交给 2345，png/jpg 走自研看图器
- ArchiveFromRunning360zip()：WMI 读 360zip 命令行定位压缩包
- Log()：%TEMP%\ComicViewer.log 诊断日志
- Installer 增加 IFEO 安装/卸载/状态

### 修复
- 360zip$Temp 目录未被识别为图片模式（原只认 360zip$Pic）
- IFEO 指向根目录旧 exe 导致「秒退无日志」+ 转发死循环
- 转发时优先走 .real.exe，避免 IFEO 自噬

### 验证
- 真实 360zip 双击 webp -> 2345PicViewer.real 001.webp 第1/83张，可翻页
- 临时目录铺图 83 张成功

### 留痕
- 工作记录\2026-10-31-打通360zip与2345连续看图.md

## 2026-11-01 — v1.1.0 抗清理加固（IFEO 被删事故）

### 根因
- 10-31 装的 IFEO 劫持键被清掉（HKLM\...\Image File Execution Options\2345PicViewer.exe 不存在），
  360zip 重新直启 2345 → 退回单张。ComicViewer.log 从 11-01 00:00 后无任何记录。

### 新增
- **本体替换**：D:\D\2345pic\2345PicViewer.exe 直接换成 ComicViewer.exe（原文件备份为
  2345PicViewer.exe.orig-20261101）。不依赖注册表，清理工具再删 IFEO 也不影响。
- Settings.MainDir / ConfigPath 回落：副本与主程序不同目录时共用主目录配置。
- ComicViewer.ini 的 original_viewer 改指 2345PicViewer.real.exe。

### 验证
- IFEO 在位 → 剥离 → Handoff → .real.exe 起来 ✓
- IFEO 删除 → 普通启动 → Handoff → .real.exe 起来 ✓

### 留痕
- 工作记录\2026-11-01-IFEO被清理与本体替换加固.md
