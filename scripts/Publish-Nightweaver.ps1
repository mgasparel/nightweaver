[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination,

    [Parameter(Mandatory = $true)]
    [string]$TargetRepository
)

$ErrorActionPreference = 'Stop'

function Get-FullPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath)
    while ($fullPath.Length -gt $pathRoot.Length -and
        ($fullPath.EndsWith([System.IO.Path]::DirectorySeparatorChar) -or
            $fullPath.EndsWith([System.IO.Path]::AltDirectorySeparatorChar))) {
        $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
    }
    return $fullPath
}

function Test-ContainsPath([string]$Parent, [string]$Child) {
    $comparison = [System.StringComparison]::OrdinalIgnoreCase
    $parentFull = Get-FullPath $Parent
    $childFull = Get-FullPath $Child
    if ($parentFull.Equals($childFull, $comparison)) { return $true }
    $prefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    return $childFull.StartsWith($prefix, $comparison)
}

$repositoryRoot = Get-FullPath (Join-Path $PSScriptRoot '..')
$destinationFull = Get-FullPath $Destination
$targetRepositoryFull = Get-FullPath $TargetRepository
$destinationParent = Split-Path -Parent $destinationFull

if (-not (Test-Path -LiteralPath $targetRepositoryFull -PathType Container)) {
    throw "Target Repository must be an existing directory: $targetRepositoryFull"
}
foreach ($protectedPath in @($repositoryRoot, $targetRepositoryFull)) {
    if ((Test-ContainsPath $protectedPath $destinationFull) -or (Test-ContainsPath $destinationFull $protectedPath)) {
        throw "Destination must be separate from the source and Target Repository: $destinationFull"
    }
}

if (Test-Path -LiteralPath $destinationFull) {
    if (-not (Test-Path -LiteralPath $destinationFull -PathType Container)) {
        throw "Destination exists and is not a directory: $destinationFull"
    }
    $existingItems = @(Get-ChildItem -LiteralPath $destinationFull -Force)
    if ($existingItems.Count -ne 0) {
        throw "Destination must be new or empty; existing data was left unchanged: $destinationFull"
    }
}

if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
    [System.IO.Directory]::CreateDirectory($destinationParent) | Out-Null
}

$stageName = (Split-Path -Leaf $destinationFull) + '.staging.' + [guid]::NewGuid().ToString('N')
$stageFull = Get-FullPath (Join-Path $destinationParent $stageName)
$parentFull = Get-FullPath $destinationParent
$stagePrefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar
if (-not $stageFull.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary publish path escaped its destination parent: $stageFull"
}

$cliProject = Join-Path $repositoryRoot 'src/Nightweaver/Nightweaver.csproj'
$webProject = Join-Path $repositoryRoot 'src/Nightweaver.Web/Nightweaver.Web.csproj'
$cliOutput = $stageFull
$webOutput = Join-Path $stageFull 'web'
[System.IO.Directory]::CreateDirectory($webOutput) | Out-Null

try {
    $cliArgs = @('publish', $cliProject, '--configuration', 'Release', '--self-contained', 'false', '-p:UseAppHost=true', '--output', $cliOutput)
    & dotnet @cliArgs
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE" }

    $webArgs = @('publish', $webProject, '--configuration', 'Release', '--self-contained', 'false', '-p:UseAppHost=true', '--output', $webOutput)
    & dotnet @webArgs
    if ($LASTEXITCODE -ne 0) { throw "Web Console publish failed with exit code $LASTEXITCODE" }

    $cliExecutable = Join-Path $stageFull 'nightweaver.exe'
    $webExecutable = Join-Path $webOutput 'Nightweaver.Web.exe'
    if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf)) { throw "Published CLI apphost is missing: $cliExecutable" }
    if (-not (Test-Path -LiteralPath $webExecutable -PathType Leaf)) { throw "Published Web Console apphost is missing: $webExecutable" }
    if (-not (Test-Path -LiteralPath (Join-Path $stageFull 'prompts/implementer.md') -PathType Leaf)) {
        throw 'Published CLI role prompts are incomplete.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $webOutput 'wwwroot/index.html') -PathType Leaf)) {
        throw 'Published Web Console static assets are incomplete.'
    }

    $buildVersion = (& git -C $repositoryRoot rev-parse --verify HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($buildVersion)) { $buildVersion = 'unknown' }
    $manifest = [ordered]@{
        schema_version = 1
        build_version = $buildVersion
        cli_executable = 'nightweaver.exe'
        web_executable = 'web/Nightweaver.Web.exe'
    }
    $manifestPath = Join-Path $stageFull 'nightweaver-deployment.json'
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $destinationFull) {
        $existingItems = @(Get-ChildItem -LiteralPath $destinationFull -Force)
        if ($existingItems.Count -ne 0) { throw "Destination changed during publish; existing data was left unchanged: $destinationFull" }
        [System.IO.Directory]::Delete($destinationFull)
    }
    [System.IO.Directory]::Move($stageFull, $destinationFull)
    Write-Output "Published Nightweaver CLI and Web Console build $buildVersion to $destinationFull"
    Write-Output "The runtime requires .NET 10 and ASP.NET Core 10. Background v1 starts manually and requires manual restart after a crash."
}
finally {
    if (Test-Path -LiteralPath $stageFull -PathType Container) {
        $resolvedStage = Get-FullPath $stageFull
        if (-not $resolvedStage.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected temporary directory: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
