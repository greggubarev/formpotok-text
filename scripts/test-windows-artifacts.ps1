param(
    [Parameter(Mandatory = $true)][string] $PortableArchive,
    [Parameter(Mandatory = $true)][string] $Installer,
    [Parameter(Mandatory = $true)][string] $OutputRoot,
    [string] $RunnerPath,
    [string] $ReferenceManifest
)

$ErrorActionPreference = 'Stop'
if (-not $RunnerPath) { $RunnerPath = Join-Path $PSScriptRoot 'runner/LocalTextSmoke.exe' }
if (-not $ReferenceManifest) { $ReferenceManifest = Join-Path $PSScriptRoot 'sample-letter.reference.json' }

function Invoke-CheckedProcess([string] $Executable, [string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Process failed ($($process.ExitCode)): $Executable`n$err`n$out" }
        return $out
    }
    finally { $process.Dispose() }
}

function Assert-PackageVersion([string] $Executable, [string] $Version, [string] $Commit) {
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion.Trim()
    if ($actual -notin @($Version, "$Version+$Commit")) {
        throw "App version mismatch in $Executable : $actual"
    }
}

$archive = [IO.Path]::GetFullPath($PortableArchive)
$setup = [IO.Path]::GetFullPath($Installer)
$runner = [IO.Path]::GetFullPath($RunnerPath)
$reference = [IO.Path]::GetFullPath($ReferenceManifest)
$output = [IO.Path]::GetFullPath($OutputRoot)
foreach ($required in @($archive, $setup, $runner, $reference)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file missing: $required" }
}
if (Test-Path -LiteralPath $output) { throw "Use a fresh OutputRoot: $output" }
$verify = Join-Path $PSScriptRoot 'scripts/verify-windows-portable.ps1'
if (-not (Test-Path -LiteralPath $verify)) { throw "Verifier missing: $verify" }
$package = & $verify -Archive $archive | ConvertFrom-Json
if (-not $package.passed -or $package.developmentOnly) { throw 'Portable archive failed verification.' }
$setupVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup).ProductVersion.Trim()
if ($setupVersion -ne $package.version) { throw 'Installer and portable versions differ.' }

New-Item -ItemType Directory -Path $output | Out-Null
$portable = Join-Path $output 'portable'
Expand-Archive -LiteralPath $archive -DestinationPath $portable
$portableExe = Join-Path $portable 'FormPotok.Text.exe'
Assert-PackageVersion $portableExe $package.version $package.sourceCommit
$models = Join-Path $portable 'tools/ocr-sidecar/models/paddlex'
$modelVerifier = Join-Path $PSScriptRoot 'scripts/verify-model-lock.ps1'
if (-not (Test-Path -LiteralPath $modelVerifier -PathType Leaf)) { throw 'Model verifier missing.' }
$modelLock = Join-Path $portable 'tools/ocr-sidecar/model-lock.json'
$modelEvidence = & $modelVerifier -ModelRoot (Join-Path $models 'official_models') -LockPath $modelLock | ConvertFrom-Json
if (-not $modelEvidence.passed -or $modelEvidence.modelCount -ne 5) { throw 'Bundled models differ from lock.' }
$dependencyVerifier = Join-Path $PSScriptRoot 'scripts/verify-runtime-dependency-lock.ps1'
if (-not (Test-Path -LiteralPath $dependencyVerifier -PathType Leaf)) { throw 'Dependency verifier missing.' }
$dependencyEvidence = & $dependencyVerifier -ManifestPath (Join-Path $portable 'tools/ocr-sidecar/runtime-manifest.json') -LockPath (Join-Path $portable 'tools/ocr-sidecar/runtime-dependencies.lock') -WheelLockPath (Join-Path $portable 'tools/ocr-sidecar/runtime-wheels.lock') | ConvertFrom-Json
if (-not $dependencyEvidence.passed) { throw 'Bundled runtime dependencies differ from lock.' }
$formats = @('png', 'jpg', 'pdf')
$portableResults = @()
foreach ($format in $formats) {
    $result = Invoke-CheckedProcess $runner @($portable, (Join-Path $portable "sample-letter.$format"),
        (Join-Path $output "portable-smoke-$format"), $models, $reference) | ConvertFrom-Json
    if (-not $result.passed -or $result.comparison.cer -ne 0 -or $result.comparison.wer -ne 0 -or
        @($result.comparison.perPage | Where-Object { -not $_.markerPresent }).Count -gt 0) {
        throw "Portable OCR smoke failed: $format"
    }
    $portableResults += [pscustomobject]@{ format = $format; cer = $result.comparison.cer; wer = $result.comparison.wer }
}

$installed = Join-Path $output 'installed'
$installLog = Join-Path $output 'install.log'
$uninstallExit = $null
try {
    Invoke-CheckedProcess $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installed", "/LOG=$installLog") | Out-Null
    $installedExe = Join-Path $installed 'FormPotok.Text.exe'
    Assert-PackageVersion $installedExe $package.version $package.sourceCommit
    if ((Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $portableExe -Algorithm SHA256).Hash) { throw 'Installed executable differs from verified ZIP.' }
    $installedModelEvidence = & $modelVerifier -ModelRoot (Join-Path $installed 'tools/ocr-sidecar/models/paddlex/official_models') -LockPath (Join-Path $installed 'tools/ocr-sidecar/model-lock.json') | ConvertFrom-Json
    if (-not $installedModelEvidence.passed -or $installedModelEvidence.lockSha256 -ne $modelEvidence.lockSha256) {
        throw 'Installed models differ from verified ZIP.'
    }
    $installedDependencyEvidence = & $dependencyVerifier -ManifestPath (Join-Path $installed 'tools/ocr-sidecar/runtime-manifest.json') -LockPath (Join-Path $installed 'tools/ocr-sidecar/runtime-dependencies.lock') -WheelLockPath (Join-Path $installed 'tools/ocr-sidecar/runtime-wheels.lock') | ConvertFrom-Json
    if (-not $installedDependencyEvidence.passed -or $installedDependencyEvidence.snapshotSha256 -ne $dependencyEvidence.snapshotSha256 -or
        $installedDependencyEvidence.wheelLockSha256 -ne $dependencyEvidence.wheelLockSha256) {
        throw 'Installed runtime dependencies differ from verified ZIP.'
    }
    $installedResults = @()
    foreach ($format in $formats) {
        $result = Invoke-CheckedProcess $runner @($installed, (Join-Path $installed "sample-letter.$format"),
            (Join-Path $output "installed-smoke-$format"), (Join-Path $installed 'tools/ocr-sidecar/models/paddlex'), $reference) | ConvertFrom-Json
        if (-not $result.passed -or $result.comparison.cer -ne 0 -or $result.comparison.wer -ne 0 -or
            @($result.comparison.perPage | Where-Object { -not $_.markerPresent }).Count -gt 0) {
            throw "Installed OCR smoke failed: $format"
        }
        $installedResults += [pscustomobject]@{ format = $format; cer = $result.comparison.cer; wer = $result.comparison.wer }
    }
}
finally {
    $uninstaller = Join-Path $installed 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        Invoke-CheckedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') | Out-Null
        $uninstallExit = 0
    }
}
if ($uninstallExit -ne 0 -or (Test-Path -LiteralPath (Join-Path $installed 'FormPotok.Text.exe'))) {
    throw 'Uninstall did not remove the app executable.'
}

$result = [pscustomobject]@{
    passed = $true
    scope = 'same-machine-artifact-smoke'
    version = $package.version
    sourceCommit = $package.sourceCommit
    portableSha256 = $package.archiveSha256
    installerSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    modelLockSha256 = $modelEvidence.lockSha256
    dependencySnapshotSha256 = $dependencyEvidence.snapshotSha256
    wheelLockSha256 = $dependencyEvidence.wheelLockSha256
    portableRecognitionPassed = $portableResults.Count -eq $formats.Count
    installedRecognitionPassed = $installedResults.Count -eq $formats.Count
    portableFormats = $portableResults
    installedFormats = $installedResults
    uninstallPassed = $true
    cleanOfflineMachineVerified = $false
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'artifact-smoke-summary.json') -Encoding utf8
$result | ConvertTo-Json
