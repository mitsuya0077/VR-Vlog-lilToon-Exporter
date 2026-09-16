$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskContract = Join-Path $taskRepo 'Editor/HumanoidPoseData.cs'
if (!(Test-Path $taskContract)) { $taskContract = Join-Path $taskRepo 'Assets/Scripts/UniVrmRuntime/HumanoidPoseData.cs' }
Add-Type -Path @($taskContract, (Join-Path $PSScriptRoot 'PoseContractBehaviorTests.cs'))
[PoseContractBehaviorTests]::Run()
