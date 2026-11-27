# T1.1 overnight: comparable runs to diagnose the v3 regression, run back to back.
# Keeps Windows awake while running (no admin needed). Each run resumes if re-launched.
param([int]$Ticks = 24000, [int]$Worlds = 6, [string]$Prefix = "t11b")
$ErrorActionPreference = "Continue"
$root = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
Add-Type -Namespace Win -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint f);'
[Win.Power]::SetThreadExecutionState([uint32]"0x80000001") | Out-Null   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED

$common = @{ Ticks = $Ticks; Seed = 1; Worlds = $Worlds; Plants = 2800; Lakes = 12 }
$runs = @(
    @{ RunName = "$Prefix-v2";        Brain = "mlp" },
    @{ RunName = "$Prefix-v3-ctrl";   Brain = "gru" },
    @{ RunName = "$Prefix-v3-nocur";  Brain = "gru"; Curiosity = 0.0 },
    @{ RunName = "$Prefix-v3-lr1e4";  Brain = "gru"; LearningRate = 1e-4 }
)
$log = Join-Path $root "benchmarks\runs\night-$Prefix.log"
foreach ($r in $runs) {
    $a = $common + $r
    "$(Get-Date -Format s) start $($r.RunName) ($Worlds worlds x $Ticks ticks)" | Add-Content $log
    & (Join-Path $PSScriptRoot "train-ppo.ps1") @a
    "$(Get-Date -Format s) end $($r.RunName) exit $LASTEXITCODE" | Add-Content $log
}
"$(Get-Date -Format s) all done" | Add-Content $log
