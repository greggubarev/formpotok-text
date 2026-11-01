param(
    [Parameter(Mandatory = $true)][string] $ManifestPath,
    [string] $LockPath,
    [string] $WheelLockPath
)

$ErrorActionPreference = 'Stop'
if (-not $LockPath) { $LockPath = Join-Path $PSScriptRoot '../packaging/win-x64-runtime-dependencies.lock' }
if (-not $WheelLockPath) { $WheelLockPath = Join-Path $PSScriptRoot '../packaging/win-x64-cp310-wheels.lock' }
$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
if ($manifest.schemaVersion -ne '3.0' -or $manifest.runtimeIdentifier -ne 'win-x64' -or
    $manifest.dependencySnapshotSha256 -notmatch '^[0-9a-f]{64}$' -or
    $manifest.wheelLockSha256 -notmatch '^[0-9a-f]{64}$' -or [int]$manifest.dependencyCount -le 0) {
    throw 'Runtime dependency manifest is incomplete.'
}
$lines = @(Get-Content -LiteralPath $LockPath | Where-Object { $_ -ne '' })
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in $lines) {
    if ($line -notmatch '^([A-Za-z0-9][A-Za-z0-9._-]*)==([A-Za-z0-9][A-Za-z0-9.!+_-]*)$' -or
        -not $names.Add(($Matches[1] -replace '[-_.]+', '-'))) {
        throw "Invalid or duplicate dependency pin: $line"
    }
}
if ($lines.Count -ne [int]$manifest.dependencyCount) { throw 'Runtime dependency count differs from lock.' }
$sorted = [string[]]@($lines)
[Array]::Sort($sorted, [StringComparer]::Ordinal)
$canonical = ($sorted -join "`n") + "`n"
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $digest = [BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($canonical))).Replace('-', '').ToLowerInvariant()
}
finally { $sha.Dispose() }
if ($digest -ne $manifest.dependencySnapshotSha256) { throw 'Runtime dependency snapshot differs from lock.' }
$wheelLines = @(Get-Content -LiteralPath $WheelLockPath | Where-Object { $_ -ne '' })
if ($wheelLines.Count -ne $lines.Count) { throw 'Wheel hash count differs from dependency lock.' }
for ($i = 0; $i -lt $lines.Count; $i++) {
    $prefix = "$($lines[$i]) --hash=sha256:"
    if (-not $wheelLines[$i].StartsWith($prefix, [StringComparison]::Ordinal) -or
        $wheelLines[$i].Substring($prefix.Length) -notmatch '^[0-9a-f]{64}$') {
        throw "Wheel hash entry differs from dependency pin: $($lines[$i])"
    }
}
$wheelDigest = (Get-FileHash -LiteralPath $WheelLockPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($wheelDigest -ne $manifest.wheelLockSha256) { throw 'Wheel hash lock differs from runtime manifest.' }
[pscustomobject]@{ passed = $true; dependencyCount = $lines.Count; snapshotSha256 = $digest; wheelLockSha256 = $wheelDigest } | ConvertTo-Json
