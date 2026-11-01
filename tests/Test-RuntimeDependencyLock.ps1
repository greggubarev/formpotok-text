$ErrorActionPreference = 'Stop'
$verify = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../scripts/verify-runtime-dependency-lock.ps1'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('formpotok-runtime-lock-' + [guid]::NewGuid().ToString('N'))

function Get-SnapshotHash([string[]] $Lines) {
    $sorted = [string[]]@($Lines)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $canonical = ($sorted -join "`n") + "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($canonical))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Assert-Rejected([string] $Label) {
    try { & $verify -ManifestPath (Join-Path $scratch 'manifest.json') -LockPath (Join-Path $scratch 'lock.txt') -WheelLockPath (Join-Path $scratch 'wheels.txt') | Out-Null }
    catch { Write-Output "PASS: rejects $Label"; return }
    throw "Dependency lock unexpectedly accepted: $Label"
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $lines = @('paddleocr==3.4.1', 'paddlepaddle==3.2.0')
    $wheelLines = @($lines | ForEach-Object { "$_ --hash=sha256:$('a' * 64)" })
    $wheelLines | Set-Content -LiteralPath (Join-Path $scratch 'wheels.txt')
    $manifest = @{ schemaVersion = '3.0'; runtimeIdentifier = 'win-x64'; dependencyCount = 2; dependencySnapshotSha256 = (Get-SnapshotHash $lines); wheelLockSha256 = (Get-FileHash -LiteralPath (Join-Path $scratch 'wheels.txt') -Algorithm SHA256).Hash.ToLowerInvariant() }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'manifest.json')
    $lines | Set-Content -LiteralPath (Join-Path $scratch 'lock.txt')
    $result = & $verify -ManifestPath (Join-Path $scratch 'manifest.json') -LockPath (Join-Path $scratch 'lock.txt') -WheelLockPath (Join-Path $scratch 'wheels.txt') | ConvertFrom-Json
    if (-not $result.passed -or $result.dependencyCount -ne 2) { throw 'Valid dependency lock was rejected.' }
    Write-Output 'PASS: accepts matching dependency snapshot'

    @('paddleocr==3.4.2', 'paddlepaddle==3.2.0') | Set-Content -LiteralPath (Join-Path $scratch 'lock.txt')
    Assert-Rejected 'changed version'
    @('paddleocr==3.4.1', 'paddle_ocr==3.4.1') | Set-Content -LiteralPath (Join-Path $scratch 'lock.txt')
    Assert-Rejected 'duplicate normalized package'
    @('paddleocr>=3.4.1', 'paddlepaddle==3.2.0') | Set-Content -LiteralPath (Join-Path $scratch 'lock.txt')
    Assert-Rejected 'unpinned dependency'
    @('paddleocr==3.4.1') | Set-Content -LiteralPath (Join-Path $scratch 'lock.txt')
    Assert-Rejected 'missing dependency'
    $lines | Set-Content -LiteralPath (Join-Path $scratch 'lock.txt')
    @($wheelLines[0], 'paddlepaddle==3.2.1 --hash=sha256:' + ('a' * 64)) | Set-Content -LiteralPath (Join-Path $scratch 'wheels.txt')
    Assert-Rejected 'wheel version mismatch'
    @($wheelLines[0], 'paddlepaddle==3.2.0 --hash=sha256:' + ('b' * 64)) | Set-Content -LiteralPath (Join-Path $scratch 'wheels.txt')
    Assert-Rejected 'changed wheel hash'
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^formpotok-runtime-lock-[0-9a-f]{32}$') {
        throw "Refusing unsafe test cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
