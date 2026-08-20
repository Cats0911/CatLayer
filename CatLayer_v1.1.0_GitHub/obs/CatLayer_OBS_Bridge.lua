obs = obslua

local command_path = nil
local status_path = nil
local last_command = ""
local last_heartbeat = 0

local function base_dir()
    local p = os.getenv("LOCALAPPDATA")
    if p == nil or p == "" then
        p = "."
    end
    return p .. "\\CatLayer"
end

local function write_status(text)
    if status_path == nil then return end
    local f = io.open(status_path, "w")
    if f ~= nil then
        f:write(text)
        f:close()
    end
end

local function read_command()
    if command_path == nil then return nil end
    local f = io.open(command_path, "r")
    if f == nil then return nil end
    local line = f:read("*l")
    f:close()
    return line
end

local function tick()
    local now = os.time()
    if now ~= last_heartbeat then
        last_heartbeat = now
        write_status("READY|" .. tostring(now))
    end

    local cmd = read_command()
    if cmd == nil or cmd == "" or cmd == last_command then return end
    last_command = cmd

    local action, token = string.match(cmd, "^([^|]+)|?(.*)$")
    if action == "OPEN_PROGRAM" then
        local ok, err = pcall(function()
            -- Official OBS frontend API. monitor=-1 means Windowed Projector.
            -- StudioProgram is the actual Program/output mix, not the Preview mix.
            obs.obs_frontend_open_projector("StudioProgram", -1, "", "")
        end)
        if ok then
            write_status("OPENED|" .. tostring(token) .. "|" .. tostring(os.time()))
        else
            write_status("ERROR|" .. tostring(token) .. "|" .. tostring(err))
        end
    elseif action == "PING" then
        write_status("READY|" .. tostring(now))
    end
end

function script_description()
    return [[
CatLayer OBS Bridge

CatLayer가 요청하면 OBS의 Windowed Projector (Program)를 엽니다.
Virtual Camera / DirectShow를 사용하지 않습니다. CatLayer가 projector를 1920x1080으로 유지하고 DWM으로 축소 표시합니다.

이 스크립트는 OBS 내부의 obs_frontend_open_projector("StudioProgram", -1, ...) API만 사용합니다.
]]
end

function script_load(settings)
    local dir = base_dir()
    command_path = dir .. "\\obs_bridge_command.txt"
    status_path = dir .. "\\obs_bridge_status.txt"
    write_status("READY|" .. tostring(os.time()))
    obs.timer_add(tick, 200)
end

function script_unload()
    obs.timer_remove(tick)
    write_status("STOPPED|" .. tostring(os.time()))
end
