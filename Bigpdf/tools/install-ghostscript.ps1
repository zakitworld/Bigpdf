# Install Ghostscript via winget and set user GHOSTSCRIPT_PATH and update PATH
# Run in an elevated PowerShell session.

Write-Host "Installing Ghostscript via winget..."
winget install --id Artifex.Ghostscript -e --accept-package-agreements --accept-source-agreements

# Ghostscript typically installs to C:\Program Files\gs\gs<version>\bin
$gsRoot = "C:\Program Files\gs"
if (-Not (Test-Path $gsRoot)) {
	Write-Warning "Expected install root $gsRoot not found. Please verify Ghostscript installed and adjust the path."
} else {
	# find latest version folder
	$versions = Get-ChildItem -Path $gsRoot -Directory | Sort-Object Name -Descending
	if ($versions.Length -eq 0) {
		Write-Warning "No Ghostscript versions found under $gsRoot"
	}
	else {
		$binPath = Join-Path $versions[0].FullName "bin"
		$gsExe = Join-Path $binPath "gswin64c.exe"
		if (-Not (Test-Path $gsExe)) {
			$gsExe = Join-Path $binPath "gs.exe"
		}

		if (Test-Path $gsExe) {
			Write-Host "Setting user environment variable GHOSTSCRIPT_PATH to $gsExe"
			setx GHOSTSCRIPT_PATH "$gsExe"

			# Add to user PATH if not present
			$currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
			if ($currentPath -notlike "*$binPath*") {
				Write-Host "Adding $binPath to user PATH"
				[Environment]::SetEnvironmentVariable("Path", "$currentPath;$binPath", "User")
			} else {
				Write-Host "$binPath already in user PATH"
			}

			Write-Host "Ghostscript installed and environment variables set. Restart Visual Studio or your shell to pick up changes."
		}
		else {
			Write-Warning "Ghostscript executable not found in $binPath"
		}
	}
}
