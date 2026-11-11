$ErrorActionPreference = "Stop"
try {
    $runtime = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue
    if (!$runtime -or $runtime.Installed -ne 1 -or [version]$runtime.Version.TrimStart('v') -lt [version]"14.44") {
        $installer = Join-Path $PSScriptRoot "vc_redist.x64.exe"
        if (!(Test-Path $installer)) { throw "Install the Microsoft Visual C++ x64 runtime from https://aka.ms/vs/17/release/vc_redist.x64.exe first." }
        $signature = Get-AuthenticodeSignature $installer
        if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
            throw "The prerequisite installer does not have a valid Microsoft signature."
        }
        Write-Host "Installing the required Microsoft Visual C++ runtime. Follow the installer prompts."
        $process = Start-Process -FilePath $installer -ArgumentList "/install", "/passive", "/norestart" -Wait -PassThru
        if ($process.ExitCode -eq 3010) { Write-Host "Restart Windows, then launch Blinky again."; exit 1 }
        if ($process.ExitCode -notin 0, 1638) { throw "Runtime installation failed ($($process.ExitCode))." }
    }
    Start-Process -FilePath (Join-Path $PSScriptRoot "Blinky.exe") -WorkingDirectory $PSScriptRoot
} catch { Write-Error $_; exit 1 }
