param(
    [Parameter(Mandatory = $true)][string] $ModelRoot,
    [string] $LockPath
)

$ErrorActionPreference = 'Stop'
if (-not $LockPath) { $LockPath = Join-Path $PSScriptRoot '../packaging/model-lock.json' }
$root = [IO.Path]::GetFullPath($ModelRoot)
$lockFile = [IO.Path]::GetFullPath($LockPath)
$lock = Get-Content -Raw -LiteralPath $lockFile | ConvertFrom-Json
$expected = @('PP-OCRv5_mobile_det', 'eslav_PP-OCRv5_mobile_rec', 'PP-LCNet_x1_0_doc_ori', 'UVDoc', 'PP-LCNet_x1_0_textline_ori')
if ($lock.schemaVersion -ne 1 -or @($lock.models).Count -ne $expected.Count) { throw 'Invalid model lock schema or count.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($model in $lock.models) {
    $name = [string]$model.name
    if ($name -notin $expected -or -not $seen.Add($name) -or $model.revision -notmatch '^[0-9a-f]{40}$' -or
        $model.license -ne 'apache-2.0' -or $model.weightsSha256 -notmatch '^[0-9a-f]{64}$' -or
        [long]$model.weightsBytes -le 0) { throw "Invalid model lock entry: $name" }
    $dir = Join-Path $root $name
    $weights = Join-Path $dir 'inference.pdiparams'
    $config = Join-Path $dir 'config.json'
    $readme = Join-Path $dir 'README.md'
    $metadata = Join-Path $dir '.cache/huggingface/download/inference.pdiparams.metadata'
    foreach ($file in @($weights, $config, $readme, $metadata, (Join-Path $dir 'inference.json'),
        (Join-Path $dir 'inference.yml'))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Model file missing: $name" }
    }
    $identity = Get-Content -Raw -LiteralPath $config | ConvertFrom-Json
    $lines = @(Get-Content -LiteralPath $metadata -TotalCount 2)
    $licenseLine = Select-String -LiteralPath $readme -Pattern '^license: apache-2\.0$' | Select-Object -First 1
    if ($identity.Global.model_name -ne $name -or $lines.Count -ne 2 -or $lines[0] -ne $model.revision -or
        $lines[1] -ne $model.weightsSha256 -or $null -eq $licenseLine) {
        throw "Model identity or origin differs from lock: $name"
    }
    $file = Get-Item -LiteralPath $weights
    if ($file.Length -ne [long]$model.weightsBytes -or
        (Get-FileHash -LiteralPath $weights -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.weightsSha256) {
        throw "Model weight bytes differ from lock: $name"
    }
}
if ($seen.Count -ne $expected.Count) { throw 'Model lock is incomplete.' }
[pscustomobject]@{ passed = $true; modelCount = $seen.Count; lockSha256 = (Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json
