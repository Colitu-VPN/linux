param(
    # Colitu for Linux version of the packages, e.g. 1.0.0.
    [Parameter(Mandatory = $true)][string]$Version,
    # The amd64 .deb and the x86_64 .rpm of the GitHub release (scripts/package-linux.sh, release-linux.yml).
    [Parameter(Mandatory = $true)][string]$DebPath,
    [Parameter(Mandatory = $true)][string]$RpmPath,
    # ECDSA P-256 private key (PKCS#8 PEM) that signs latest.json: the same release key as the
    # Windows manifest. Keep it off the repository.
    [string]$SigningKeyPath = $(if ($env:COLITU_UPDATE_SIGNING_KEY) { $env:COLITU_UPDATE_SIGNING_KEY } else { Join-Path $PSScriptRoot "..\..\_gizli_anahtarlar\colitu-windows--update-signing-private.pem" }),
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\release-linux"),
    [switch]$ForceUpdate,
    [string]$ReleaseNotes = ""
)

# Writes downloads/linux/latest.json for the in-app updater (ColituUpdateService). Upload the two
# packages under their stable names and this file to the website's downloads/linux/ folder.
$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Run this script with PowerShell 7 (pwsh): signing the update manifest needs .NET's PEM support."
}
foreach ($path in @($DebPath, $RpmPath, $SigningKeyPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Not found: $path" }
}
# Minor and patch stay single digits: the version code is major*100 + minor*10 + patch,
# so 1.10.0 would get the same code as 2.0.0.
if ($Version -notmatch '^\d+\.\d\.\d$') { throw "Version must look like 1.2.3 (one-digit minor and patch)" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$debName = "colitu-vpn_${Version}_amd64.deb"
$rpmName = "colitu-vpn-${Version}-1.x86_64.rpm"
Copy-Item -LiteralPath $DebPath -Destination (Join-Path $OutputDir $debName) -Force
Copy-Item -LiteralPath $RpmPath -Destination (Join-Path $OutputDir $rpmName) -Force

$debUrl = "https://colitu.com/downloads/linux/$debName"
$rpmUrl = "https://colitu.com/downloads/linux/$rpmName"
$debSha = (Get-FileHash -LiteralPath $DebPath -Algorithm SHA256).Hash.ToLowerInvariant()
$rpmSha = (Get-FileHash -LiteralPath $RpmPath -Algorithm SHA256).Hash.ToLowerInvariant()
$parts = $Version.Split('.') | ForEach-Object { [int]$_ }
$versionCode = ($parts[0] * 100) + ($parts[1] * 10) + $parts[2]
$force = [bool]$ForceUpdate
# Freshness window (signed): the app rejects a manifest issued more than 30 days ago or already expired.
$issuedAt = [DateTime]::UtcNow
$issuedAtText = $issuedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)
$expiresAtText = $issuedAt.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)

# Same text as ColituUpdateSignature.Message in the Linux app; any change there must be mirrored here.
$signedText = @(
    "colitu-linux-update-v1",
    $versionCode.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    $Version,
    $debUrl,
    $debSha,
    $rpmUrl,
    $rpmSha,
    $(if ($force) { "true" } else { "false" })
) -join "`n"
# signature_v2: the v1 text above (unchanged, so clients up to 1.4.3 keep verifying "signature")
# plus the freshness window. The current client checks only signature_v2.
$signedTextV2 = @($signedText, $issuedAtText, $expiresAtText) -join "`n"

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem((Get-Content -LiteralPath $SigningKeyPath -Raw))
    $signature = [Convert]::ToBase64String($key.SignData([System.Text.Encoding]::UTF8.GetBytes($signedText), [System.Security.Cryptography.HashAlgorithmName]::SHA256))
    $signatureV2 = [Convert]::ToBase64String($key.SignData([System.Text.Encoding]::UTF8.GetBytes($signedTextV2), [System.Security.Cryptography.HashAlgorithmName]::SHA256))
}
finally {
    $key.Dispose()
}

$manifest = [ordered]@{
    latestVersionCode = $versionCode
    versionName = $Version
    forceUpdate = $force
    releaseNotes = $ReleaseNotes
    deb = [ordered]@{ url = $debUrl; sha256 = $debSha }
    rpm = [ordered]@{ url = $rpmUrl; sha256 = $rpmSha }
    issued_at = $issuedAtText
    expires_at = $expiresAtText
    signature = $signature
    signature_v2 = $signatureV2
}
$manifestPath = Join-Path $OutputDir "latest.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding utf8NoBOM

Write-Host "Manifest: $manifestPath"
Write-Host "Packages: $debName, $rpmName"
Write-Host "Check it with: `$env:COLITU_VERIFY_MANIFEST='$manifestPath'; dotnet test --project src/ColituVPN.Tests"
