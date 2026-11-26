# Headless PPO training run: Python brain (online PPO, respawn) + Godot world, unthrottled.
# Re-running with the same -RunName resumes from its checkpoint.
#
#   .\scripts\train-ppo.ps1 -RunName ppo-a -Ticks 72000      # 72k brain ticks = 2 h simulated
#   .\scripts\train-ppo.ps1 -Policy random -RunName base-a    # matched random baseline
param(
    [string]$RunName = "ppo-$(Get-Date -Format 'yyyyMMdd-HHmm')",
    [int]$Ticks = 36000,          # brain ticks (10 Hz): 36000 = 1 h simulated
    [int]$Seed = 0,
    [int]$Port = 5557,            # separate from the spectator default (5555)
    [string]$Device = "cuda",
    [ValidateSet("ppo", "random")] [string]$Policy = "ppo",
    [ValidateSet("gru", "mlp")] [string]$Brain = "gru",   # gru = brain v3 (memory + curiosity); mlp = v2 runs such as v2-ppo-a
    [int]$Worlds = 1,             # parallel Godot worlds (seeds Seed..Seed+Worlds-1) feeding one brain
    [int]$Plants = 0,             # 0 = world default; >0 overrides (food-abundance curriculum)
    [int]$Lakes = 0,              # 0 = world default; >0 overrides (water curriculum)
    [switch]$NoRespawn            # one life per creature; world quits when all died (paired G10 benchmark)
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$runDir = Join-Path $root "benchmarks\runs\$RunName"
New-Item -ItemType Directory -Force $runDir | Out-Null

$python = Join-Path $root ".venv\Scripts\python.exe"
$godot = Get-ChildItem -LiteralPath (Join-Path $root ".tools") -Filter 'Godot*_console.exe' -File -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $godot) { throw "Godot .NET console executable not found under .tools/." }
$env:DOTNET_ROOT = Join-Path $root ".tools\dotnet8"
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
# PPO seeds torch with deterministic algorithms; cuBLAS needs this workspace setting to honour it.
$env:CUBLAS_WORKSPACE_CONFIG = ":4096:8"

$serverArgs = @("-m", "creature_brain.bridge.server", "--port", $Port, "--policy", $Policy,
    "--seed", $Seed, "--horizon", $Ticks, "--exit-on-disconnect", "--sessions", $Worlds,
    "--episodes", (Join-Path $runDir "$Policy-episodes.jsonl"))
if (-not $NoRespawn) { $serverArgs += "--respawn" }
if ($Policy -eq "ppo") {
    $serverArgs += @("--brain", $Brain, "--device", $Device, "--checkpoint", (Join-Path $runDir "ppo.pt"),
        "--metrics", (Join-Path $runDir "metrics.csv"))
}
$server = Start-Process -FilePath $python -ArgumentList $serverArgs -NoNewWindow -PassThru `
    -RedirectStandardError (Join-Path $runDir "server.log") -RedirectStandardOutput (Join-Path $runDir "server.out")

$deadline = (Get-Date).AddSeconds(60)
while (-not (Select-String -Path (Join-Path $runDir "server.log") -Pattern "listening" -Quiet -ErrorAction SilentlyContinue)) {
    if ($server.HasExited) { throw "Brain server exited early; see $runDir\server.log" }
    if ((Get-Date) -gt $deadline) { throw "Brain server did not start within 60 s." }
    Start-Sleep -Milliseconds 300
}

$env:CREATURE_IA_BRAIN_PORT = "$Port"
$env:CREATURE_IA_MAX_BRAIN_TICKS = "$Ticks"
if ($NoRespawn) { $env:CREATURE_IA_QUIT_WHEN_EXTINCT = "1" } else { Remove-Item Env:CREATURE_IA_QUIT_WHEN_EXTINCT -ErrorAction SilentlyContinue }
if ($Lakes -gt 0) { $env:CREATURE_IA_LAKES = "$Lakes" } else { Remove-Item Env:CREATURE_IA_LAKES -ErrorAction SilentlyContinue }
if ($Plants -gt 0) { $env:CREATURE_IA_PLANTS = "$Plants" } else { Remove-Item Env:CREATURE_IA_PLANTS -ErrorAction SilentlyContinue }
Write-Host "training $Policy for $Ticks ticks x $Worlds world(s) (seeds $Seed..$($Seed + $Worlds - 1)) -> $runDir"
$worldProcesses = @()
for ($i = 0; $i -lt $Worlds; $i++) {
    $env:CREATURE_IA_WORLD_SEED = "$($Seed + $i)"
    $log = Join-Path $runDir $(if ($Worlds -eq 1) { "godot.log" } else { "godot-$i.log" })
    $worldProcesses += Start-Process -FilePath $godot -NoNewWindow -PassThru `
        -ArgumentList @("--headless", "--fixed-fps", "60", "--path", (Join-Path $root "godot")) `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
}
$worldProcesses | ForEach-Object { $_.WaitForExit() }
$godotExit = ($worldProcesses | Measure-Object -Property ExitCode -Maximum).Maximum
$server.WaitForExit(30000) | Out-Null
Write-Host "godot exit $godotExit; outputs in $runDir"
exit $godotExit
