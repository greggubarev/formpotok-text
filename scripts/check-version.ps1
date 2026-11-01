param(
    [string] $Version,
    [string] $Repository = (Join-Path $PSScriptRoot '..'),
    [switch] $RequireTag
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($Repository)
if (-not $PSBoundParameters.ContainsKey('Version')) {
    $Version = (Get-Content -Raw -LiteralPath (Join-Path $repo 'VERSION')).Trim()
}

$pattern = '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<channel>preview|rc)\.(?<sequence>[1-9][0-9]*))?$'
$match = [regex]::Match($Version, $pattern)
if (-not $match.Success) {
    throw "Invalid product version '$Version'. Use MAJOR.MINOR.PATCH[-preview.N|-rc.N]."
}
foreach ($component in @('major', 'minor', 'patch')) {
    $number = 0
    if (-not [int]::TryParse($match.Groups[$component].Value, [ref]$number) -or $number -gt 65535) {
        throw "Version component '$component' must fit the Windows file version."
    }
}

if ($RequireTag) {
    $dirty = (& git -C $repo status --porcelain=v1 --untracked-files=all | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $dirty.Length -gt 0) { throw 'A publication tag requires a clean source tree.' }
    $tag = "v$Version"
    $tagType = (& git -C $repo cat-file -t "refs/tags/$tag" 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $tagType -ne 'tag') { throw "Annotated tag '$tag' is required for publication." }
    $tagCommit = (& git -C $repo rev-parse "refs/tags/$tag^{commit}" | Out-String).Trim()
    $head = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    if ($tagCommit -ne $head) { throw "Tag '$tag' does not point to HEAD." }
}

$Version
