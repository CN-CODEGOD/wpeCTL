-- wpectl_ffi.lua — LuaJIT 版本，直接调 user32，不需要 wpectl.exe
--
-- 适用于 LuaJIT 2.0/2.1。原理与 C# 版一致：
--   FindWindowW("WPEEventWindow") + PostMessageW(WM_COMMAND, id)
--
-- 用法：
--   local wpe = require("wpectl_ffi")
--   if wpe.find() then wpe.pause() end
--   wpe.send(wpe.commands.settings)

local ffi = require("ffi")

ffi.cdef[[
  void*  FindWindowW(const wchar_t* lpClassName, const wchar_t* lpWindowName);
  int    PostMessageW(void* hWnd, unsigned int Msg, void* wParam, void* lParam);
  int    IsWindow(void* hWnd);
]]

local user32 = ffi.load("user32")

local M = {}

M.WM_COMMAND = 0x0111
M.commands = {
  pause      = 0x9c56,
  stop       = 0x9c57,
  mute       = 0x9c5f,
  screenshot = 0x9c5b,
  next       = 0x9c5d,
  browse     = 0x9c59,
  settings   = 0x9c53,
  editor     = 0x9c52,
}

-- ASCII 类名转 UTF-16 缓冲
local function wstr(s)
  local buf = ffi.new("wchar_t[?]", #s + 1)
  for i = 1, #s do buf[i - 1] = s:byte(i) end
  return buf
end

local CLASS = wstr("WPEEventWindow")

--- 返回引擎主窗口句柄（cdata），未运行时为 nil
function M.find()
  local h = user32.FindWindowW(CLASS, nil)
  if h == nil then return nil end
  if user32.IsWindow(h) == 0 then return nil end
  return h
end

--- 向引擎投递一条命令 id
function M.send(id)
  local h = M.find()
  if not h then return false, "engine-not-found" end
  local ok = user32.PostMessageW(h, M.WM_COMMAND, ffi.cast("void*", id), nil)
  return ok ~= 0
end

--- 与 wpectl.lua 保持同一 API
M.raw = function(id) return M.send(id) end

for name, id in pairs(M.commands) do
  M[name] = function() return M.send(id) end
end

return M