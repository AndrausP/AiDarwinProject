# Stage evaluation (T1.4): random x frozen policies on the RESERVED seed 0 (never trained), no respawn,
# N replicas of the seed-0 terrain (same land, different plants/spawns), on CreatureIA.Sim.
# Writes benchmarks/runs/<OutName>/summary.md (+ .json) with bootstrap CIs and the stage-1 gates.
#
#   .\scripts\eval-stage.ps1 -Policies "v2=t11c-v2:mlp","v3=v3-sim-final:gru" -OutName eval-etapa1-a
param(
    [Parameter(Mandatory)] [string[]]$Policies,   # label=runName:brain (checkpoint benchmarks/runs/<runName>/ppo.pt)
    [string]$OutName = "eval-etapa1-$(Get-Date -Format 'yyyyMMdd-HHmm')",
    [int]$Replicas = 6,
    [long]$Ticks = 36000,           # cap per life: 1 h simulated; lives still running are right-censored
    [int]$Seed = 0,
    [int]$Plants = 1400,            # the default world, not the training curriculum
    [int]$Lakes = 6,
    [int]$BasePort = 5600,
    [switch]$Deterministic,
    [switch]$NoBuild                # use the newest existing Sim build (e.g. while a training run locks bin\Release)
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$outDir = Join-Path $root "benchmarks\runs\$OutName"
New-Item -ItemType Directory -Force $outDir | Out-Null
$env:DOTNET_ROOT = Join-Path $root ".tools\dotnet8"
$env:CUBLAS_WORKSPACE_CONFIG = ":4096:8"
$env:PYTHONPATH = Join-Path $root "brain\src"
$python = Join-Path $root ".venv\Scripts\python.exe"

if (-not $NoBuild) {
    & (Join-Path $env:DOTNET_ROOT "dotnet.exe") build (Join-Path $root "godot\CreatureIA.Sim\CreatureIA.Sim.csproj") -c Release -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "CreatureIA.Sim build failed." }
}
$built = Get-ChildItem -LiteralPath (Join-Path $root "godot\CreatureIA.Sim\bin") -Recurse -Filter "CreatureIA.Sim.exe" |
    Where-Object { $_.FullName -notmatch '\\Debug\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$simDir = Join-Path $outDir "sim"
New-Item -ItemType Directory -Force $simDir | Out-Null
Copy-Item -Path (Join-Path $built.DirectoryName "*") -Destination $simDir -Recurse -Force
$sim = Join-Path $simDir "CreatureIA.Sim.exe"

$entries = @(@{ Label = "random"; Run = $null; Brain = "mlp" })
foreach ($p in $Policies) {
    $label, $rest = $p -split "=", 2
    $run, $brain = $rest -split ":", 2
    $checkpoint = Join-Path $root "benchmarks\runs\$run\ppo.pt"
    if (-not (Test-Path $checkpoint)) { throw "missing checkpoint $checkpoint" }
    $entries += @{ Label = $label; Run = $run; Brain = $brain; Checkpoint = $checkpoint }
}

$processes = @()
$port = $BasePort
foreach ($e in $entries) {
    $label = $e.Label
    $episodes = Join-Path $outDir "$label-episodes.jsonl"
    Remove-Item $episodes -ErrorAction SilentlyContinue
    $policy = if ($e.Run) { "ppo" } else { "random" }
    $serverArgs = @("-m", "creature_brain.bridge.server", "--port", $port, "--policy", $policy, "--seed", $Seed,
        "--horizon", $Ticks, "--exit-on-disconnect", "--sessions", 1, "--population", ($Replicas * 48),
        "--episodes", $episodes, "--config-id", "eval-seed$Seed-r$Replicas")
    if ($e.Run) {
        $serverArgs += @("--brain", $e.Brain, "--device", "cuda", "--checkpoint", $e.Checkpoint, "--freeze")
        if ($Deterministic) { $serverArgs += "--deterministic" }
    }
    $log = Join-Path $outDir "$label-server.log"
    $server = Start-Process -FilePath $python -ArgumentList $serverArgs -NoNewWindow -PassThru -RedirectStandardError $log -RedirectStandardOutput "$log.out"
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Select-String -Path $log -Pattern "listening" -Quiet -ErrorAction SilentlyContinue)) {
        if ($server.HasExited) { throw "server for $label exited early; see $log" }
        if ((Get-Date) -gt $deadline) { throw "server for $label did not start" }
        Start-Sleep -Milliseconds 300
    }
    $simArgs = @("--port", $port, "--worlds", $Replicas, "--replicas", "--seed", $Seed, "--plants", $Plants, "--lakes", $Lakes,
        "--ticks", $Ticks, "--quit-when-extinct", "--report-file", (Join-Path $outDir "$label-sim.json"))
    $processes += Start-Process -FilePath $sim -ArgumentList $simArgs -NoNewWindow -PassThru `
        -RedirectStandardError (Join-Path $outDir "$label-sim.log") -RedirectStandardOutput (Join-Path $outDir "$label-sim.out")
    $processes += $server
    Write-Host "evaluating $label ($policy) on seed $Seed x $Replicas replicas (port $port)"
    $port++
}
$processes | ForEach-Object { $_.WaitForExit() }

$episodeArgs = $entries | ForEach-Object { "$($_.Label)=$(Join-Path $outDir "$($_.Label)-episodes.jsonl")" }
$exploitArgs = $entries | ForEach-Object { "$($_.Label)=$(Join-Path $outDir "$($_.Label)-sim.json")" }
& $python -m creature_brain.benchmarks.stage_eval @episodeArgs --exploits @exploitArgs --output $outDir
