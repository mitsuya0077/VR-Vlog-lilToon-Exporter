$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Run with PowerShell 7 (pwsh). This runner compiles production C# using its bundled compiler.'
}
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskSources = @(
    (Join-Path $taskRepo 'Editor/LilToonMainTextureBaker.cs'),
    (Join-Path $taskRepo 'Editor/MaterialBakeIssue.cs'),
    (Join-Path $taskRepo 'Editor/ExportRendererSelection.cs'),
    (Join-Path $PSScriptRoot 'BakeTestShim.cs'),
    (Join-Path $PSScriptRoot 'BakeBehaviorTests.cs')
)
Add-Type -Path $taskSources -CompilerOptions '/define:EXPORTER_BAKE_TESTS'
[ExporterBakeBehaviorTests]::Run()
