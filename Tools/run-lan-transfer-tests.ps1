#Requires -Version 7.0
param(
    [ValidateSet('Default', 'EditorOnly', 'DevelopmentOnly', 'DevelopmentEditor')]
    [string]$CloudAvailabilityMode = 'Default'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dependencyRoot = Join-Path $repoRoot 'Editor/LanTransfer/Dependencies'
$dependencies = @(
    (Join-Path $dependencyRoot 'BouncyCastle.Cryptography.dll'),
    (Join-Path $dependencyRoot 'zxing.dll')
)
$hashes = @(
    'd61c1f2ba929a230a58e101ccd850e21f2675fa6b9814ec279633e8a089c3495',
    'f3b823b6fd6492525a7547989056883def5d43be1e12c4f63fa54df73e3c5cfc'
)
for ($i = 0; $i -lt $dependencies.Count; $i++) {
    if ((Get-FileHash -LiteralPath $dependencies[$i] -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hashes[$i]) {
        throw 'The fixed LAN transfer dependency hash does not match.'
    }
    [System.Reflection.Assembly]::LoadFrom($dependencies[$i]) | Out-Null
}
$referenceRoot = Join-Path $PSHOME 'ref'
if (-not (Test-Path -LiteralPath $referenceRoot -PathType Container)) {
    throw 'PowerShell 7 reference assemblies are required for the LAN transfer tests.'
}
$references = @((Get-ChildItem -LiteralPath $referenceRoot -Filter '*.dll').FullName) + $dependencies
$sources = @(
    (Join-Path $repoRoot 'Editor/LanTransfer/LanTransferProtocol.cs'),
    (Join-Path $repoRoot 'Editor/LanTransfer/LanVrmTransferServer.cs'),
    (Join-Path $repoRoot 'Editor/LanTransfer/CloudTransferProtocol.cs'),
    (Join-Path $repoRoot 'Editor/LanTransfer/CloudTransferAvailability.cs'),
    (Join-Path $repoRoot 'Editor/LanTransfer/CloudDevelopmentSnapshot.cs'),
    (Join-Path $repoRoot 'Editor/LanTransfer/CloudTransferEncryption.cs'),
    (Join-Path $repoRoot 'Editor/LanTransfer/CloudVrmTransferSession.cs'),
    (Join-Path $repoRoot 'Tests/LanTransfer/LanTransferTests.cs'),
    (Join-Path $repoRoot 'Tests/LanTransfer/CloudTransferTests.cs'),
    (Join-Path $PSScriptRoot 'LanTransferTestHarness.cs')
)
$defines = 'VRVLOG_LAN_TRANSFER_CLI'
switch ($CloudAvailabilityMode) {
    'EditorOnly' { $defines += ',UNITY_EDITOR' }
    'DevelopmentOnly' { $defines += ',VRVLOG_CLOUD_TRANSFER_DEVELOPMENT' }
    'DevelopmentEditor' { $defines += ',UNITY_EDITOR,VRVLOG_CLOUD_TRANSFER_DEVELOPMENT' }
}
Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions ("/define:" + $defines)
if (-not $IsWindows) { Write-Host 'Windows snapshot sharing exclusion is covered by Windows validation; this host runs the managed TLS checks.' }
$failures = [VRVlog.LilToonExporter.LanTransfer.Tests.LanTransferCliRunner]::Run()
if ($failures -ne 0) { throw 'LAN transfer tests failed. Console intentionally withholds request and QR values.' }
$cloudFailures = [VRVlog.LilToonExporter.LanTransfer.Tests.CloudTransferCliRunner]::Run()
if ($cloudFailures -ne 0) { throw 'Cloud transfer tests failed. Private request and QR values are withheld.' }
