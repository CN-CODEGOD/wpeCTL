-- test_wpectl_ffi.lua — 验证 lua/wpectl_ffi.lua（LuaJIT FFI，直接调 user32，不启进程）
-- 运行： luajit.exe test_wpectl_ffi.lua [模块目录]

local function thisDir()
  local src = (debug.getinfo(1, "S").source or ""):gsub("^@", "")
  local dir = src:match("^(.*)[/\\][^/\\]+$")
  return dir or "."
end

local moduleDir = (arg and arg[1]) or thisDir()
package.path = moduleDir .. "/?.lua;" .. package.path

local okreq, wpe = pcall(require, "wpectl_ffi")
if not okreq then
  print("FAIL  需要 LuaJIT (ffi)： " .. tostring(wpe))
  os.exit(2)
end
if type(wpe) ~= "table" then
  print("FAIL  模块未返回 table（文件可能被清空？）")
  os.exit(2)
end

local fails = 0
local function check(name, cond, detail)
  if cond then
    print(string.format("PASS  %-38s", name))
  else
    fails = fails + 1
    print(string.format("FAIL  %-38s %s", name, tostring(detail or "")))
  end
end

local h = wpe.find()
check("find() 拿到 WPEEventWindow", h ~= nil, "engine-not-found")
if not h then
  print("\nFAILED: 引擎未运行，无法继续")
  os.exit(1)
end

check("commands.pause == 0x9c56", wpe.commands.pause == 0x9c56)
check("commands.settings == 0x9c53", wpe.commands.settings == 0x9c53)

local m1, e1 = wpe.send(wpe.commands.mute)
local m2, e2 = wpe.send(wpe.commands.mute)
check("send(mute) 两次（净零）", m1 and m2, tostring(e1 or "") .. tostring(e2 or ""))

local p1 = wpe.pause()
local p2 = wpe.pause()
check("pause() 便捷方法", p1 and p2)

local r1 = wpe.raw(0x9c5f)
local r2 = wpe.raw(0x9c5f)
check("raw(0x9c5f) 两次（净零）", r1 and r2)

print(string.format("\n%s: %d 项失败", fails == 0 and "ALL PASS" or "FAILED", fails))
os.exit(fails == 0 and 0 or 1)