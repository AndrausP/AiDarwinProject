param(
    [string]$Godot = $env:GODOT_EXE,
    # 0 = unthrottled: every frame advances exactly 1/60 s of simulated time (--fixed-fps 60)
    # and the simulation runs as fast as the hardware allows. >0 = wall-clock paced multiple.
    [float]$TimeScale = 0.0
)

$projectDirectory = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$toolRoot = (Resolve-Path -LiteralPath "$PSScriptRoot\..\..\.tools" -ErrorAction SilentlyContinue).Path
if ([string]::IsNullOrWhiteSpace($Godot) -and $toolRoot) {
    $Godot = Get-ChildItem -LiteralPath $toolRoot -Filter 'Godot*_console.exe' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object Name -Match 'mono|\.NET' |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($Godot) -or -not (Test-Path -LiteralPath $Godot)) {
    throw "Godot .NET executable not found. Set GODOT_EXE or extract the portable .NET build below '.tools/'."
}

# Godot 4.7 C# tooling cannot load the .NET 11 preview MSBuild; prefer the portable stable SDK.
if ($toolRoot -and (Test-Path -LiteralPath "$toolRoot\dotnet8\dotnet.exe")) {
    $env:DOTNET_ROOT = "$toolRoot\dotnet8"
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:PATH = "$toolRoot\dotnet8;$env:PATH"
}

& $Godot --headless --path $projectDirectory --build-solutions --quit
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($TimeScale -le 0) {
    Remove-Item Env:CREATURE_IA_TIME_SCALE -ErrorAction SilentlyContinue
    & $Godot --headless --fixed-fps 60 --path $projectDirectory
} else {
    $env:CREATURE_IA_TIME_SCALE = $TimeScale.ToString([Globalization.CultureInfo]::InvariantCulture)
    & $Godot --headless --path $projectDirectory
}
exit $LASTEXITCODE
