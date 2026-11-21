#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$publisher = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Publish-Nightweaver.ps1'))
$source = Split-Path -Parent (Split-Path -Parent $publisher)
$global:NightweaverPublisherFixture = @{ calls = 0; scenario = 'valid' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('publisher-contract-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $root 'target'
[IO.Directory]::CreateDirectory($target) | Out-Null
function Check($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Put([string]$Path, [string]$Content = 'fixture') {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}
# Replace only the native publish boundary; invoke the unchanged public script
# with real staging, validation, manifest, move and cleanup operations.
function dotnet {
    $global:NightweaverPublisherFixture.calls++
    $arguments = @($args)
    Check ($arguments[0] -eq 'publish') 'Unexpected dotnet command'
    Check (($arguments -join '|') -match '\|--configuration\|Release\|--self-contained\|false\|-p:UseAppHost=true\|--output\|') 'Packaging arguments changed'
    $output = $arguments[[Array]::IndexOf($arguments, '--output') + 1]
    $web = $arguments[1] -like '*Nightweaver.Web.csproj'
    if (!$web) {
        if ($global:NightweaverPublisherFixture.scenario -ne 'cli') { Put (Join-Path $output 'nightweaver.exe') }
        if ($global:NightweaverPublisherFixture.scenario -ne 'prompts') { Put (Join-Path $output 'prompts/implementer.md') }
    } else {
        if ($global:NightweaverPublisherFixture.scenario -ne 'web') { Put (Join-Path $output 'Nightweaver.Web.exe') }
        if ($global:NightweaverPublisherFixture.scenario -ne 'entry') {
            $html = '<script src="/vendor/helper.js"></script><script type="module" src="/studio/assets/app.js?v=1&amp;x=2"></script><link rel="stylesheet" href=''./assets/app.css#style''>'
            if ($global:NightweaverPublisherFixture.scenario -eq 'escape') { $html = '<script src="../../../outside.js"></script>' }
            if ($global:NightweaverPublisherFixture.scenario -eq 'empty') { $html = '<html></html>' }
            Put (Join-Path $output 'wwwroot/studio/index.html') $html
        }
        Put (Join-Path $output 'wwwroot/vendor/helper.js')
        if ($global:NightweaverPublisherFixture.scenario -ne 'js') { Put (Join-Path $output 'wwwroot/studio/assets/app.js') }
        if ($global:NightweaverPublisherFixture.scenario -ne 'css') { Put (Join-Path $output 'wwwroot/studio/assets/app.css') }
    }
    $global:LASTEXITCODE = 0
}
function Reject([string]$Destination, [string]$Expected, [int]$Calls) {
    $global:NightweaverPublisherFixture.calls = 0
    $failure = $null
    try { & $publisher -Destination $Destination -TargetRepository $target | Out-Null }
    catch { $failure = $_.Exception.Message }
    Check ($failure -like "*$Expected*") "Expected '$Expected', got '$failure'"
    Check ($global:NightweaverPublisherFixture.calls -eq $Calls) "Unexpected publish count for $global:NightweaverPublisherFixture.scenario"
}
try {
    $global:NightweaverPublisherFixture.scenario = 'valid'; $global:NightweaverPublisherFixture.calls = 0
    $destination = Join-Path $root 'valid pair'
    & $publisher -Destination $destination -TargetRepository $target | Out-Null
    Check ($global:NightweaverPublisherFixture.calls -eq 2) 'Expected paired publish'
    Check (!(Test-Path (Join-Path $destination 'web/wwwroot/index.html'))) 'Fixture must be Studio-only'
    $manifest = Get-Content -LiteralPath (Join-Path $destination 'nightweaver-deployment.json') -Raw | ConvertFrom-Json
    $revision = (& git -C $source rev-parse HEAD).Trim()
    Check ($LASTEXITCODE -eq 0 -and $manifest.build_version -ceq $revision) 'Manifest revision mismatch'
    Check ($manifest.schema_version -eq 1 -and $manifest.cli_executable -ceq 'nightweaver.exe' -and $manifest.web_executable -ceq 'web/Nightweaver.Web.exe') 'Manifest contract changed'
    Check (@($manifest.PSObject.Properties).Count -eq 4) 'Manifest schema changed'
    $inventory = Get-Content -LiteralPath (Join-Path $destination 'nightweaver-release.json') -Raw | ConvertFrom-Json
    Check ($inventory.schema_version -eq 1 -and $inventory.build_version -ceq $revision) 'Inventory identity mismatch'
    Check (($inventory.capabilities -join ',') -ceq 'paired-inventory-v1') 'Publisher claimed undelivered upgrade capabilities'
    $payload = @(Get-ChildItem -LiteralPath $destination -File -Recurse -Force | Where-Object Name -ne 'nightweaver-release.json')
    Check ($inventory.files.Count -eq $payload.Count) 'Inventory omitted payload'
    foreach ($file in $inventory.files) {
        $full = Join-Path $destination $file.path
        Check ((Get-Item -LiteralPath $full).Length -eq $file.length) 'Inventory length mismatch'
        Check ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ceq $file.sha256) 'Inventory hash mismatch'
    }
    foreach ($case in @(
        @('entry', 'Studio entry is missing'), @('js', 'Studio bundle is missing'),
        @('css', 'Studio bundle is missing'), @('cli', 'CLI apphost is missing'),
        @('web', 'Web Console apphost is missing'), @('prompts', 'role prompts are incomplete'),
        @('escape', 'outside wwwroot'), @('empty', 'no local JS/CSS bundles')
    )) {
        $global:NightweaverPublisherFixture.scenario = $case[0]
        $destination = Join-Path $root $global:NightweaverPublisherFixture.scenario
        Reject $destination $case[1] 2
        Check (!(Test-Path -LiteralPath $destination)) 'Failed publish exposed a destination'
        Check (@(Get-ChildItem -LiteralPath $root -Filter '*.staging.*').Count -eq 0) 'Failed publish leaked staging'
    }
    $global:NightweaverPublisherFixture.scenario = 'safety'
    $occupied = Join-Path $root 'occupied'
    Put (Join-Path $occupied 'sentinel') 'keep'
    Reject $occupied 'new or empty' 0
    Check (([IO.File]::ReadAllText((Join-Path $occupied 'sentinel'))) -ceq 'keep') 'Occupied destination changed'
    Put (Join-Path $target 'sentinel') 'target-keep'
    foreach ($overlap in @($target, (Join-Path $target 'child'), $root, $source, (Join-Path $source 'child'), (Split-Path -Parent $source))) {
        Reject $overlap 'separate from the source and Target Repository' 0
    }
    Check (([IO.File]::ReadAllText((Join-Path $target 'sentinel'))) -ceq 'target-keep') 'Target sentinel changed'
    Write-Host 'Publisher contract: 16 cases passed (Studio-only success, 8 asset/apphost failures, 7 destination guards).'
} finally {
    # Delete only this invocation's disposable tree, never source/target inputs.
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolvedRoot) -notlike 'publisher-contract-*') { throw 'Unsafe fixture cleanup path' }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    Remove-Variable -Name NightweaverPublisherFixture -Scope Global
}
