param(
    [Parameter(Mandatory = $true)][string] $PortableArchive,
    [string] $IsccPath = "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe",
    [string] $OutputRoot = 'output/installer'
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$archive = [IO.Path]::GetFullPath($PortableArchive)
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Portable archive not found: $archive" }
$verification = & (Join-Path $PSScriptRoot 'verify-windows-portable.ps1') -Archive $archive | ConvertFrom-Json
if (-not $verification.passed -or $verification.developmentOnly) { throw 'Portable archive did not pass verification.' }
$version = (Get-Content -Raw -LiteralPath (Join-Path $repo 'VERSION')).Trim()
& (Join-Path $PSScriptRoot 'check-version.ps1') -Version $version | Out-Null
if ($verification.version -ne $version) { throw 'Archive and source versions differ.' }
$iscc = [IO.Path]::GetFullPath($IsccPath)
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw "Inno Setup compiler not found: $iscc" }
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be inside the repository.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$setup = Join-Path $output "FormPotok.Text-win-x64-v$version-Setup.exe"
if (Test-Path -LiteralPath $setup) { throw "Installer already exists: $setup" }
$head = (& git -C $repo rev-parse HEAD).Trim()
$dirty = (& git -C $repo status --porcelain=v1 --untracked-files=all | Out-String).Trim()
if ($head -ne $verification.sourceCommit -or $dirty.Length -gt 0) {
    throw 'Installer must be built from the clean commit recorded in the verified archive.'
}
$source = Join-Path $output ('.installer-source-' + [guid]::NewGuid().ToString('N'))
$drive = $null
foreach ($letter in @('Z', 'Y', 'X', 'W', 'V')) {
    $candidate = "${letter}:"
    if (-not (Test-Path -LiteralPath "$candidate\")) { $drive = $candidate; break }
}
if ($null -eq $drive) { throw 'No free temporary drive letter for installer compilation.' }
$mapped = $false
try {
    New-Item -ItemType Directory -Path $source | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $source
    if (-not (Test-Path -LiteralPath (Join-Path $source 'Assets/formpotok-mark.ico') -PathType Leaf)) {
        throw 'Portable archive is missing the installer icon.'
    }
    & subst $drive $source
    $mapped = $LASTEXITCODE -eq 0
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath "$drive\FormPotok.Text.exe")) {
        throw 'Cannot map a short installer source path.'
    }
    & $iscc '/Qp' "/DSourceDir=$drive" "/DAppVersion=$version" "/O$output" (Join-Path $repo 'packaging/FormPotok.Text.iss')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer build failed.' }
    $installerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup).ProductVersion.Trim()
    if ($installerVersion -ne $version) { throw "Installer product version differs from VERSION: '$installerVersion'." }
}
finally {
    if ($mapped) { & subst $drive /D | Out-Null }
    $resolvedSource = [IO.Path]::GetFullPath($source)
    $insideOutput = $resolvedSource.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    $ownedName = (Split-Path -Leaf $resolvedSource) -match '^\.installer-source-[0-9a-f]{32}$'
    if ($insideOutput -and $ownedName -and (Test-Path -LiteralPath $resolvedSource)) {
        Remove-Item -LiteralPath $resolvedSource -Recurse -Force
    }
}
[pscustomobject]@{
    installer = $setup
    sha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    version = $version
    sourceCommit = $verification.sourceCommit
} | ConvertTo-Json
