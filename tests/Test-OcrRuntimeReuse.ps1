$ErrorActionPreference = 'Stop'
$sourceRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("formpotok-runtime-reuse-" + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $scratch 'repo'
$runtimeParent = Join-Path $scratch 'runtime'
$runtime = Join-Path $runtimeParent 'formpotok-ocr-sidecar'

function Invoke-Git {
    param([string[]] $GitArgs)
    & git -C $repo @GitArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git failed: $($GitArgs -join ' ')" }
}

function Assert-Validation {
    param([bool] $Expected, [string] $Message, [string] $ExpectedError = '')
    $output = ''
    $passed = $false
    try {
        $output = & (Join-Path $repo 'scripts/build-windows-portable.ps1') `
            -RuntimeDirectory $runtime -ModelCacheDirectory $scratch -ValidateRuntimeOnly 2>&1 | Out-String
        $passed = $output -match 'OCR runtime validated'
    }
    catch {
        $output = $_.Exception.Message
    }
    if ($passed -ne $Expected) { throw "$Message`: $output" }
    if (-not $Expected -and $output -notmatch [regex]::Escape($ExpectedError)) {
        throw "$Message rejected for the wrong reason: $output"
    }
    Write-Output "PASS: $Message"
}

try {
    New-Item -ItemType Directory -Path (Join-Path $repo 'scripts'), (Join-Path $repo 'tools/ocr-sidecar'), $runtime -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRepo 'scripts/build-ocr-sidecar-runtime.ps1') -Destination (Join-Path $repo 'scripts')
    Copy-Item -LiteralPath (Join-Path $sourceRepo 'scripts/build-windows-portable.ps1') -Destination (Join-Path $repo 'scripts')
    Copy-Item -LiteralPath (Join-Path $sourceRepo 'tools/ocr-sidecar/main.py') -Destination (Join-Path $repo 'tools/ocr-sidecar')
    Copy-Item -LiteralPath (Join-Path $sourceRepo 'tools/ocr-sidecar/reading_order.py') -Destination (Join-Path $repo 'tools/ocr-sidecar')
    Copy-Item -LiteralPath (Join-Path $sourceRepo 'tools/ocr-sidecar/requirements-cpu.txt') -Destination (Join-Path $repo 'tools/ocr-sidecar')
    Copy-Item -LiteralPath (Join-Path $sourceRepo 'tools/ocr-sidecar/test_main.py') -Destination (Join-Path $repo 'tools/ocr-sidecar')
    Set-Content -LiteralPath (Join-Path $repo 'VERSION') -Value '1.0.0'
    Set-Content -LiteralPath (Join-Path $runtime 'formpotok-ocr-sidecar.exe') -Value 'binary fixture'
    & git init -q $repo
    if ($LASTEXITCODE -ne 0) { throw 'git init failed' }
    Invoke-Git -GitArgs @('config', 'user.name', 'Test')
    Invoke-Git -GitArgs @('config', 'user.email', 'test@example.invalid')
    Invoke-Git -GitArgs @('add', '.')
    Invoke-Git -GitArgs @('commit', '-qm', 'initial')

    $fingerprint = & (Join-Path $repo 'scripts/build-ocr-sidecar-runtime.ps1') -RuntimeIdentifier win-x64 -FingerprintOnly | ConvertFrom-Json
    $commit = (& git -C $repo rev-parse HEAD).Trim()
    $manifest = [ordered]@{
        schemaVersion = '3.0'; runtimeIdentifier = 'win-x64'; platform = 'windows'; architecture = 'x64'
        configuration = 'Release'; gitCommit = $commit; sourceDirty = $false
        executableRelativePath = 'formpotok-ocr-sidecar/formpotok-ocr-sidecar.exe'
        executableSha256 = (Get-FileHash -LiteralPath (Join-Path $runtime 'formpotok-ocr-sidecar.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceFingerprint = $fingerprint; createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $manifestPath = Join-Path $runtimeParent 'ocr-sidecar-runtime-manifest.json'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath
    Assert-Validation $true 'matching runtime'

    Set-Content -LiteralPath (Join-Path $repo 'VERSION') -Value '1.0.1'
    Invoke-Git -GitArgs @('add', 'VERSION')
    Invoke-Git -GitArgs @('commit', '-qm', 'UI-only release')
    Assert-Validation $true 'different commit with unchanged OCR inputs'

    Add-Content -LiteralPath (Join-Path $repo 'tools/ocr-sidecar/test_main.py') -Value '# test-only change'
    Invoke-Git -GitArgs @('add', 'tools/ocr-sidecar/test_main.py')
    Invoke-Git -GitArgs @('commit', '-qm', 'test-only change')
    Assert-Validation $true 'test-only source change'

    $pythonPath = Join-Path $repo 'tools/ocr-sidecar/reading_order.py'
    $originalPython = Get-Content -Raw -LiteralPath $pythonPath
    $requirementsPath = Join-Path $repo 'tools/ocr-sidecar/requirements-cpu.txt'
    $originalRequirements = Get-Content -Raw -LiteralPath $requirementsPath
    Add-Content -LiteralPath $pythonPath -Value '# changed'
    Assert-Validation $false 'dirty OCR source' 'OCR runtime source is dirty'
    Invoke-Git -GitArgs @('add', 'tools/ocr-sidecar/reading_order.py')
    Invoke-Git -GitArgs @('commit', '-qm', 'change OCR source')
    Assert-Validation $false 'changed local Python source' 'OCR runtime inputs differ'

    Set-Content -LiteralPath $pythonPath -Value $originalPython -NoNewline
    Invoke-Git -GitArgs @('add', 'tools/ocr-sidecar/reading_order.py')
    Invoke-Git -GitArgs @('commit', '-qm', 'restore OCR source')
    Add-Content -LiteralPath $requirementsPath -Value '# changed'
    Assert-Validation $false 'changed requirements' 'OCR runtime source is dirty'
    Set-Content -LiteralPath $requirementsPath -Value $originalRequirements -NoNewline
    if (@(& git -C $repo status --porcelain=v1 -- 'tools/ocr-sidecar/requirements-cpu.txt').Count -ne 0) {
        throw 'Requirements fixture was not restored.'
    }
    Set-Content -LiteralPath (Join-Path $runtime 'formpotok-ocr-sidecar.exe') -Value 'corrupt binary'
    Assert-Validation $false 'corrupted binary' 'OCR runtime executable SHA-256 mismatch'

    $manifest.schemaVersion = '2.0'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath
    Assert-Validation $false 'legacy manifest' 'OCR runtime manifest lacks complete'
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if (-not $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedScratch) -notmatch '^formpotok-runtime-reuse-[0-9a-f]{32}$') {
        throw "Refusing unsafe test cleanup path: $resolvedScratch"
    }
    if (Test-Path -LiteralPath $resolvedScratch) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
}
