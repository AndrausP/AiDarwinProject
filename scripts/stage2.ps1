# Etapa 2 (T2.8 + T2.9): a run WITH selection and its scientific control (random parents, same
# genetics and mutation) side by side on the same seeds, then the stage-2 report.
# Re-running with the same -Name resumes both runs (brain, instinct and worlds).
#
#   .\scripts\stage2.ps1 -Name s2-a -Ticks 288000 -MaxMinutes 600       # ~8 simulated hours (4 years of 7200 s... see Ticks)
#   .\scripts\stage2.ps1 -Name s2-a -EvalOnly                            # just the report
param(
    [string]$Name = "s2-$(Get-Date -Format 'yyyyMMdd-HHmm')",
    [long]$Ticks = 288000,          # brain ticks per world (10 Hz): 288000 = 8 simulated hours = 4 years of 12 days
    [int]$Worlds = 4,
    [int]$Population = 48,
    [int]$Seed = 1,
    [int]$Plants = 2800,
    [int]$Lakes = 12,
    [ValidateSet("gru", "mlp")] [string]$Brain = "gru",
    [int]$Rollout = 64,
    [double]$MaxMinutes = 0,
    [switch]$Island,
    [switch]$EvalOnly
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$selection = "$Name-sel"
$control = "$Name-ctl"
if (-not $EvalOnly) {
    $env:DOTNET_ROOT = Join-Path $root ".tools\dotnet8"
    & (Join-Path $env:DOTNET_ROOT "dotnet.exe") build (Join-Path $root "godot\CreatureIA.Sim\CreatureIA.Sim.csproj") -c Release -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "CreatureIA.Sim build failed." }
    $train = Join-Path $PSScriptRoot "train.ps1"
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $common = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $train, "-Brain", $Brain, "-Worlds", $Worlds,
        "-Population", $Population, "-Ticks", $Ticks, "-Seed", $Seed, "-Plants", $Plants, "-Lakes", $Lakes,
        "-Rollout", $Rollout, "-MaxMinutes", $MaxMinutes.ToString($inv), "-NoBuild")
    if ($Island) { $common += "-Island" }
    # Separate processes (not jobs): each run waits for its own brain server like a manual launch.
    $runs = @(
        Start-Process powershell -ArgumentList ($common + @("-RunName", $selection, "-Port", 5700)) -NoNewWindow -PassThru
        Start-Process powershell -ArgumentList ($common + @("-RunName", $control, "-Port", 5701, "-Control")) -NoNewWindow -PassThru
    )
    Write-Host "stage 2: $selection (selection) and $control (control) running"
    $runs | ForEach-Object { $_.WaitForExit() }
}
$python = Join-Path $root ".venv\Scripts\python.exe"
$env:PYTHONPATH = Join-Path $root "brain\src"
& $python -m creature_brain.benchmarks.stage2_eval --selection (Join-Path $root "benchmarks\runs\$selection") `
    --control (Join-Path $root "benchmarks\runs\$control") --output (Join-Path $root "benchmarks\runs\eval-$Name")
