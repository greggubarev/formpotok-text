$ErrorActionPreference = 'Stop'
$script = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../scripts/check-version.ps1'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('formpotok-version-policy-' + [guid]::NewGuid().ToString('N'))

function Assert-Accepts([string] $Version, [string] $Repository = $scratch, [switch] $RequireTag) {
    $actual = & $script -Version $Version -Repository $Repository -RequireTag:$RequireTag
    if ($actual -ne $Version) { throw "Version was not accepted: $Version" }
    Write-Output "PASS: accepts $Version"
}

function Assert-Rejects([string] $Version, [string] $Repository = $scratch, [switch] $RequireTag) {
    try { & $script -Version $Version -Repository $Repository -RequireTag:$RequireTag | Out-Null }
    catch { Write-Output "PASS: rejects $Version"; return }
    throw "Version was unexpectedly accepted: $Version"
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    foreach ($version in @('0.1.0-preview.9', '0.1.0-preview.10', '0.1.0-rc.1', '0.1.0', '1.2.3')) {
        Assert-Accepts $version
    }
    foreach ($version in @('', 'v1.2.3', '01.2.3', '1.02.3', '1.2.03', '1.2.3-preview',
        '1.2.3-preview.0', '1.2.3-preview.01', '1.2.3-beta.1', '1.2.3+sha', '65536.0.0')) {
        Assert-Rejects $version
    }

    Set-Content -LiteralPath (Join-Path $scratch 'VERSION') -Value '1.0.0'
    & git init -q $scratch
    & git -C $scratch config user.name 'Version Test'
    & git -C $scratch config user.email 'version@example.invalid'
    & git -C $scratch add VERSION
    & git -C $scratch commit -qm 'Version test'
    & git -C $scratch tag -a v1.0.0 -m 'Version test'
    Assert-Accepts '1.0.0' -RequireTag
    Assert-Rejects '1.0.1' -RequireTag
    & git -C $scratch tag v1.0.1
    Assert-Rejects '1.0.1' -RequireTag
    Set-Content -LiteralPath (Join-Path $scratch 'VERSION') -Value '1.0.1'
    & git -C $scratch add VERSION
    & git -C $scratch commit -qm 'Next version'
    Assert-Rejects '1.0.0' -RequireTag
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^formpotok-version-policy-[0-9a-f]{32}$') {
        throw "Refusing unsafe test cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
