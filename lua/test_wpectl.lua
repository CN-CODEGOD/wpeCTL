-- test_wpectl.lua — 验证 lua/wpectl.lua（纯 Lua 层，走 wpectl.exe）
-- 运行： lua.exe test_wpectl.lua [模块目录]
--
-- set 测试用“当前壁纸”本身，净效果为零，不会改变桌面。

local function thisDir()
  local src = (debug.getinfo(1, "S").source or ""):gsub("^@", "")
  local dir = src:match("^(.*)[/\\][^/\\]+$")
  return dir or "."
end

local moduleDir = (arg and arg[1]) or thisDir()
package.path = moduleDir .. "/?.lua;" .. package.path

local wpe = require("wpectl")

local fails = 0
local function check(name, cond, detail)
  if cond then
    print(string.format("PASS  %-40s", name))
  else
    fails = fails + 1
    print(string.format("FAIL  %-40s %s", name, tostring(detail or "")))
  end
end

local ok, raw = wpe.status()
check("status() 找到引擎", ok, raw)
check("waitReady(3000)", (wpe.waitReady(3000)))

local cur, curRaw = wpe.current()
check("current() 返回路径", type(cur) == "string" and #cur > 0, curRaw)
check("current() 是绝对路径", cur ~= nil and (cur:match("^%a:/") ~= nil or cur:match("^%a:\\") ~= nil), cur)

if cur then
  local okSet, setRaw = wpe.set(cur)
  check("set(当前壁纸) 幂等成功", okSet, setRaw)
  local after = wpe.current()
  check("set 后状态一致", after == cur, tostring(after))
end

check("play()", (wpe.play()))
check("pause()", (wpe.pause()))
check("play() 恢复", (wpe.play()))
check("mute()", (wpe.mute()))
check("unmute()", (wpe.unmute()))

if cur then
  local okIns, insRaw = wpe.insert(cur, 1500)
  check("insert(当前壁纸, 1500ms) 成功", okIns, insRaw)
  local afterIns = wpe.current()
  check("insert 结束后已还原", afterIns == cur, tostring(afterIns))
end

local lists = wpe.playlists()
check("playlists() 可读", type(lists) == "string", lists)

local okT, tRaw = wpe.toggle("pause")
local okT2 = wpe.toggle("pause")
check("toggle(pause) 两次（净零）", okT and okT2, tRaw)

local okRaw1 = wpe.raw(0x9c5f)
local okRaw2 = wpe.raw(0x9c5f)
check("raw(0x9c5f) 两次（净零）", okRaw1 and okRaw2)

local win = wpe.windows()
check("windows() 含 WPEEventWindow", type(win) == "string" and win:find("WPEEventWindow", 1, true) ~= nil, win)

print(string.format("\n%s: %d 项失败", fails == 0 and "ALL PASS" or "FAILED", fails))
os.exit(fails == 0 and 0 or 1)