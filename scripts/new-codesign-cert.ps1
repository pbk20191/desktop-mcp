# Creates the self-signed code-signing certificate the release workflow signs with (.github/workflows/release.yml):
# CN "desktop-mcp (pbk20191)", RSA 3072, SHA-256, valid 10 years, key usage digital signature + certificate signing
# (so that OpenSSL accepts it as its own issuer), extended key usage code signing.
#
# Writes to the output directory (default: the current one):
#   desktop-mcp-codesign.p12      the certificate and private key, password-protected (keep it private)
#   desktop-mcp-codesign.p12.txt  the same, base64: the CODESIGN_P12_BASE64 secret
#   desktop-mcp-codesign.cer      the public certificate, for users who want to trust it
# The password becomes the CODESIGN_P12_PASSWORD secret. The .p12 uses 3DES/SHA-1 so that macOS `security import`
# and OpenSSL 3 both read it. The certificate is removed from Cert:\CurrentUser\My afterwards; the .p12 is the copy.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\new-codesign-cert.ps1 [-OutDir dir]   (asks for the password)
#        or from PowerShell: & scripts\new-codesign-cert.ps1 -OutDir dir -Password $secureString
param([string]$OutDir = '.', [SecureString]$Password)
$ErrorActionPreference = 'Stop'
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
New-Item -ItemType Directory -Force $OutDir | Out-Null
$p12 = Join-Path $OutDir 'desktop-mcp-codesign.p12'
if (Test-Path $p12) { throw "$p12 exists already; move it away first." }
$password = if ($Password) { $Password } else { Read-Host -AsSecureString 'Password for the .p12' }

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=desktop-mcp (pbk20191)' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeySpec Signature -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature, CertSign -NotAfter (Get-Date).AddYears(10) -CertStoreLocation Cert:\CurrentUser\My
try {
    Export-PfxCertificate -Cert $cert -FilePath $p12 -Password $password -CryptoAlgorithmOption TripleDES_SHA1 | Out-Null
    Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'desktop-mcp-codesign.cer') | Out-Null
    [IO.File]::WriteAllText("$p12.txt", [Convert]::ToBase64String([IO.File]::ReadAllBytes($p12)))
} finally {
    Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey
}
Write-Output "Wrote $p12, $p12.txt and the .cer beside them (thumbprint $($cert.Thumbprint), valid to $($cert.NotAfter.ToString('yyyy-MM-dd')))."
Write-Output "Add the secrets with: Get-Content -Raw '$p12.txt' | gh secret set CODESIGN_P12_BASE64; gh secret set CODESIGN_P12_PASSWORD"
