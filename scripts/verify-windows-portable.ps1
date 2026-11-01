param(
    [Parameter(Mandatory = $true)][string] $Archive,
    [switch] $AllowDevelopment
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$path = [IO.Path]::GetFullPath($Archive)
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Archive not found: $path" }
$zip = [IO.Compression.ZipFile]::OpenRead($path)
try {
    $entries = @($zip.Entries | Where-Object { $_.Name.Length -gt 0 })
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name.Contains('../') -or $name.Contains(':') -or -not $names.Add($name)) {
            throw "Unsafe or duplicate archive entry: $name"
        }
    }
    $manifestEntry = $entries | Where-Object FullName -EQ 'package-manifest.json' | Select-Object -First 1
    if ($null -eq $manifestEntry) { throw 'Package manifest missing.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'FormPotok.Text' -or
        $manifest.rid -ne 'win-x64' -or $manifest.signingStatus -ne 'unsigned' -or
        $manifest.sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid package identity.' }
    & (Join-Path $PSScriptRoot 'check-version.ps1') -Version ([string]$manifest.version) | Out-Null
    if ($manifest.developmentOnly -and -not $AllowDevelopment) { throw 'Development package cannot pass the release gate.' }
    $expected = @($manifest.files)
    if ($expected.Count + 1 -ne $entries.Count) { throw 'Manifest file count differs from archive.' }
    $required = @('FormPotok.Text.exe', 'tools/ocr-sidecar/runtime/formpotok-ocr-sidecar.exe',
        'tools/ocr-sidecar/models/paddlex/official_models/PP-OCRv5_mobile_det/inference.json',
        'tools/ocr-sidecar/models/paddlex/official_models/eslav_PP-OCRv5_mobile_rec/inference.json')
    foreach ($requiredPath in $required) {
        if (-not $names.Contains($requiredPath)) { throw "Required file missing: $requiredPath" }
    }
    $byName = @{}
    foreach ($entry in $entries) { $byName[$entry.FullName.Replace('\', '/')] = $entry }
    foreach ($file in $expected) {
        $name = [string]$file.path
        if (-not $byName.ContainsKey($name)) { throw "Manifest entry missing: $name" }
        $entry = $byName[$name]
        if ($entry.Length -ne [long]$file.size) { throw "Size mismatch: $name" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($digest -ne [string]$file.sha256) { throw "Hash mismatch: $name" }
    }
    [pscustomobject]@{
        passed = $true
        version = $manifest.version
        sourceCommit = $manifest.sourceCommit
        developmentOnly = $manifest.developmentOnly
        files = $expected.Count
        archiveSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json
}
finally { $zip.Dispose() }
