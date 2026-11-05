-- wpectl.lua — 通过 wpectl.exe 控制 Wallpaper Engine（纯 Lua 5.1+）
--
-- 依赖：wpectl.exe（tools/build.cmd 生成并部署到 %LOCALAPPDATA%\WpeCtl\）
--
-- 读状态（current / playlists）走 config.json，写命令走引擎 control CLI，
-- toggle 系列走 WM_COMMAND；细节见 ../README.md

local M = {}

-- WM_COMMAND 通道（toggle 语义，引擎内部热键用的那一组）
M.toggles = {
  pause      = 0x9c56,
  stop       = 0x9c57,
  mute       = 0x9c5f,
  screenshot = 0x9c5b,
  next       = 0x9c5d,
  browse     = 0x9c59,
  settings   = 0x9c53,
  editor     = 0x9c52,
}

local function exePath()
  local p = os.getenv("WPECTL_EXE")
  if p and #p > 0 then return p end
  local la = os.getenv("LOCALAPPDATA")
  if la then
    local guess = la .. "\\WpeCtl\\wpectl.exe"
    local f = io.open(guess, "rb")
    if f then f:close(); return guess end
  end
  return "wpectl.exe"
end

local function quote(s) return '"' .. s .. '"' end

local function run(args, withJson)
  local cmd = exePath() .. " " .. args .. (withJson and " --json" or "") .. " 2>&1"
  local pipe = io.popen(cmd)
  if not pipe then return nil end
  local out = pipe:read("*a")
  pipe:close()
  return out or ""
end

local function isTrue(out, key)
  return out ~= nil and out:find('"' .. key .. '":true', 1, true) ~= nil
end

--- 引擎是否在运行
function M.status()
  local out = run("status", true)
  return isTrue(out, "found"), out
end

--- 等待引擎启动（毫秒）
function M.waitReady(timeoutMs)
  local out = run(string.format("status --wait %d", timeoutMs or 15000), true)
  return isTrue(out, "found"), out
end

--- 当前壁纸路径（Monitor0）。返回 path, 原始输出
function M.current()
  local out = run("current", false)
  if not out then return nil end
  local path = out:match("Monitor0:%s*(.-)\r?\n") or out:match("Monitor0:%s*(.+)$")
  return path, out
end

--- 所有显示器的当前壁纸（原始文本，每行 "MonitorN: path"）
function M.currentAll()
  return run("current", false)
end

--- 把文件/项目目录设为壁纸。返回 ok, 原始JSON
function M.set(path)
  local out = run("set " .. quote(path), true)
  return isTrue(out, "ok"), out
end

--- 播放控制（绝对语义）
local function simple(name)
  return function()
    local out = run(name, true)
    return isTrue(out, "ok"), out
  end
end
M.play = simple("play")
M.pause = simple("pause")
M.stop = simple("stop")
M.mute = simple("mute")
M.unmute = simple("unmute")
M.next = simple("next")
M.hideIcons = simple("hideicons")
M.showIcons = simple("showicons")

--- 播放列表名（返回每行一个名字的原始文本）
function M.playlists()
  return run("playlists", false)
end

--- 播放指定播放列表
function M.playlist(name)
  local out = run("playlist " .. quote(name), true)
  return isTrue(out, "ok"), out
end

--- WM_COMMAND toggle（不是绝对语义；对 pause/stop/mute 用上面的同名函数更好）
function M.toggle(name)
  local out = run("toggle " .. name, true)
  return isTrue(out, "ok"), out
end

--- 直接投递命令 id
function M.raw(id)
  local out = run(string.format("raw 0x%x", id), true)
  return isTrue(out, "ok"), out
end

--- 列出所有 WPE* 窗口（诊断）
function M.windows()
  return run("list", false)
end

return M