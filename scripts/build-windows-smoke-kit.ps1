param([string] $OutputRoot = 'output/windows-smoke-kit')

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be inside the repository.'
}
if (Test-Path -LiteralPath $output) { throw "Smoke kit already exists: $output" }
$archive = "$output.zip"
if (Test-Path -LiteralPath $archive) { throw "Smoke kit archive already exists: $archive" }
New-Item -ItemType Directory -Path (Join-Path $output 'scripts') -Force | Out-Null
& dotnet publish (Join-Path $repo 'tools/LocalTextSmoke/LocalTextSmoke.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $output 'runner')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $output 'runner/LocalTextSmoke.exe'))) {
    throw 'Self-contained smoke runner build failed.'
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-windows-artifacts.ps1') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-windows-portable.ps1') -Destination (Join-Path $output 'scripts')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'check-version.ps1') -Destination (Join-Path $output 'scripts')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-model-lock.ps1') -Destination (Join-Path $output 'scripts')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-runtime-dependency-lock.ps1') -Destination (Join-Path $output 'scripts')
Copy-Item -LiteralPath (Join-Path $repo 'examples/sample-letter.reference.json') -Destination $output
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive
Write-Output $archive
