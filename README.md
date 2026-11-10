# 360zip 国际版 · 压缩包内连续看图插件

> 状态：**已打通（2026-10-31），2026-11-01 做了抗清理加固（IFEO 被删事故）**
> 目标软件：360 Zip（国际版）1.0.0.1041，安装于 `D:\D\360zip`
> 成品：`ComicViewer.exe`（.NET Framework 4.8，34KB，零外部依赖）
> 看图器：2345 看图王 `D:\D\2345pic\2345PicViewer.exe`

## 一、这个项目要解决什么

360 Zip 里打开压缩包（漫画包），图片只能**一张一张点开**看，没有左右翻页。
目标：

1. 压缩包内图片**连续浏览**。
2. 做成**不修改 360zip 本体**的插件，**360zip 升级后不失效**。

## 二、逆向结论（关键事实，全部实测）

### 2.1 360zip 有两条完全不同的图片打开路径

| 格式 | 临时目录 | config.ini | 启动方式 |
|---|---|---|---|
| 内置支持（png/jpg/gif/bmp/tif…） | `%TEMP%\360zip$Pic\360$N` | **有** | `ShellExecuteW` 走文件关联 |
| 内置不支持（**webp** 等） | `%TEMP%\360zip$Temp\360$N` | **没有** | **直接 CreateProcess 拉起 2345** |

webp 那条路上，360zip 的命令行实测为：

```
"D:\D\2345pic\2345PicViewer.exe" "C:\...\Temp\360zip$Temp\360$0\001.webp"
```

**它绕过 ShellExecute、绕过文件关联**，所以改注册表 ProgID 对它完全无效。

### 2.2 为什么改注册表没用

以下位置全部实测改过或查过，360zip 一律无视：

- `HKCU/HKLM\Software\Classes\2345Pic.webp\shell\open\command`
- `HKCR`（合并视图）
- `HKLM\Software\WOW6432Node\Classes\...`（键不存在）
- `HKCR\SystemFileAssociations\.webp`（不存在）
- `HKCU\Software\360zip\FileAssoc`（372 个键，全是压缩包扩展名，**无 .webp**）
- `HKLM\Software\2345Pic`（只有 Capabilities，无路径）

`AssocQueryString` 在 64 位与 32 位进程下**都返回 ComicViewer**，但 360zip 照样拉 2345。
全盘扫描 360zip 目录所有 exe/dll（ASCII + UTF16）找 `2345Pic`/`PicViewer` → **零命中**，排除硬编码。

**决定性实验**：把 `2345PicViewer.exe` 改名后双击 webp → 360zip **完全静默无反应**
（不报错、不回退、不启动其它程序）→ 它死认这个 exe。

### 2.3 config.ini 协议（png 路径用）

UTF-16LE + BOM：

```ini
[files]
01.png
02.png
[param]
total=8
current=0
handle=725312        ← 360zip 主窗口 HWND
```

2345 **不解析**这套协议，只扫同目录兄弟文件 → 目录里只有 1 张时显示 `第1/1张`。

### 2.4 关键发现：2345 认同目录多图翻页

把 83 张 webp 铺到同一目录，用 2345 打开第一张：

```
001.webp（1280×1807像素, 167KB）- 看图王 - 第1/83张 75%
```

**这是整个方案的基础。**

### 2.5 GDI+ 在 Win10 不支持 WebP

自研看图器用 `Image.FromStream` 解 webp 会抛「参数无效」。
.NET Framework 4.8 的 GDI+ 走不了 WIC 的 webp 编解码器。
**所以 webp 必须交给 2345 解码。**

## 三、最终方案

```
360zip 双击 webp
   → 解压 1 张到 %TEMP%\360zip$Temp\360$N\
   → CreateProcess 启动 2345PicViewer.exe
   → 被 IFEO 劫持到 ComicViewer.exe
       1. 剥离 IFEO 参数（args[0] 是原 exe），拿到真实图片路径
       2. WMI 读 360zip 命令行 → 定位源压缩包
       3. 把压缩包内全部图片铺到 360zip$Temp\360$N\（同名同大小跳过）
       4. 启动 2345PicViewer.real.exe（原 2345 副本）
   → 2345 看到同目录 83 张 → 显示「第1/83张」，左右翻页正常
```

**实测结果**：

```
2345PicViewer.real  001.webp（1280×1807像素, 167KB）- 看图王 - 第1/83张 66%
```

日志：
```
普通启动: args=C:\...\360zip$Temp\360$0\001.webp
Handoff: archive=E:\本子2\...zip
Handoff: 铺图完成 82 张
Handoff: 启动 D:\D\2345pic\2345PicViewer.real.exe
```

### 3.1 为什么 IFEO 能绕过 360zip 的顽固

IFEO 在**进程创建层**生效，位于 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<exe>`。
不管 360zip 从哪个途径拿到 `2345PicViewer.exe` 这个路径，只要它 `CreateProcess`，就会被重定向。

### 3.2 为什么不会死循环

原 exe 被复制为 `2345PicViewer.real.exe`，转发时启动副本（文件名不同，不触发 IFEO）。

### 3.3 为什么 360zip 升级不失效

- 未修改 360zip 任何文件。
- 未依赖 360zip 的配置/注册表。
- 只依赖两个稳定事实：临时目录在 `%TEMP%\360zip$Temp\360$N`、压缩包路径在其进程命令行里。
- 即使这两点变了，代码里还有「扫描本目录 + 扫描压缩包」的兜底。

## 四、目录结构

```
E:\360zip连续看图\
├─ ComicViewer.exe            成品（与 dist 同步）
├─ ComicViewer.ini            配置（original_viewer / last_archive）
├─ ComicViewer.backup.ini     关联备份
├─ src\
│  ├─ ComicViewer.cs          入口 / IFEO 剥离 / 模式判定 / Handoff / 压缩包定位
│  ├─ ComicForm.cs            自研看图窗口（png/jpg 用）/ ArchiveReader / External
│  └─ Installer.cs            关联接管 + IFEO 安装/卸载/状态
├─ dist\ComicViewer.exe       编译输出（编译后需复制到根目录）
├─ tools\                     逆向小工具（scanstrings/pedump/xref/disasm/widestrings/assoc_test）
├─ evidence\                  逆向证据
├─ 工作记录\                  按日期的工作记录
├─ README.md
└─ CHANGELOG.md
```

## 五、安装 / 卸载

```
ComicViewer.exe --install      接管关联 + 安装 IFEO（需管理员）
ComicViewer.exe --uninstall    还原关联 + 移除 IFEO
ComicViewer.exe --status       查看当前状态
```

也可以直接用项目根目录的三个 .cmd。

**IFEO 需要管理员权限**，普通权限运行会提示失败。

## 六、当前状态

### 已完成
- ✅ **2026-11-01 抗清理加固**：IFEO 被清掉事故后改为「IFEO + D:\D\2345pic\2345PicViewer.exe 本体替换」双保险，本体替换不依赖注册表（原文件备份 2345PicViewer.exe.orig-20261101）
- ✅ webp 包端到端打通：360zip 双击 → 2345 显示 `第1/83张`，可翻页
- ✅ png 包走自研看图器，左右翻页、缩放、全屏（此前已验证 1/8→8/8→1/8）
- ✅ IFEO 安装/卸载/状态已集成进 Installer
- ✅ 360zip 升级免疫（不改其任何文件）

### 已知限制
- **rar / 7z 未真机验证**：本机没有 7-Zip / WinRAR，代码路径已写（`External.ExtractAll`）
- 2345 升级会覆盖 `2345PicViewer.exe`，需重新 `--install` 刷新 `.real.exe`
- IFEO 修改 HKLM，必须有管理员权限

### 下一步
- 装 7-Zip 后验证 rar/7z
- 若 360zip 未来改临时目录命名，靠代码里的兜底扫描自动适应

## 七、编译

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
  /codepage:65001 /platform:anycpu /optimize+ ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /out:dist\ComicViewer.exe src\ComicViewer.cs src\ComicForm.cs src\Installer.cs
```

**编译后必须**：`Copy-Item dist\ComicViewer.exe ComicViewer.exe -Force`
（IFEO 指向根目录那份，不同步会跑旧版本 —— 本次已踩过这个坑）

