param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

if (-not $SkipBuild) {
    dotnet build VoxArchive.sln -m:1 -v:m
}

dotnet test tests/VoxArchive.IntegrationTests/VoxArchive.IntegrationTests.csproj -m:1 -v:m
dotnet test tests/VoxArchive.LongRunTests/VoxArchive.LongRunTests.csproj -m:1 -v:m
