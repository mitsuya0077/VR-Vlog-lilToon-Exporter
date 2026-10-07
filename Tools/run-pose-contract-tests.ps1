$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskContract = Join-Path $taskRepo 'Editor/HumanoidPoseData.cs'
if (!(Test-Path $taskContract)) { $taskContract = Join-Path $taskRepo 'Assets/Scripts/UniVrmRuntime/HumanoidPoseData.cs' }
$taskAnimationContract = Join-Path (Split-Path -Parent $taskContract) 'HumanoidAnimationData.cs'
Add-Type -Path @($taskContract, $taskAnimationContract, (Join-Path $PSScriptRoot 'PoseContractBehaviorTests.cs'))
[PoseContractBehaviorTests]::Run()
