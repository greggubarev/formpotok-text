param(
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$Corpus,
    [Parameter(Mandatory=$true)][string]$Results,
    [string]$ModelCache = '',
    [ValidateSet('eslav','cyrillic')][string]$RecognizerModel = 'eslav',
    [string]$SourceSidecarScript = '',
    [string]$SourcePython = '',
    [string[]]$SmokeFlags = @()
)

$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $Package).Path
$corpusRoot = (Resolve-Path -LiteralPath $Corpus).Path
$manifestPath = Join-Path $corpusRoot 'corpus.reference.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$resultRoot = [IO.Path]::GetFullPath($Results)
[IO.Directory]::CreateDirectory($resultRoot) | Out-Null
$modelRoot = if ($ModelCache) { (Resolve-Path -LiteralPath $ModelCache).Path } else { Join-Path $packageRoot 'tools/ocr-sidecar/models/paddlex' }
$env:FORMPOTOK_LAB_RECOGNIZER = $RecognizerModel
if ([bool]$SourceSidecarScript -ne [bool]$SourcePython) { throw 'Specify both SourceSidecarScript and SourcePython.' }
if ($SourceSidecarScript) {
    $sourceScript = (Resolve-Path -LiteralPath $SourceSidecarScript).Path
    $stage = Join-Path $resultRoot 'source-sidecar'
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    Copy-Item -LiteralPath $sourceScript -Destination (Join-Path $stage 'main.py')
    Copy-Item -LiteralPath (Join-Path (Split-Path $sourceScript) 'reading_order.py') -Destination (Join-Path $stage 'reading_order.py')
    $env:FORMPOTOK_LAB_SIDECAR_SOURCE = Join-Path $stage 'main.py'
    $env:FORMPOTOK_LAB_PYTHON = (Resolve-Path -LiteralPath $SourcePython).Path
} else {
    Remove-Item Env:FORMPOTOK_LAB_SIDECAR_SOURCE -ErrorAction SilentlyContinue
    Remove-Item Env:FORMPOTOK_LAB_PYTHON -ErrorAction SilentlyContinue
}
$appExecutable = Join-Path $packageRoot 'FormPotok.Text.exe'
$sidecarExecutable = Join-Path $packageRoot 'tools/ocr-sidecar/runtime/formpotok-ocr-sidecar.exe'
$project = Join-Path $PSScriptRoot '../LocalTextSmoke/LocalTextSmoke.csproj'
$cases = @()
foreach ($entry in $manifest.sources) {
    $source = Join-Path $corpusRoot $entry.file
    $caseRoot = Join-Path $resultRoot ([IO.Path]::GetFileNameWithoutExtension($entry.file))
    $summary = $null
    $exitCode = 1
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $result = "${caseRoot}-attempt-$attempt"
        $arguments = @('run', '--no-build', '--project', $project, '-c', 'Release', '--',
            $packageRoot, $source, $result, $modelRoot, $manifestPath) + $SmokeFlags
        $output = & dotnet @arguments 2>&1
        $exitCode = $LASTEXITCODE
        $summaryPath = Join-Path $result 'smoke-summary.json'
        if ($exitCode -eq 0 -and (Test-Path -LiteralPath $summaryPath)) {
            $summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($summary.passed -and $summary.sourceSha256 -eq $entry.sha256 -and $summary.comparison) { break }
            $summary = $null
        }
    }
    if ($null -eq $summary) {
        $cases += [pscustomobject]@{
            sourceSha256 = $entry.sha256
            sourceGroup = $entry.sourceGroup
            severity = $entry.severity
            status = 'error'
            exitCode = $exitCode
        }
        Write-Output "$($entry.file): ERROR exit=$exitCode"
        continue
    }
    $numeric = $null
    if ($entry.sourceGroup -eq 'numeric-table') {
        $metricScript = Join-Path $PSScriptRoot 'numeric_table_metric.py'
        $metricOutput = & py -3.10 $metricScript $manifestPath $entry.file (Join-Path $result 'reviewed.txt') 2>&1
        if ($LASTEXITCODE -ne 0) {
            $cases += [pscustomobject]@{
                sourceSha256 = $entry.sha256
                sourceGroup = $entry.sourceGroup
                severity = $entry.severity
                status = 'error'
                exitCode = $LASTEXITCODE
            }
            Write-Output "$($entry.file): ERROR numeric metric"
            continue
        }
        $numeric = ($metricOutput | Out-String) | ConvertFrom-Json
    }
    $page = $summary.comparison.perPage[0]
    $cases += [pscustomobject]@{
        sourceSha256 = $entry.sha256
        sourceGroup = $entry.sourceGroup
        severity = $entry.severity
        status = 'ok'
        characterEdits = $page.characterEdits
        referenceCharacters = $page.referenceCharacters
        wordEdits = $page.wordEdits
        referenceWords = $page.referenceWords
        markerPresent = $page.markerPresent
        durationSeconds = $summary.durationSeconds
        modelDigest = $summary.modelDigest
        exactCells = if ($numeric) { $numeric.exactCells } else { $null }
        referenceCells = if ($numeric) { $numeric.referenceCells } else { $null }
        exactRows = if ($numeric) { $numeric.exactRows } else { $null }
        referenceRows = if ($numeric) { $numeric.referenceRows } else { $null }
    }
    Write-Output "$($entry.file): CER=$([Math]::Round($summary.comparison.cer, 4)) WER=$([Math]::Round($summary.comparison.wer, 4))"
}
$completed = @($cases | Where-Object status -eq 'ok')
$failed = $cases.Count - $completed.Count
$modelDigests = @($completed.modelDigest | Sort-Object -Unique)
if ($modelDigests.Count -gt 1) { throw 'OCR model digest changed during the corpus run.' }
$characters = ($completed | Measure-Object -Property referenceCharacters -Sum).Sum
$words = ($completed | Measure-Object -Property referenceWords -Sum).Sum
$numericCompleted = @($completed | Where-Object sourceGroup -eq 'numeric-table')
$numericCells = ($numericCompleted | Measure-Object -Property referenceCells -Sum).Sum
$numericRows = ($numericCompleted | Measure-Object -Property referenceRows -Sum).Sum
$report = [ordered]@{
    scope = 'synthetic-diagnostic-only'
    corpusManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    appExecutableSha256 = (Get-FileHash -LiteralPath $appExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    sidecarExecutableSha256 = if ($SourceSidecarScript) { $null } else { (Get-FileHash -LiteralPath $sidecarExecutable -Algorithm SHA256).Hash.ToLowerInvariant() }
    sidecarSourceSha256 = if ($SourceSidecarScript) { (Get-FileHash -LiteralPath $env:FORMPOTOK_LAB_SIDECAR_SOURCE -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    readingOrderSha256 = if ($SourceSidecarScript) { (Get-FileHash -LiteralPath (Join-Path $stage 'reading_order.py') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    pythonExecutableSha256 = if ($SourcePython) { (Get-FileHash -LiteralPath $env:FORMPOTOK_LAB_PYTHON -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    modelDigest = if ($modelDigests.Count -eq 1) { $modelDigests[0] } else { $null }
    recognizerModel = $RecognizerModel
    smokeFlags = $SmokeFlags
    cases = $cases.Count
    completed = $completed.Count
    failed = $failed
    sourceGroups = @($cases.sourceGroup | Sort-Object -Unique).Count
    characterErrorRate = if ($failed -eq 0 -and $characters -gt 0) { ($completed | Measure-Object -Property characterEdits -Sum).Sum / $characters } else { $null }
    wordErrorRate = if ($failed -eq 0 -and $words -gt 0) { ($completed | Measure-Object -Property wordEdits -Sum).Sum / $words } else { $null }
    markerRecall = if ($failed -eq 0) { @($completed | Where-Object markerPresent).Count / $cases.Count } else { $null }
    exactCellRate = if ($failed -eq 0 -and $numericCells -gt 0) { ($numericCompleted | Measure-Object -Property exactCells -Sum).Sum / $numericCells } else { $null }
    exactRowRate = if ($failed -eq 0 -and $numericRows -gt 0) { ($numericCompleted | Measure-Object -Property exactRows -Sum).Sum / $numericRows } else { $null }
    results = $cases
}
$json = $report | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $resultRoot 'corpus-summary.json'), $json, [Text.UTF8Encoding]::new($false))
Write-Output "Summary: completed=$($report.completed) failed=$($report.failed) CER=$($report.characterErrorRate) WER=$($report.wordErrorRate) marker recall=$($report.markerRecall) exact cell=$($report.exactCellRate) exact row=$($report.exactRowRate)"
if ($failed -ne 0) { exit 1 }
