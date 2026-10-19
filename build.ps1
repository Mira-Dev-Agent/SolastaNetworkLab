param([string]$GameDir = 'X:\SteamLibrary\steamapps\common\Slasta_COTM', [switch]$Restore, [switch]$Test, [switch]$DiscoveryTests, [switch]$LegacyTransportTests)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
$previous = @{}
foreach ($name in @('APPDATA', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:APPDATA = Join-Path $PSScriptRoot '.runtime\AppData'
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.runtime'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    if ($Restore) {
        dotnet restore src/DirectTransport/DirectTransport.csproj --configfile NuGet.Config
        if ($LASTEXITCODE) { throw 'Compatibility project restore failed' }
        dotnet restore src/SolastaNetworkLab/SolastaNetworkLab.csproj --configfile NuGet.Config "-p:GameDir=$GameDir"
        if ($LASTEXITCODE) { throw 'Mod restore failed' }
        dotnet restore tests/TransportChecks/TransportChecks.csproj --configfile NuGet.Config "-p:GameDir=$GameDir"
        if ($LASTEXITCODE) { throw 'Test restore failed' }
        dotnet restore tests/UpdateChecks/UpdateChecks.csproj --configfile NuGet.Config
        if ($LASTEXITCODE) { throw 'Update test restore failed' }
        dotnet restore tests/DiscoveryChecks/DiscoveryChecks.csproj --configfile NuGet.Config
        if ($LASTEXITCODE) { throw 'Discovery test restore failed' }
    }
    dotnet build src/SolastaNetworkLab/SolastaNetworkLab.csproj -c Release --no-restore "-p:GameDir=$GameDir"
    if ($LASTEXITCODE) { throw 'Build failed' }
    dotnet build src/DirectTransport/DirectTransport.csproj -c Release --no-restore
    if ($LASTEXITCODE) { throw 'Compatibility assembly build failed' }
    if ($Test) {
        dotnet run --project tests/UpdateChecks/UpdateChecks.csproj -c Release --no-restore
        if ($LASTEXITCODE) { throw 'Update checks failed' }
    }
    if ($LegacyTransportTests) {
        dotnet run --project tests/TransportChecks/TransportChecks.csproj -c Release --no-restore "-p:GameDir=$GameDir"
        if ($LASTEXITCODE) { throw 'Legacy transport checks failed' }
    }
    if ($DiscoveryTests) {
        dotnet run --project tests/DiscoveryChecks/DiscoveryChecks.csproj -c Release --no-restore
        if ($LASTEXITCODE) { throw 'Discovery checks failed' }
    }
    $version = (Get-Content 'src/SolastaNetworkLab/Info.json' -Raw | ConvertFrom-Json).Version
    $out = Join-Path $PSScriptRoot "artifacts\v$version\SolastaNetworkLab"
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    Copy-Item -LiteralPath 'src/SolastaNetworkLab/bin/Release/net472/SolastaNetworkLab.dll' -Destination $out
    Copy-Item -LiteralPath 'src/DirectTransport/bin/Release/netstandard2.0/DirectTransport.dll' -Destination $out
    Copy-Item -LiteralPath 'src/SolastaNetworkLab/Info.json' -Destination $out
    Copy-Item -LiteralPath 'src/SolastaNetworkLab/ApplyUpdate.ps1' -Destination $out
    Copy-Item -LiteralPath 'README.md' -Destination $out
    New-Item -ItemType Directory -Path (Join-Path $out 'analysis') -Force | Out-Null
    Copy-Item -LiteralPath 'analysis/FINDINGS.md' -Destination (Join-Path $out 'analysis')
    Compress-Archive -LiteralPath $out -DestinationPath "artifacts/SolastaNetworkLab-$version.zip" -Force
    Write-Output "Package: $PSScriptRoot\artifacts\SolastaNetworkLab-$version.zip"
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}
