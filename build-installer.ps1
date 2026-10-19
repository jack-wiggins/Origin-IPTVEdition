[CmdletBinding()]
param(
    # Defaults to the <Version> in Directory.Build.props.
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSCommandPath
$publishRoot = Join-Path $projectRoot 'artifacts\publish'
$appPublish = Join-Path $publishRoot 'Origin'
$appProject = Join-Path $projectRoot 'src\Origin.App\Origin.App.csproj'
$nugetConfig = Join-Path $projectRoot 'nuget.config'
$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'

if (-not (Test-Path -LiteralPath $iscc)) {
    throw 'Inno Setup 6 was not found at C:\Program Files (x86)\Inno Setup 6\ISCC.exe.'
}

if (-not $PSBoundParameters.ContainsKey('Version')) {
    [xml]$buildProps = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $versionNode = $buildProps.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $versionNode -or $versionNode.InnerText.Trim() -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw 'Directory.Build.props has no valid <Version>. Pass -Version instead.'
    }
    $Version = $versionNode.InnerText.Trim()
}

dotnet restore (Join-Path $projectRoot 'Origin.sln') --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) {
    throw "NuGet restore failed with exit code $LASTEXITCODE."
}
# Recreate the publish output so files from earlier builds are not repackaged.
if (Test-Path -LiteralPath $appPublish) {
    $resolvedPublish = (Resolve-Path -LiteralPath $appPublish).ProviderPath
    $expectedParent = [System.IO.Path]::GetFullPath($publishRoot).TrimEnd('\') + '\'
    if (-not $resolvedPublish.StartsWith($expectedParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The publish output path is outside the expected publish directory.'
    }
    $publishEntries = @((Get-Item -LiteralPath $resolvedPublish -Force)) + @(Get-ChildItem -LiteralPath $resolvedPublish -Recurse -Force)
    if ($publishEntries | Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }) {
        throw 'The publish output directory contains a link and cannot be cleaned automatically.'
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

# Publishes OriginHTPC.exe with LibVLC for x86 and x64 in libvlc\win-x86 and libvlc\win-x64.
dotnet publish $appProject --no-restore --configuration Release "-p:Version=$Version" --output $appPublish
if ($LASTEXITCODE -ne 0) {
    throw "Origin publish failed with exit code $LASTEXITCODE."
}
& $iscc "/DVersion=$Version" (Join-Path $projectRoot 'installer\Origin.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}
