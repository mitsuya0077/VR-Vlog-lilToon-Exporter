$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
Add-Type -Path @(
    (Join-Path $taskRepo 'Editor/Compatibility/DependencyPolicy.cs'),
    (Join-Path $taskRepo 'Editor/Compatibility/DependencyPolicy.Generated.cs'),
    (Join-Path $PSScriptRoot 'DependencyCompatibilityTests.cs')
)
[DependencyCompatibilityTests]::Run()
