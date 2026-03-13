param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$dotnetCliHome = Join-Path $repoRoot ".dotnet-cli-home"

New-Item -ItemType Directory -Force -Path $dotnetCliHome | Out-Null
$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Invoke-DotnetCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Args
    )

    & dotnet @Args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet コマンド失敗: dotnet $($Args -join ' ')"
    }
}

if (-not $SkipBuild) {
    Invoke-DotnetCommand -Args @("build", "VoxArchive.sln", "-m:1", "-v:m")
}

Invoke-DotnetCommand -Args @("test", "tests/VoxArchive.IntegrationTests/VoxArchive.IntegrationTests.csproj", "-m:1", "-v:m")
Invoke-DotnetCommand -Args @("test", "tests/VoxArchive.LongRunTests/VoxArchive.LongRunTests.csproj", "-m:1", "-v:m")
