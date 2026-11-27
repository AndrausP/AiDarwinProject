# Training run on the own simulator (CreatureIA.Sim, T2.0b): Python brain + N headless worlds in ONE
# process, all worlds batched into one STEP per tick. Default training path; Godot is only for watching.
# Re-running with the same -RunName resumes from its checkpoint.
#
#   .\scripts\train.ps1 -RunName sim-v3-a -Worlds 6 -Ticks 72000 -Seed 1 -Plants 2800 -Lakes 12
#   .\scripts\train.ps1 -Policy random -RunName sim-random -NoRespawn     # matched random baseline
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
    [double]$Curiosity = 0.02,
    [double]$LearningRate = 3e-4,
    [switch]$NoRespawn
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
& $dotnet build $simProject -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CreatureIA.Sim build failed." }
$sim = Get-ChildItem -LiteralPath (Join-Path $root "godot\CreatureIA.Sim\bin\Release") -Recurse -Filter "CreatureIA.Sim.exe" |
    Select-Object -First 1 -ExpandProperty FullName

$inv = [Globalization.CultureInfo]::InvariantCulture
$serverArgs = @("-m", "creature_brain.bridge.server", "--port", $Port, "--policy", $Policy, "--seed", $Seed,
    "--horizon", $Ticks, "--exit-on-disconnect", "--sessions", 1, "--population", ($Worlds * $Population),
    "--episodes", (Join-Path $runDir "$Policy-episodes.jsonl"), "--config-id", "sim-w$Worlds")
if (-not $NoRespawn) { $serverArgs += "--respawn" }
if ($Policy -eq "ppo") {
    $serverArgs += @("--brain", $Brain, "--device", $Device, "--checkpoint", (Join-Path $runDir "ppo.pt"),
        "--metrics", (Join-Path $runDir "metrics.csv"), "--curiosity", $Curiosity.ToString($inv), "--lr", $LearningRate.ToString($inv))
}
$env:PYTHONPATH = Join-Path $root "brain\src"
$server = Start-Process -FilePath $python -ArgumentList $serverArgs -NoNewWindow -PassThru `
    -RedirectStandardError (Join-Path $runDir "server.log") -RedirectStandardOutput (Join-Path $runDir "server.out")
$deadline = (Get-Date).AddSeconds(90)
while (-not (Select-String -Path (Join-Path $runDir "server.log") -Pattern "listening" -Quiet -ErrorAction SilentlyContinue)) {
    if ($server.HasExited) { throw "Brain server exited early; see $runDir\server.log" }
    if ((Get-Date) -gt $deadline) { throw "Brain server did not start within 90 s." }
    Start-Sleep -Milliseconds 300
}

$simArgs = @("--port", $Port, "--worlds", $Worlds, "--seed", $Seed, "--population", $Population,
    "--plants", $Plants, "--lakes", $Lakes, "--ticks", $Ticks)
if ($NoRespawn) { $simArgs += "--quit-when-extinct" }
Write-Host "training $Policy ($Brain) on CreatureIA.Sim: $Worlds world(s) x $Population for $Ticks ticks -> $runDir"
$simProcess = Start-Process -FilePath $sim -ArgumentList $simArgs -NoNewWindow -PassThru `
    -RedirectStandardError (Join-Path $runDir "sim.log") -RedirectStandardOutput (Join-Path $runDir "sim.out")
$simProcess.WaitForExit()
$server.WaitForExit(60000) | Out-Null
Write-Host "sim exit $($simProcess.ExitCode); outputs in $runDir"
exit $simProcess.ExitCode
