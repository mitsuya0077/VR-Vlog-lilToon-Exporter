param([string]$LocalVrm = '')
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskSources = @(
    (Join-Path $taskRepo 'Editor/JsonDom.cs'),
    (Join-Path $taskRepo 'Editor/GlbDocument.cs'),
    (Join-Path $taskRepo 'Editor/VrmExpressionBindings.cs'),
    (Join-Path $taskRepo 'Editor/LilToonEmissionPolicy.cs'),
    (Join-Path $taskRepo 'Editor/LilToonMaterialReader.cs'),
    (Join-Path $taskRepo 'Editor/LilToonExtensionModel.cs'),
    (Join-Path $taskRepo 'Editor/LilToonMobileProfile.cs'),
    (Join-Path $taskRepo 'Editor/ExportRendererSelection.cs'),
    (Join-Path $taskRepo 'Editor/LilToonGlbExtension.cs'),
    (Join-Path $taskRepo 'Editor/LilToonExtensionValidator.cs'),
    (Join-Path $taskRepo 'Tests/Editor/Fixtures/MaterialBindingFixture.cs'),
    (Join-Path $PSScriptRoot 'MaterialTestShim.cs'),
    (Join-Path $PSScriptRoot 'BehaviorTests.cs')
)
Add-Type -Path $taskSources -CompilerOptions '/define:EXPORTER_BEHAVIOR_TESTS'
[ExporterBehaviorTests]::Run()
if ($LocalVrm) { [ExporterBehaviorTests]::VerifyLocalVrm($LocalVrm) }
