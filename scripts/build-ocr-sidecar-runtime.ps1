param(
    [Parameter(Mandatory = $true)][string] $RuntimeIdentifier,
    [string] $Python = "python",
    [string] $Configuration = "Release",
    [string] $OutputRoot = "output/ocr-sidecar-runtime",
    [string] $WheelhouseDirectory,
    [switch] $FingerprintOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sidecarRoot = Join-Path $repoRoot "tools/ocr-sidecar"
$outputRootPath = Join-Path $repoRoot $OutputRoot
$runtimeRoot = Join-Path $outputRootPath $RuntimeIdentifier
$buildRoot = Join-Path $outputRootPath ".build/$RuntimeIdentifier"
$venvRoot = Join-Path $buildRoot ".venv"
$workRoot = Join-Path $buildRoot "pyinstaller-work"
$specRoot = Join-Path $buildRoot "spec"
$runtimeName = "formpotok-ocr-sidecar"
$requirementsFileName = if ($RuntimeIdentifier -eq "osx-x64") { "requirements-cpu-osx-x64.txt" } else { "requirements-cpu.txt" }
$requirementsPath = Join-Path $sidecarRoot $requirementsFileName
$windowsDependencyLock = Join-Path $repoRoot 'packaging/win-x64-runtime-dependencies.lock'
$windowsWheelLock = Join-Path $repoRoot 'packaging/win-x64-cp310-wheels.lock'

function Get-RuntimeSourceFingerprint {
    param(
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][string] $Identifier
    )

    $paths = @(& git -C $Repository ls-files --cached --others --exclude-standard -- 'tools/ocr-sidecar' 'scripts/build-ocr-sidecar-runtime.ps1' 'scripts/verify-macos-native-closure.py' 'packaging/win-x64-runtime-dependencies.lock' 'packaging/win-x64-cp310-wheels.lock')
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inventory OCR runtime source files.' }
    $selectedRequirements = if ($Identifier -eq 'osx-x64') { 'requirements-cpu-osx-x64.txt' } else { 'requirements-cpu.txt' }
    $paths = @($paths | Where-Object {
        $_ -and
        $_ -notmatch '^tools/ocr-sidecar/test_[^/]+\.py$' -and
        ($_ -notmatch '^tools/ocr-sidecar/requirements-[^/]+\.txt$' -or $_ -eq "tools/ocr-sidecar/$selectedRequirements") -and
        ($_ -ne 'packaging/win-x64-runtime-dependencies.lock' -or $Identifier -eq 'win-x64') -and
        ($_ -ne 'packaging/win-x64-cp310-wheels.lock' -or $Identifier -eq 'win-x64') -and
        ($_ -ne 'scripts/verify-macos-native-closure.py' -or $Identifier.StartsWith('osx-'))
    } | Sort-Object -Unique)
    if ($paths -notcontains 'tools/ocr-sidecar/main.py' -or
        $paths -notcontains "tools/ocr-sidecar/$selectedRequirements" -or
        $paths -notcontains 'scripts/build-ocr-sidecar-runtime.ps1') {
        throw 'Required OCR runtime source files are missing.'
    }
    $files = @($paths | ForEach-Object {
        $path = $_.Replace('\', '/')
        $fullPath = Join-Path $Repository $path
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Missing OCR runtime input: $path" }
        [pscustomobject]@{
            path = $path
            sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $canonical = ($files | ForEach-Object { "$($_.path)`t$($_.sha256)" }) -join "`n"
    $dirty = @(& git -C $Repository status --porcelain=v1 --untracked-files=all -- $paths)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to check OCR runtime source status.' }
    return [pscustomobject]@{
        sha256 = Get-StringSha256 -Value ($canonical + "`n")
        files = $files
        sourceDirty = $dirty.Count -gt 0
    }
}

if ($RuntimeIdentifier -notin @("win-x64", "osx-arm64", "osx-x64", "linux-x64")) {
    throw "Unsupported runtime identifier: $RuntimeIdentifier"
}

function Get-HostPlatform {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return "windows"
    }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        return "macos"
    }
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        return "linux"
    }

    throw "Unsupported build host OS."
}

function Get-NormalizedArchitecture {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    switch ($architecture) {
        ([System.Runtime.InteropServices.Architecture]::X64) { return "x64" }
        ([System.Runtime.InteropServices.Architecture]::Arm64) { return "arm64" }
        default { throw "Unsupported build host architecture: $architecture" }
    }
}

function Get-StringSha256 {
    param([Parameter(Mandatory = $true)][string] $Value)

    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Value)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

if ($FingerprintOnly) {
    Get-RuntimeSourceFingerprint -Repository $repoRoot -Identifier $RuntimeIdentifier | ConvertTo-Json -Depth 5
    return
}

function Assert-PeArchitecture {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ExpectedArchitecture
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "Runtime is not a valid PE executable: $Path"
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "Runtime has an invalid PE header: $Path"
    }
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $actualArchitecture = switch ($machine) {
        0x8664 { "x64" }
        0xAA64 { "arm64" }
        default { "unknown-0x$($machine.ToString('x4'))" }
    }
    if ($actualArchitecture -ne $ExpectedArchitecture) {
        throw "PE architecture mismatch. Expected $ExpectedArchitecture, got $actualArchitecture."
    }
}

function Assert-NativeExecutableArchitecture {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Platform,
        [Parameter(Mandatory = $true)][string] $ExpectedArchitecture
    )

    if ($Platform -eq "windows") {
        Assert-PeArchitecture -Path $Path -ExpectedArchitecture $ExpectedArchitecture
        return
    }

    if ($Platform -eq "macos") {
        if (-not (Get-Command lipo -ErrorAction SilentlyContinue)) {
            throw "lipo is required to verify the macOS runtime architecture."
        }
        $architectures = (& lipo -archs $Path 2>&1 | Out-String).Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
        if ($LASTEXITCODE -ne 0 -or $architectures -notcontains $(if ($ExpectedArchitecture -eq "x64") { "x86_64" } else { "arm64" })) {
            throw "Mach-O architecture mismatch. Expected $ExpectedArchitecture, got: $($architectures -join ', ')."
        }
        return
    }

    if (-not (Get-Command file -ErrorAction SilentlyContinue)) {
        throw "file is required to verify the Linux runtime architecture."
    }
    $description = (& file -b $Path 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or ($ExpectedArchitecture -eq "x64" -and $description -notmatch 'x86-64|x86_64')) {
        throw "ELF architecture mismatch. Expected $ExpectedArchitecture, got: $description"
    }
}

$hostPlatform = Get-HostPlatform
$hostArchitecture = Get-NormalizedArchitecture
$expectedHost = switch ($RuntimeIdentifier) {
    "win-x64" { @{ Platform = "windows"; Architecture = "x64" } }
    "osx-arm64" { @{ Platform = "macos"; Architecture = "arm64" } }
    "osx-x64" { @{ Platform = "macos"; Architecture = "x64" } }
    "linux-x64" { @{ Platform = "linux"; Architecture = "x64" } }
}
if ($hostPlatform -ne $expectedHost.Platform -or $hostArchitecture -ne $expectedHost.Architecture) {
    throw "Runtime $RuntimeIdentifier must be built natively on $($expectedHost.Platform)/$($expectedHost.Architecture); current host is $hostPlatform/$hostArchitecture."
}

function Test-IsWindowsHost {
    $isWindowsVariable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
    if ($isWindowsVariable) {
        return [bool]$isWindowsVariable.Value
    }

    return $PSVersionTable.PSEdition -eq "Desktop"
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string] $Executable,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $FailureMessage
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)"
    }
}

function Get-MachODependencies {
    param([Parameter(Mandatory = $true)][string] $Path)

    $lines = @(& otool -L $Path 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect Mach-O dependencies: $Path"
    }

    return @($lines | Select-Object -Skip 1 | ForEach-Object {
        if ($_ -match '^\s*(\S+)\s+\(') { $Matches[1] }
    })
}

function Get-MachODylibIds {
    param([Parameter(Mandatory = $true)][string] $Path)

    $lines = @(& otool -D $Path 2>$null)
    if ($LASTEXITCODE -ne 0) { return @() }
    return @($lines | Select-Object -Skip 1 | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-MachOFiles {
    param([Parameter(Mandatory = $true)][string] $Root)

    $paths = @(& find $Root -type f -print 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inventory native files under $Root."
    }

    return @($paths | Where-Object {
        $description = (& file -b $_ 2>$null | Out-String).Trim()
        $LASTEXITCODE -eq 0 -and $description -match 'Mach-O'
    })
}

function Get-ExactMacArchitectureName {
    param([Parameter(Mandatory = $true)][string] $Architecture)

    if ($Architecture -eq "x64") { return "x86_64" }
    if ($Architecture -eq "arm64") { return "arm64" }
    throw "Unsupported macOS architecture: $Architecture"
}

function Resolve-MacExternalDependency {
    param(
        [Parameter(Mandatory = $true)][string] $Dependency,
        [Parameter(Mandatory = $true)][string[]] $SearchRoots
    )

    if ([System.IO.Path]::IsPathRooted($Dependency) -and (Test-Path -LiteralPath $Dependency -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $Dependency).Path
    }

    $name = [System.IO.Path]::GetFileName($Dependency)
    foreach ($root in $SearchRoots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $candidate = Get-ChildItem -LiteralPath $root -File -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Sort-Object FullName |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "Unable to vendor required macOS dependency '$Dependency'. Install a native GCC formula on the build host; the library will be copied into the runtime, not used from Homebrew on the client."
}

function Add-PortableMacDependencyClosure {
    param(
        [Parameter(Mandatory = $true)][string] $RuntimeDirectory,
        [Parameter(Mandatory = $true)][string] $RuntimeExecutable,
        [Parameter(Mandatory = $true)][string] $ExpectedArchitecture
    )

    foreach ($tool in @("codesign", "file", "find", "install_name_tool", "lipo", "otool")) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool is required to produce a portable macOS OCR runtime."
        }
    }

    $runtimeDirectory = [System.IO.Path]::GetFullPath($RuntimeDirectory)
    $vendorDirectory = Join-Path $RuntimeDirectory "_internal/formpotok-native-libs"
    New-Item -ItemType Directory -Path $vendorDirectory -Force | Out-Null

    $searchRoots = [System.Collections.Generic.List[string]]::new()
    if (Get-Command brew -ErrorAction SilentlyContinue) {
        foreach ($formula in @("gcc", "gcc@15", "gcc@14", "gcc@13")) {
            $prefix = (& brew --prefix $formula 2>$null | Out-String).Trim()
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($prefix)) {
                $searchRoots.Add($prefix)
            }
        }
    }
    foreach ($prefix in @("/opt/homebrew/opt/gcc", "/usr/local/opt/gcc")) {
        if (Test-Path -LiteralPath $prefix -PathType Container) { $searchRoots.Add($prefix) }
    }

    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($path in (Get-MachOFiles -Root $RuntimeDirectory)) { $queue.Enqueue($path) }
    $processed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $changed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $vendoredByName = @{}

    while ($queue.Count -gt 0) {
        $consumer = $queue.Dequeue()
        if (-not $processed.Add($consumer)) { continue }
        $ownDylibIds = @(Get-MachODylibIds -Path $consumer)

        foreach ($dependency in (Get-MachODependencies -Path $consumer)) {
            if ($ownDylibIds -contains $dependency) { continue }
            if ($dependency -match '^/(System/Library|usr/lib)/') { continue }

            $destination = $null
            $needsVendoring = [System.IO.Path]::IsPathRooted($dependency)
            if ($dependency.StartsWith("@rpath/", [System.StringComparison]::Ordinal)) {
                $dependencyName = [System.IO.Path]::GetFileName($dependency)
                $vendorCandidate = Join-Path $vendorDirectory $dependencyName
                if (Test-Path -LiteralPath $vendorCandidate -PathType Leaf) {
                    $destination = $vendorCandidate
                }
                else {
                    $needsVendoring = -not (Get-ChildItem -LiteralPath $RuntimeDirectory -File -Recurse -Filter $dependencyName -ErrorAction SilentlyContinue |
                        Select-Object -First 1)
                }
            }
            if (-not $needsVendoring -and $null -eq $destination) { continue }

            if ($needsVendoring) {
                $source = Resolve-MacExternalDependency -Dependency $dependency -SearchRoots $searchRoots.ToArray()
                $name = [System.IO.Path]::GetFileName($source)
                $destination = Join-Path $vendorDirectory $name
                $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
                if ($vendoredByName.ContainsKey($name) -and $vendoredByName[$name] -ne $sourceHash) {
                    throw "Conflicting native dependencies share the name '$name'."
                }

                if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
                    Invoke-NativeChecked -Executable "/bin/cp" -Arguments @("-L", $source, $destination) -FailureMessage "Unable to copy native dependency $name into the OCR runtime."
                    Invoke-NativeChecked -Executable "/bin/chmod" -Arguments @("u+w", $destination) -FailureMessage "Unable to make vendored dependency writable: $name"
                    $architectures = @((& lipo -archs $destination 2>&1 | Out-String).Trim().Split(' ', [StringSplitOptions]::RemoveEmptyEntries))
                    if ($LASTEXITCODE -ne 0 -or $architectures -notcontains $ExpectedArchitecture) {
                        throw "Vendored dependency '$source' does not contain $ExpectedArchitecture."
                    }
                    if ($architectures.Count -ne 1) {
                        $thinPath = "$destination.thin"
                        Invoke-NativeChecked -Executable "lipo" -Arguments @("-thin", $ExpectedArchitecture, $destination, "-output", $thinPath) -FailureMessage "Unable to thin $name to $ExpectedArchitecture."
                        Move-Item -LiteralPath $thinPath -Destination $destination -Force
                    }
                    $vendoredByName[$name] = $sourceHash
                    $queue.Enqueue($destination)
                    $changed.Add($destination) | Out-Null
                }
            }

            $relative = [System.IO.Path]::GetRelativePath((Split-Path -Parent $consumer), $destination).Replace('\', '/')
            $portableDependency = "@loader_path/$relative"
            Invoke-NativeChecked -Executable "/bin/chmod" -Arguments @("u+w", $consumer) -FailureMessage "Unable to make Mach-O consumer writable: $consumer"
            Invoke-NativeChecked -Executable "install_name_tool" -Arguments @("-change", $dependency, $portableDependency, $consumer) -FailureMessage "Unable to rewrite $dependency in $consumer."
            $changed.Add($consumer) | Out-Null
        }
    }

    foreach ($path in ($changed | Sort-Object { $_.Split('/').Count } -Descending)) {
        Invoke-NativeChecked -Executable "codesign" -Arguments @("--force", "--sign", "-", $path) -FailureMessage "Unable to ad-hoc sign rewritten Mach-O file: $path"
    }

    $verificationScript = Join-Path $PSScriptRoot "verify-macos-native-closure.py"
    Invoke-NativeChecked -Executable $venvPython -Arguments @(
        $verificationScript,
        $RuntimeDirectory,
        "--executable", $RuntimeExecutable,
        "--architecture", $ExpectedArchitecture
    ) -FailureMessage "macOS OCR runtime dependency closure verification failed."
}

function Test-PythonExecutable {
    param([Parameter(Mandatory = $true)][string] $Executable)

    try {
        $version = & $Executable -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" 2>$null
        return $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($version)
    }
    catch {
        return $false
    }
}

function Resolve-PythonExecutable {
    param([Parameter(Mandatory = $true)][string] $RequestedPython)

    if (Test-PythonExecutable -Executable $RequestedPython) {
        $command = Get-Command $RequestedPython -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }

        return $RequestedPython
    }

    if (Test-IsWindowsHost -and (Get-Command "py" -ErrorAction SilentlyContinue)) {
        foreach ($version in @("-3.10", "-3.11", "-3.12")) {
            $path = & py $version -c "import sys; print(sys.executable)" 2>$null
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($path) -and (Test-PythonExecutable -Executable $path.Trim())) {
                return $path.Trim()
            }
        }
    }

    throw "Python executable was not found or cannot run. Pass -Python with a Python 3.10-3.12 executable path."
}

$resolvedPython = Resolve-PythonExecutable -RequestedPython $Python
$sourceFingerprint = Get-RuntimeSourceFingerprint -Repository $repoRoot -Identifier $RuntimeIdentifier
$env:PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK = "True"
$env:PYTHONIOENCODING = "utf-8"

New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

if (Test-Path -LiteralPath $runtimeRoot) {
    Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
}

if (-not (Test-Path -LiteralPath $venvRoot)) {
    Invoke-NativeChecked -Executable $resolvedPython -Arguments @("-m", "venv", $venvRoot) -FailureMessage "Unable to create OCR runtime virtual environment."
}

$venvPython = if ($RuntimeIdentifier.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
    Join-Path $venvRoot "Scripts/python.exe"
}
else {
    Join-Path $venvRoot "bin/python"
}

if ($RuntimeIdentifier -eq 'win-x64') {
    $lockedPins = @(Get-Content -LiteralPath $windowsDependencyLock)
    foreach ($requirement in @(Get-Content -LiteralPath $requirementsPath | Where-Object { $_ -and -not $_.StartsWith('#') })) {
        if ($requirement -notmatch '^([A-Za-z0-9_.-]+)(?:\[[A-Za-z0-9_,.-]+\])?==([A-Za-z0-9.!+_-]+)$') {
            throw "Unpinned or unsupported Windows OCR requirement: $requirement"
        }
        $pin = "$($Matches[1])==$($Matches[2])"
        if ($lockedPins -notcontains $pin) { throw "Windows OCR requirement differs from dependency lock: $pin" }
    }
    $installArgs = @('-m', 'pip', 'install', '--only-binary=:all:', '--require-hashes')
    if ($WheelhouseDirectory) {
        $wheelhouse = [IO.Path]::GetFullPath($WheelhouseDirectory)
        if (-not (Test-Path -LiteralPath $wheelhouse -PathType Container)) { throw "Wheelhouse not found: $wheelhouse" }
        $installArgs += @('--no-index', '--find-links', $wheelhouse)
    }
    $installArgs += @('-r', $windowsWheelLock)
    Invoke-NativeChecked -Executable $venvPython -Arguments $installArgs -FailureMessage 'Unable to install hash-locked Windows OCR dependencies.'
    Invoke-NativeChecked -Executable $venvPython -Arguments @('-m', 'pip', 'check') -FailureMessage 'Windows OCR dependencies are incompatible.'
}
else {
    Invoke-NativeChecked -Executable $venvPython -Arguments @("-m", "pip", "install", "--upgrade", "pip", "wheel", "setuptools") -FailureMessage "Unable to update OCR runtime build tools."
    Invoke-NativeChecked -Executable $venvPython -Arguments @("-m", "pip", "install", "-r", $requirementsPath) -FailureMessage "Unable to install OCR runtime dependencies from $requirementsFileName."
    $pyInstallerVersion = if ($hostPlatform -eq "macos") { "6.21.0" } else { "6.17.0" }
    Invoke-NativeChecked -Executable $venvPython -Arguments @("-m", "pip", "install", "pyinstaller==$pyInstallerVersion") -FailureMessage "Unable to install PyInstaller."
}

$pyInstallerArgs = @(
    "-m", "PyInstaller",
    "--clean",
    "--noconfirm",
    "--name", $runtimeName,
    "--onedir",
    "--distpath", $runtimeRoot,
    "--workpath", $workRoot,
    "--specpath", $specRoot,
    "--collect-all", "paddle",
    "--collect-all", "paddleocr",
    "--collect-all", "paddlex",
    "--collect-all", "pypdfium2",
    "--collect-all", "PIL",
    "--collect-all", "pypdf",
    "--recursive-copy-metadata", "paddlex",
    "--recursive-copy-metadata", "paddleocr",
    "--copy-metadata", "beautifulsoup4",
    "--copy-metadata", "einops",
    "--copy-metadata", "ftfy",
    "--copy-metadata", "imagesize",
    "--copy-metadata", "Jinja2",
    "--copy-metadata", "lxml",
    "--copy-metadata", "opencv-contrib-python",
    "--copy-metadata", "openpyxl",
    "--copy-metadata", "premailer",
    "--copy-metadata", "pyclipper",
    "--copy-metadata", "pypdfium2",
    "--copy-metadata", "python-bidi",
    "--copy-metadata", "regex",
    "--copy-metadata", "safetensors",
    "--copy-metadata", "scikit-learn",
    "--copy-metadata", "scipy",
    "--copy-metadata", "sentencepiece",
    "--copy-metadata", "shapely",
    "--copy-metadata", "tiktoken",
    "--copy-metadata", "tokenizers",
    "--hidden-import", "paddleocr",
    "--hidden-import", "paddlex",
    "--hidden-import", "pypdfium2",
    (Join-Path $sidecarRoot "main.py")
)

Invoke-NativeChecked -Executable $venvPython -Arguments $pyInstallerArgs -FailureMessage "PyInstaller failed to build the OCR runtime."

$executable = if ($RuntimeIdentifier.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
    Join-Path $runtimeRoot "$runtimeName/$runtimeName.exe"
}
else {
    Join-Path $runtimeRoot "$runtimeName/$runtimeName"
}

if (-not (Test-Path -LiteralPath $executable)) {
    throw "PyInstaller did not produce expected sidecar executable: $executable"
}

Assert-NativeExecutableArchitecture -Path $executable -Platform $hostPlatform -ExpectedArchitecture $hostArchitecture

if ($hostPlatform -eq "macos") {
    Add-PortableMacDependencyClosure `
        -RuntimeDirectory (Join-Path $runtimeRoot $runtimeName) `
        -RuntimeExecutable $executable `
        -ExpectedArchitecture (Get-ExactMacArchitectureName -Architecture $hostArchitecture)
}

$gitCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitCommit)) {
    throw "Unable to resolve the runtime source commit."
}
$finalSourceFingerprint = Get-RuntimeSourceFingerprint -Repository $repoRoot -Identifier $RuntimeIdentifier
if ($finalSourceFingerprint.sha256 -ne $sourceFingerprint.sha256 -or
    $finalSourceFingerprint.sourceDirty -ne $sourceFingerprint.sourceDirty) {
    throw 'OCR runtime source changed during the build; discard this runtime and rebuild.'
}
$sourceDirty = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all).Count -gt 0

$dependencyLines = @(& $venvPython -m pip freeze --all)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to capture the OCR runtime dependency snapshot."
}
$sortedDependencies = [string[]]@($dependencyLines)
[Array]::Sort($sortedDependencies, [StringComparer]::Ordinal)
$dependencySnapshot = ($sortedDependencies -join "`n") + "`n"
if ($RuntimeIdentifier -eq 'win-x64') {
    $sortedLocked = [string[]]@(Get-Content -LiteralPath $windowsDependencyLock)
    [Array]::Sort($sortedLocked, [StringComparer]::Ordinal)
    $lockedSnapshot = ($sortedLocked -join "`n") + "`n"
    if ($dependencySnapshot -cne $lockedSnapshot) { throw 'Installed Windows dependencies differ from lock.' }
}
$toolVersionProbe = @'
import importlib.metadata as metadata
import json
import platform

def version(name):
    try:
        return metadata.version(name)
    except metadata.PackageNotFoundError:
        return None

print(json.dumps({
    "python": platform.python_version(),
    "pyinstaller": version("pyinstaller"),
    "paddleocr": version("paddleocr"),
    "paddlepaddle": version("paddlepaddle"),
    "paddlex": version("paddlex"),
}, sort_keys=True))
'@
$toolVersionsJson = ($toolVersionProbe | & $venvPython - | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($toolVersionsJson)) {
    throw "Unable to capture OCR runtime tool versions."
}
$toolVersions = $toolVersionsJson | ConvertFrom-Json

$executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$mainHash = (Get-FileHash -LiteralPath (Join-Path $sidecarRoot "main.py") -Algorithm SHA256).Hash.ToLowerInvariant()
$requirementsHash = (Get-FileHash -LiteralPath $requirementsPath -Algorithm SHA256).Hash.ToLowerInvariant()

$manifest = [pscustomobject]@{
    schemaVersion = "3.0"
    runtimeIdentifier = $RuntimeIdentifier
    platform = $hostPlatform
    architecture = $hostArchitecture
    configuration = $Configuration
    gitCommit = $gitCommit
    sourceDirty = $sourceDirty
    sourceFingerprint = $sourceFingerprint
    executableRelativePath = "$runtimeName/$($([System.IO.Path]::GetFileName($executable)))"
    executableSha256 = $executableHash
    sourceHashes = [pscustomobject]@{
        mainPySha256 = $mainHash
        requirementsFile = $requirementsFileName
        requirementsSha256 = $requirementsHash
    }
    dependencySnapshotSha256 = Get-StringSha256 -Value $dependencySnapshot
    dependencyCount = $dependencyLines.Count
    wheelLockSha256 = if ($RuntimeIdentifier -eq 'win-x64') { (Get-FileHash -LiteralPath $windowsWheelLock -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    toolVersions = $toolVersions
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtimeRoot "ocr-sidecar-runtime-manifest.json") -Encoding UTF8
$manifest | Format-List
