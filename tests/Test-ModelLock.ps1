$ErrorActionPreference = 'Stop'
$verify = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../scripts/verify-model-lock.ps1'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('formpotok-model-lock-' + [guid]::NewGuid().ToString('N'))
$names = @('PP-OCRv5_mobile_det', 'eslav_PP-OCRv5_mobile_rec', 'PP-LCNet_x1_0_doc_ori', 'UVDoc', 'PP-LCNet_x1_0_textline_ori')
$revision = '0123456789abcdef0123456789abcdef01234567'

function Assert-Rejected([string] $Label) {
    try { & $verify -ModelRoot $scratch -LockPath (Join-Path $scratch 'lock.json') | Out-Null }
    catch { Write-Output "PASS: rejects $Label"; return }
    throw "Model lock unexpectedly accepted: $Label"
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $models = @()
    foreach ($name in $names) {
        $dir = Join-Path $scratch $name
        $cache = Join-Path $dir '.cache/huggingface/download'
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        $weights = Join-Path $dir 'inference.pdiparams'
        [IO.File]::WriteAllBytes($weights, [Text.Encoding]::UTF8.GetBytes("weight-$name"))
        $hash = (Get-FileHash -LiteralPath $weights -Algorithm SHA256).Hash.ToLowerInvariant()
        @{ Global = @{ model_name = $name } } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $dir 'config.json')
        Set-Content -LiteralPath (Join-Path $dir 'README.md') -Value "license: apache-2.0"
        Set-Content -LiteralPath (Join-Path $dir 'inference.json') -Value '{}'
        Set-Content -LiteralPath (Join-Path $dir 'inference.yml') -Value 'test: true'
        Set-Content -LiteralPath (Join-Path $cache 'inference.pdiparams.metadata') -Value @($revision, $hash)
        $models += @{ name = $name; revision = $revision; license = 'apache-2.0'; weightsSha256 = $hash; weightsBytes = (Get-Item $weights).Length }
    }
    $lock = @{ schemaVersion = 1; models = $models }
    $lock | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'lock.json')
    $result = & $verify -ModelRoot $scratch -LockPath (Join-Path $scratch 'lock.json') | ConvertFrom-Json
    if (-not $result.passed -or $result.modelCount -ne 5) { throw 'Valid model lock was rejected.' }
    Write-Output 'PASS: accepts five matching models'

    $first = Join-Path $scratch "$($names[0])/inference.pdiparams"
    [IO.File]::WriteAllBytes($first, [Text.Encoding]::UTF8.GetBytes('changed'))
    Assert-Rejected 'changed weights'
    [IO.File]::WriteAllBytes($first, [Text.Encoding]::UTF8.GetBytes("weight-$($names[0])"))

    $meta = Join-Path $scratch "$($names[0])/.cache/huggingface/download/inference.pdiparams.metadata"
    Set-Content -LiteralPath $meta -Value @('ffffffffffffffffffffffffffffffffffffffff', $models[0].weightsSha256)
    Assert-Rejected 'changed source revision'
    Set-Content -LiteralPath $meta -Value @($revision, $models[0].weightsSha256)

    $card = Join-Path $scratch "$($names[0])/README.md"
    Set-Content -LiteralPath $card -Value 'license: unknown'
    Assert-Rejected 'changed license declaration'
    Set-Content -LiteralPath $card -Value 'license: apache-2.0'

    $lock.models[1].name = $names[0]
    $lock | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'lock.json')
    Assert-Rejected 'duplicate model entry'
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^formpotok-model-lock-[0-9a-f]{32}$') {
        throw "Refusing unsafe test cleanup path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
