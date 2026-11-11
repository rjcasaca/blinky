param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $projectRoot "artifacts/Blinky-win-x64"
dotnet publish (Join-Path $projectRoot "src/Blinky.App/Blinky.App.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o $output
if ($LASTEXITCODE -ne 0) { throw "Publishing Blinky failed." }
dotnet publish (Join-Path $projectRoot "src/Blinky.Mcp/Blinky.Mcp.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o (Join-Path $output "mcp")
if ($LASTEXITCODE -ne 0) { throw "Publishing Blinky MCP failed." }
Copy-Item (Join-Path $projectRoot "README.md") $output
Copy-Item (Join-Path $projectRoot "WINDOWS-VALIDATION.md") $output
Copy-Item (Join-Path $projectRoot "MCP-SETUP.md") $output
Copy-Item (Join-Path $projectRoot "THIRD-PARTY-NOTICES.md") $output
foreach ($notice in @("LICENSE", "NOTICE", "LEGAL.md", "PRIVACY.md", "SECURITY.md", "DEPENDENCIES.md")) {
    Copy-Item (Join-Path $projectRoot $notice) $output
}
Copy-Item (Join-Path $projectRoot "scripts/Start-Blinky.cmd") $output
Copy-Item (Join-Path $projectRoot "scripts/Start-Blinky.ps1") $output
Copy-Item (Join-Path $projectRoot "licenses") $output -Recurse -Force
Get-ChildItem $output -Recurse -Filter "*.pdb" | Remove-Item
# Keep the native runtime prerequisite next to the app so the portable folder
# can be taken offline after downloading. Installation may ask for elevation.
Invoke-WebRequest "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile (Join-Path $output "vc_redist.x64.exe")
$signature = Get-AuthenticodeSignature (Join-Path $output "vc_redist.x64.exe")
if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
    throw "Visual C++ prerequisite did not have a valid Microsoft signature."
}
Compress-Archive -Path "$output/*" -DestinationPath (Join-Path $projectRoot "artifacts/Blinky-win-x64.zip") -Force
Write-Host "Ready: artifacts/Blinky-win-x64.zip"
