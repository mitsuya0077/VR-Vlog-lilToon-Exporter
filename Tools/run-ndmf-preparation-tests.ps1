$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskSources = @(
    (Join-Path $taskRepo 'Editor/Compatibility/DependencyPolicy.cs'),
    (Join-Path $taskRepo 'Editor/Compatibility/DependencyPolicy.Generated.cs'),
    (Join-Path $taskRepo 'Editor/NdmfExportPreparation.cs'),
    (Join-Path $taskRepo 'Tests/Editor/NdmfPreparationTests.cs'),
    (Join-Path $PSScriptRoot 'NdmfPreparationTestShim.cs')
)
Add-Type -Path $taskSources -CompilerOptions '/define:NDMF_PREPARATION_BEHAVIOR_TESTS', '/nowarn:0414,0649'
[NdmfPreparationHostTests]::Run()
