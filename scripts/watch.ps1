# Watch the creatures in a window with the trained brain, which keeps learning online while you
# watch (its own copy of the weights: the training run is never modified).
#
#   .\scripts\watch.ps1                       # trained brain (PPO), keeps adapting
#   .\scripts\watch.ps1 -Policy random        # untrained baseline, for comparison
#   .\scripts\watch.ps1 -Fresh                # restart the watch copy from the trained snapshot
#   .\scripts\watch.ps1 -Showcase             # white test room: every example side by side
param(
    [ValidateSet("ppo", "random")] [string]$Policy = "ppo",
    [ValidateSet("gru", "mlp")] [string]$Brain = "gru",   # gru = brain v3 (memory + curiosity); mlp = v2 runs such as v2-ppo-a
    # Protocol v2 (voxel, Task 002): v1 checkpoints such as ppo-f-final-snapshot.pt no longer load.
    [string]$Checkpoint = "",   # default: the latest run of the chosen brain (v3-gru-a / v2-ppo-a)
    [int]$Seed = 1337,
    [switch]$Fresh,
    [switch]$Showcase             # white test room with every tree/shrub/block example side by side
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$watchDir = Join-Path $root "benchmarks\runs\watch-live-v2"
New-Item -ItemType Directory -Force $watchDir | Out-Null
if (-not $Checkpoint) { $Checkpoint = if ($Brain -eq "gru") { "benchmarks\runs\v3-gru-a\ppo.pt" } else { "benchmarks\runs\v2-ppo-a\ppo.pt" } }
$watchDir = Join-Path $watchDir $Brain
New-Item -ItemType Directory -Force $watchDir | Out-Null
$watchBrain = Join-Path $watchDir "ppo.pt"
if ($Policy -eq "ppo" -and ($Fresh -or -not (Test-Path $watchBrain)) -and (Test-Path (Join-Path $root $Checkpoint))) {
    Copy-Item (Join-Path $root $Checkpoint) $watchBrain -Force
}

$python = Join-Path $root ".venv\Scripts\python.exe"
$godot = Get-ChildItem -LiteralPath (Join-Path $root ".tools") -Filter 'Godot*_mono_win64.exe' -File -Recurse |
    Where-Object Name -notmatch 'console' | Select-Object -First 1 -ExpandProperty FullName
$env:DOTNET_ROOT = Join-Path $root ".tools\dotnet8"
$env:DOTNET_MULTILEVEL_LOOKUP = "0"
$env:CUBLAS_WORKSPACE_CONFIG = ":4096:8"
foreach ($name in "CREATURE_IA_MAX_BRAIN_TICKS", "CREATURE_IA_QUIT_WHEN_EXTINCT", "CREATURE_IA_PLANTS",
                  "CREATURE_IA_LAKES", "CREATURE_IA_TIME_SCALE", "CREATURE_IA_PROFILE") {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}
$env:CREATURE_IA_WORLD_SEED = "$Seed"
if ($Showcase) { $env:CREATURE_IA_SHOWCASE = "1" } else { Remove-Item Env:CREATURE_IA_SHOWCASE -ErrorAction SilentlyContinue }
$env:CREATURE_IA_BRAIN_PORT = "5555"

$serverArgs = @("-m", "creature_brain.bridge.server", "--port", "5555", "--policy", $Policy,
    "--respawn", "--exit-on-disconnect", "--episodes", (Join-Path $watchDir "$Policy-episodes.jsonl"))
if ($Policy -eq "ppo") {
    $serverArgs += @("--brain", $Brain, "--device", "cuda", "--checkpoint", $watchBrain, "--metrics", (Join-Path $watchDir "metrics.csv"))
}
$server = Start-Process -FilePath $python -ArgumentList $serverArgs -NoNewWindow -PassThru `
    -RedirectStandardError (Join-Path $watchDir "server.log") -RedirectStandardOutput (Join-Path $watchDir "server.out")
$deadline = (Get-Date).AddSeconds(60)
while (-not (Select-String -Path (Join-Path $watchDir "server.log") -Pattern "listening" -Quiet -ErrorAction SilentlyContinue)) {
    if ($server.HasExited) { throw "Brain server exited early; see $watchDir\server.log" }
    if ((Get-Date) -gt $deadline) { throw "Brain server did not start within 60 s." }
    Start-Sleep -Milliseconds 300
}
Write-Host "watching ($Policy brain) - close the window to stop"
& $godot --path (Join-Path $root "godot") *> (Join-Path $watchDir "godot.log")
$server.WaitForExit(15000) | Out-Null
