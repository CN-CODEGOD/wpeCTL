# wpectl — 用代码控制 Wallpaper Engine

不注入、不修改 Wallpaper Engine 安装目录下的任何文件。两条互补通道：

| 通道 | 用途 | 机制 |
|---|---|---|
| **读状态** | 当前壁纸、播放列表 | 解析安装目录的 `config.json`：`<user>.general.wallpaperconfig.selectedwallpapers.MonitorN.file` |
| **发命令** | 播放 / 换壁纸 / 播放列表 | 引擎自带 `wallpaper64.exe -control <cmd>` |
| **发命令（轻量）** | 暂停 / 静音 / 截图 / 开窗口 | `PostMessageW(WM_COMMAND, 0x9c5x)` 投递到 `WPEEventWindow` |

读状态为什么不直接用 CLI：`-control` 客户端会把被重定向的 stdout detach 掉再写控制台，管道里抓不到输出；而引擎应用壁纸后会立刻写 `config.json`，读文件既准又快。

## 命令一览

```bat
wpectl status [--json]                          引擎窗口与进程信息
wpectl current [--json]                         所有显示器的当前壁纸
wpectl current --monitor 0 [--json]             指定显示器
wpectl set <路径> [--monitor N] [--json]         把文件/项目设为壁纸（会轮询确认）
wpectl play | pause | stop | mute | unmute | next
wpectl playlists                                列出播放列表名
wpectl playlist <名称> [--monitor N]            播放指定播放列表
wpectl insert <路径> [--for ms|--keep] [--monitor N]  临时插入，到点自动回到原壁纸
wpectl toggle <pause|stop|mute|screenshot|browse|settings|editor>
wpectl raw <id>                                 直接投递任意 WM_COMMAND
```

退出码：`0` 成功 · `1` 参数错误 · `2` 引擎未运行或状态不可读 · `3` 命令失败。

`set` 的目标写法：

- 媒体文件（`.mp4` `.jpg` `.png` …）→ 直接播放
- 项目目录 → 自动解析为其中的 `project.json`（场景项目必须这样给，引擎随后会回报 `<dir>/scene.pkg`）
- `project.json` 本身 → 直接使用

### insertCurrent —— 临时插入再还原

```bat
wpectl insert "D:/pics/cat.jpg" --for 8000
wpectl insert "D:/videos/intro.mp4" --keep
```

先记下当前壁纸，播放目标 `--for` 毫秒（默认 10000），到点后自动还原；`--keep` 表示一直显示到 Ctrl+C。
不带 `--monitor` 时对所有显示器插入并逐个还原。

还原用的是当前壁纸原路径：场景壁纸在 `config.json` 里记录的是 `scene.pkg`，
模块会自动改投同目录的 `project.json`（`openWallpaper` 不认 `.pkg` 路径）。
还原失败会重试 3 次，仍失败时 JSON 里该显示器的 `restored` 为 `false` 并带 `error`。

注意：Ctrl+C 会走还原逻辑（处理器里 `e.Cancel = true`），但 `taskkill` 这类强杀不经过处理器，
壁纸会停在插入的那一个，需要自己再 `wpectl set` 回去。

## 构建

只依赖 Windows 自带的 .NET Framework 4.0 `csc.exe`（无需 .NET SDK / NuGet）：

```bat
tools\build.cmd
```

产物写到 `bin\wpectl.exe`，并把可执行副本部署到 `%LOCALAPPDATA%\WpeCtl\wpectl.exe`。
如果你的目录策略禁止从工作区执行二进制（常见于受管环境），请从 `%LOCALAPPDATA%` 那份运行；Lua 层默认也在那里找 `wpectl.exe`，可用环境变量 `WPECTL_EXE` 覆盖（路径不要含空格，原因见下）。

## C# 里直接用

```csharp
using WpeCtl;

string err;
string cur = WallpaperState.Current(0, out err);
bool ok = Control.OpenWallpaper("D:/pics/cat.jpg", 0, out string reported, out err);
Control.Simple("play", out err);
Control.Playlists(out string configPath, out err);
WpeEngine.Post(WpeEngine.Find().Handle, Commands.OpenSettings);
```

## Lua

`lua/wpectl.lua` — 纯 Lua 5.1+（含 LuaJIT），通过 `wpectl.exe`：

```lua
package.path = package.path .. ";<仓库路径>/lua/?.lua"
local wpe = require("wpectl")

print(wpe.current())          -- 当前壁纸路径
wpe.pause(); wpe.play()       -- 绝对语义
wpe.set("D:/pics/cat.jpg")    -- 换壁纸
print(wpe.playlists())        -- 播放列表
wpe.toggle("screenshot")      -- WM_COMMAND 通道
wpe.insert("D:/pics/cat.jpg", 5000)   -- 临时插入，5 秒后自动回到原壁纸
```

`lua/wpectl_ffi.lua` — LuaJIT FFI，直接调 `user32`，不启动任何进程；只覆盖 WM_COMMAND 那组命令（`pause stop mute screenshot next browse settings editor` 的 toggle 语义），不含 `current / set / playlist`。

```lua
local wpe = require("wpectl_ffi")
if wpe.find() then wpe.pause() end
```

测试：

```bat
lua.exe       lua\test_wpectl.lua
luajit.exe    lua\test_wpectl_ffi.lua
```

测试用“当前壁纸”本身做 `set`，净效果为零，不会改变桌面。

注意：`io.popen` 的命令行不能以引号开头（cmd 会吃掉后续引号），所以 Lua 层不给默认 exe 路径加引号——`WPECTL_EXE` 指向无空格路径。

## 命令来源（逆向结论）

`-control` 命令由 `wallpaper64.exe` 的 `FUN_140008270` 分派：
`pause play stop mute unmute openWallpaper openPlaylist openProfile closeWallpaper nextWallpaper applyProperties getWallpaper hideIcons showIcons revealWallpaper`。
`openWallpaper` 支持 `-file -location -monitor -playInWindow -width -height -activate -borderless -preset -properties`；未知播放列表返回退出码 5。

`WM_COMMAND` id 来自 `FUN_140021e50` 的 `0x111` 分支（均为 toggle）：

| id | 语义 | 依据 |
|---|---|---|
| `0x9c56` | 暂停 | 状态位 `0x1` |
| `0x9c57` | 停止 | 状态位 `0x8` |
| `0x9c5f` | 静音 | 状态位 `0x40` |
| `0x9c5d` | 下一张 | 遍历各显示器壁纸项 |
| `0x9c5b` | 截图 | 拉起 `wallpaperui.exe -op steamscreenshot` |
| `0x9c59` | 浏览窗口 | `-window browsewallpapers` |
| `0x9c53` | 设置窗口 | `-window settings` |
| `0x9c52` | 编辑器 | `-window editor` |

以上为 2.8.42 版本逆向结果，官方未文档化，版本更新可能变化。

## 已知限制

- 暂停/静音等运行时状态不落盘，只能发命令、无法回读。
- `playlist` 依赖 WE 中已存在的播放列表（定义在 `config.json` 的 `<user>.general.playlists[]`）。
- 每次 `-control` 调用都会拉起一个 `wallpaper64.exe`（数百毫秒）；高频循环请用 `toggle` / `raw`。
- 场景/网页壁纸加载需数秒，`set` 最多轮询 `config.json` 10 秒确认。
