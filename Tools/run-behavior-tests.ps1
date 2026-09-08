param([string]$LocalVrm = '')
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskSources = @(
    (Join-Path $taskRepo 'Editor/JsonDom.cs'),
    (Join-Path $taskRepo 'Editor/GlbDocument.cs'),
    (Join-Path $taskRepo 'Editor/ExportSkinRoots.cs'),
    (Join-Path $taskRepo 'Editor/TextureResizePolicy.cs'),
    (Join-Path $taskRepo 'Editor/MobileTextureEncoder.cs'),
    (Join-Path $taskRepo 'Editor/GlbTextureDownsizer.cs'),
    (Join-Path $taskRepo 'Editor/VrmExpressionBindings.cs'),
    (Join-Path $taskRepo 'Editor/VrmMenuExpressions.cs'),
    (Join-Path $taskRepo 'Editor/VrChatExpressionMenu.cs'),
    (Join-Path $taskRepo 'Editor/VrChatFixedExpressionCurve.cs'),
    (Join-Path $taskRepo 'Editor/ExpressionAnimationData.cs'),
    (Join-Path $taskRepo 'Editor/LilToonEmissionPolicy.cs'),
    (Join-Path $taskRepo 'Editor/LilToonMaterialReader.cs'),
    (Join-Path $taskRepo 'Editor/LilToonLightingProfile.cs'),
    (Join-Path $taskRepo 'Editor/LilToonExtensionModel.cs'),
    (Join-Path $taskRepo 'Editor/LilToonMobileProfile.cs'),
    (Join-Path $taskRepo 'Editor/ExportRendererSelection.cs'),
    (Join-Path $taskRepo 'Editor/AvatarBaseShape.cs'),
    (Join-Path $taskRepo 'Editor/MobileMaterialMath.cs'),
    (Join-Path $taskRepo 'Editor/LilToonGlbExtension.cs'),
    (Join-Path $taskRepo 'Editor/LilToonExtensionValidator.cs'),
    (Join-Path $taskRepo 'Tests/Editor/Fixtures/MaterialBindingFixture.cs'),
    (Join-Path $taskRepo 'Tests/Editor/Fixtures/BaseShapeFixture.cs'),
    (Join-Path $taskRepo 'Tests/Editor/Fixtures/SkinRootFixture.cs'),
    (Join-Path $taskRepo 'Tests/Editor/Fixtures/MenuExpressionFixture.cs'),
    (Join-Path $taskRepo 'Tests/Editor/Fixtures/MenuTraversalFixture.cs'),
    (Join-Path $taskRepo 'Tests/Editor/Fixtures/FixedExpressionCurveFixture.cs'),
    (Join-Path $taskRepo 'Tests/Editor/Fixtures/AnimatedExpressionFixture.cs'),
    (Join-Path $PSScriptRoot 'MeshTestShim.cs'),
    (Join-Path $PSScriptRoot 'MaterialTestShim.cs'),
    (Join-Path $PSScriptRoot 'LightingBehaviorTests.cs'),
    (Join-Path $PSScriptRoot 'TextureResizeBehaviorTests.cs'),
    (Join-Path $PSScriptRoot 'SkinRootBehaviorTests.cs'),
    (Join-Path $PSScriptRoot 'BehaviorTests.cs')
)
Add-Type -Path $taskSources -CompilerOptions '/define:EXPORTER_BEHAVIOR_TESTS'
[ExporterBehaviorTests]::Run()
[ExporterLightingBehaviorTests]::Run()
[ExporterTextureResizeBehaviorTests]::Run()
[ExporterSkinRootBehaviorTests]::Run()
if ($LocalVrm) { [ExporterBehaviorTests]::VerifyLocalVrm($LocalVrm) }
