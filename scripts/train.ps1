# Training run on the own simulator (CreatureIA.Sim, T2.0b): Python brain + N headless worlds in ONE
# process, all worlds batched into one STEP per tick. Default training path; Godot is only for watching.
# Re-running with the same -RunName resumes from its checkpoint.
#
#   .\scripts\train.ps1 -RunName sim-v3-a -Worlds 6 -Ticks 72000 -Seed 1 -Plants 2800 -Lakes 12
#   .\scripts\train.ps1 -Policy random -RunName sim-random -NoRespawn     # matched random baseline
#
# Etapa 2 (default): the simulator owns the population (births, rescue, cap); the brain never respawns.
#   -NoBirths   etapa-1 style (brain respawns every death)      -Control  scientific control (T2.8)
#   -Island     etapa-2 island instead of the etapa-1 map
param(
    [string]$RunName = "sim-$(Get-Date -Format 'yyyyMMdd-HHmm')",
    [long]$Ticks = 36000,          # brain ticks (10 Hz) per world: 36000 = 1 h simulated
    [int]$Seed = 1,
    [int]$Port = 5558,
    [string]$Device = "cuda",
    [ValidateSet("ppo", "random")] [string]$Policy = "ppo",
    [ValidateSet("gru", "mlp")] [string]$Brain = "gru",
    [int]$Worlds = 6,               # worlds (seeds Seed..Seed+Worlds-1) stepped in parallel, one batched STEP
    [int]$Population = 48,          # creatures per world
    [int]$Plants = 1400,
    [int]$Lakes = 6,
    [double]$Curiosity = 0.0,       # gru only: RND novelty weight; off by default (T1.1: it caused the v3 regression)
    [double]$LearningRate = 3e-4,
    [double]$MaxMinutes = 0,        # also stop cleanly after this many wall-clock minutes (0 = no limit)
    [switch]$NoRespawn,
    [switch]$NoBirths,
    [switch]$Control,
    [switch]$Island,
    [double]$Instinct = 0.5,        # etapa 2: inherited instinct strength (T2.6a); 0 = off (always off with -NoBirths)
    [int]$Rollout = 64,             # PPO rollout / GRU replay window (T2.6b: 256 for long memory)
    [switch]$Amp,                   # gru only: fp16 autocast in the update (T2.0e; validate on a seed-0 eval first)
    [switch]$NoBuild                # skip the Release build (parallel runs: build once beforehand)
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$runDir = Join-Path $root "benchmarks\runs\$RunName"
New-Item -ItemType Directory -Force $runDir | Out-Null
$env:DOTNET_ROOT = Join-Path $root ".tools\dotnet8"
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
$env:CUBLAS_WORKSPACE_CONFIG = ":4096:8"
$dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe"
$python = Join-Path $root ".venv\Scripts\python.exe"

$simProject = Join-Path $root "godot\CreatureIA.Sim\CreatureIA.Sim.csproj"
if (-not $NoBuild) {
    & $dotnet build $simProject -c Release -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "CreatureIA.Sim build failed." }
}
# Newest build of any configuration (a build made while another run locks bin\Release goes elsewhere).
$built = Get-ChildItem -LiteralPath (Join-Path $root "godot\CreatureIA.Sim\bin") -Recurse -Filter "CreatureIA.Sim.exe" |
    Where-Object { $_.FullName -notmatch '\\Debug\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
# Each run executes its own copy: rebuilding never collides with a running sim, and the run keeps
# the exact simulator it was trained on.
$simDir = Join-Path $runDir "sim"
New-Item -ItemType Directory -Force $simDir | Out-Null
Copy-Item -Path (Join-Path $built.DirectoryName "*") -Destination $simDir -Recurse -Force
$sim = Join-Path $simDir "CreatureIA.Sim.exe"

$inv = [Globalization.CultureInfo]::InvariantCulture
$serverArgs = @("-m", "creature_brain.bridge.server", "--port", $Port, "--policy", $Policy, "--seed", $Seed,
    "--horizon", $Ticks, "--exit-on-disconnect", "--sessions", 1, "--population", ($Worlds * $Population),
    "--episodes", (Join-Path $runDir "$Policy-episodes.jsonl"), "--config-id", "sim-w$Worlds")
# Births: population can grow past the founders; 1.5x slots are kept ready (more grow on demand).
if ($NoBirths -and -not $NoRespawn) { $serverArgs += "--respawn" }
if (-not $NoBirths) { $serverArgs += @("--slot-capacity", [int]($Worlds * $Population * 1.5)) }
if ($Policy -eq "ppo") {
    $serverArgs += @("--brain", $Brain, "--device", $Device, "--checkpoint", (Join-Path $runDir "ppo.pt"),
        "--metrics", (Join-Path $runDir "metrics.csv"), "--curiosity", $Curiosity.ToString($inv), "--lr", $LearningRate.ToString($inv))
}
if ($Amp -and $Policy -eq "ppo") { $serverArgs += "--amp" }
if ($Policy -eq "ppo") { $serverArgs += @("--rollout", $Rollout) }
if ($Instinct -gt 0 -and -not $NoBirths) { $serverArgs += @("--instinct", $Instinct.ToString($inv)) }
$env:PYTHONPATH = Join-Path $root "brain\src"
$server = Start-Process -FilePath $python -ArgumentList $serverArgs -NoNewWindow -PassThru `
    -RedirectStandardError (Join-Path $runDir "server.log") -RedirectStandardOutput (Join-Path $runDir "server.out")
$deadline = (Get-Date).AddSeconds(300)
while (-not (Select-String -Path (Join-Path $runDir "server.log") -Pattern "listening" -Quiet -ErrorAction SilentlyContinue)) {
    if ($server.HasExited) { throw "Brain server exited early; see $runDir\server.log" }
    if ((Get-Date) -gt $deadline) { throw "Brain server did not start within 300 s." }
    Start-Sleep -Milliseconds 300
}

$simArgs = @("--port", $Port, "--worlds", $Worlds, "--seed", $Seed, "--population", $Population,
    "--plants", $Plants, "--lakes", $Lakes, "--ticks", $Ticks, "--report-file", (Join-Path $runDir "sim-report.json"),
    # World snapshots (T2.0c): re-running the same RunName continues the same worlds, not fresh ones.
    "--worlds-dir", (Join-Path $runDir "worlds"))
if ($NoRespawn) { $simArgs += "--quit-when-extinct" }
if ($NoBirths) { $simArgs += "--no-births" } else { $simArgs += @("--genealogy", (Join-Path $runDir "genealogy.jsonl")) }
if ($Control) { $simArgs += "--control" }
if ($Island) { $simArgs += "--island" }
if ($MaxMinutes -gt 0) { $simArgs += @("--max-minutes", $MaxMinutes.ToString([Globalization.CultureInfo]::InvariantCulture)) }
Write-Host "training $Policy ($Brain) on CreatureIA.Sim: $Worlds world(s) x $Population for $Ticks ticks -> $runDir"
$simProcess = Start-Process -FilePath $sim -ArgumentList $simArgs -NoNewWindow -PassThru `
    -RedirectStandardError (Join-Path $runDir "sim.log") -RedirectStandardOutput (Join-Path $runDir "sim.out")
$simProcess.WaitForExit()
$server.WaitForExit(60000) | Out-Null
Write-Host "sim exit $($simProcess.ExitCode); outputs in $runDir"
exit $simProcess.ExitCode
