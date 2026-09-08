# publish-sign.ps1 — Build a self-contained signed PlanMeter EXE.
#
# Prerequisites:
#   .NET 8 SDK (dotnet)
#   Windows SDK with signtool.exe at the known path below
#
# Usage:
#   pwsh scripts/publish-sign.ps1
#   pwsh scripts/publish-sign.ps1 -Thumbprint <existing-cert-thumbprint>
#
# D-01..D-07 — self-signed cert (5-year, SHA256, RFC 3161 timestamp),
# locked dotnet publish flags, signtool sign + verify.

param(
    [string]$Thumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$CertSubject = 'CN=PlanMeter Personal'
$CertStore = 'Cert:\CurrentUser\My'
$SignToolPath = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe'
$TimestampUrl = 'http://timestamp.digicert.com'
$ArtifactsDir = './artifacts/'
$ExeName = 'PlanMeter.App.exe'

# ── 1. Resolve or create the code-signing certificate ──

if (-not $Thumbprint) {
    $cert = Get-ChildItem -Path $CertStore -CodeSigningCert |
        Where-Object { $_.Subject -eq $CertSubject } |
        Select-Object -First 1

    if (-not $cert) {
        Write-Host "No existing cert found with Subject '$CertSubject'. Creating a new self-signed code-signing certificate..." -ForegroundColor Yellow
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $CertSubject `
            -CertStoreLocation $CertStore `
            -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(5)
        Write-Host "Created certificate: $($cert.Thumbprint)" -ForegroundColor Green
    }
    else {
        Write-Host "Found existing certificate: $($cert.Thumbprint)" -ForegroundColor Green
    }

    $Thumbprint = $cert.Thumbprint
}
else {
    Write-Host "Using provided thumbprint: $Thumbprint" -ForegroundColor Green
}

# ── 2. Publish (locked flags from research/CLAUDE.md) ──

Write-Host "`nPublishing Release build..." -ForegroundColor Cyan
if (Test-Path $ArtifactsDir) {
    Remove-Item -Recurse -Force $ArtifactsDir
}
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

dotnet publish src/PlanMeter.App/PlanMeter.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $ArtifactsDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit 1
}

$exePath = Join-Path $ArtifactsDir $ExeName
if (-not (Test-Path $exePath)) {
    Write-Error "Published EXE not found at $exePath"
    exit 1
}

Write-Host "Published: $exePath" -ForegroundColor Green

# ── 3. Sign ──

if (-not (Test-Path $SignToolPath)) {
    Write-Error "signtool.exe not found at $SignToolPath. Install Windows SDK 10.0.22621.0."
    exit 1
}

Write-Host "`nSigning with thumbprint $Thumbprint..." -ForegroundColor Cyan
& $SignToolPath sign `
    /sha1 $Thumbprint `
    /tr $TimestampUrl `
    /td sha256 `
    /fd sha256 `
    $exePath

if ($LASTEXITCODE -ne 0) {
    Write-Error "signtool sign failed with exit code $LASTEXITCODE"
    exit 1
}

Write-Host "Signed successfully." -ForegroundColor Green

# ── 4. Verify ──

Write-Host "`nVerifying signature..." -ForegroundColor Cyan
& $SignToolPath verify /pa $exePath

if ($LASTEXITCODE -ne 0) {
    Write-Error "signtool verify failed with exit code $LASTEXITCODE"
    exit 1
}

Write-Host "Signature verified.`n" -ForegroundColor Green

# ── 5. Write thumbprint for traceability ──

$thumbprintPath = Join-Path $ArtifactsDir 'sign-thumbprint.txt'
Set-Content -Path $thumbprintPath -Value $Thumbprint
Write-Host "Thumbprint written to $thumbprint"

Write-Host "`nDone. Signed EXE: $exePath" -ForegroundColor Green
