# T1.1 on CreatureIA.Sim: the four comparable runs, two at a time (the GPU is mostly idle with one).
# Keeps Windows awake while running. Each run resumes if re-launched with the same names.
param([long]$Ticks = 24000, [int]$Worlds = 6, [string]$Prefix = "t11s")
$ErrorActionPreference = "Continue"
$root = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
Add-Type -Namespace Win -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint f);'
[Win.Power]::SetThreadExecutionState([uint32]"0x80000001") | Out-Null   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED
$log = Join-Path $root "benchmarks\runs\night-$Prefix.log"
$env:DOTNET_ROOT = Join-Path $root ".tools\dotnet8"
& (Join-Path $env:DOTNET_ROOT "dotnet.exe") build (Join-Path $root "godot\CreatureIA.Sim\CreatureIA.Sim.csproj") -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { "$(Get-Date -Format s) build failed" | Add-Content $log; exit 1 }
$common = "-Ticks $Ticks -Seed 1 -Worlds $Worlds -Plants 2800 -Lakes 12 -NoBuild"
$pairs = @(
    @("-RunName $Prefix-v2 -Brain mlp -Port 5590", "-RunName $Prefix-v3-ctrl -Brain gru -Port 5591"),
    @("-RunName $Prefix-v3-nocur -Brain gru -Curiosity 0 -Port 5592", "-RunName $Prefix-v3-lr1e4 -Brain gru -LearningRate 1e-4 -Port 5593")
)
foreach ($pair in $pairs) {
    "$(Get-Date -Format s) start: $($pair -join ' | ')" | Add-Content $log
    $jobs = foreach ($runArgs in $pair) {
        Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\train.ps1`" $common $runArgs" -NoNewWindow -PassThru
    }
    $jobs | ForEach-Object { $_.WaitForExit() }
    "$(Get-Date -Format s) end pair" | Add-Content $log
}
"$(Get-Date -Format s) all done" | Add-Content $log
