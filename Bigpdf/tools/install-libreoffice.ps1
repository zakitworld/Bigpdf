# Install LibreOffice via winget and set user LIBREOFFICE_PATH and update PATH
# Run in an elevated PowerShell session.

Write-Host "Installing LibreOffice via winget..."
winget install --id TheDocumentFoundation.LibreOffice -e --accept-package-agreements --accept-source-agreements

$installPath = "C:\Program Files\LibreOffice\program"
if (-Not (Test-Path $installPath)) {
	Write-Warning "Expected install path $installPath not found. Please verify LibreOffice installed and adjust the path."
} else {
	$soffice = Join-Path $installPath "soffice.exe"
	if (Test-Path $soffice) {
		Write-Host "Setting user environment variable LIBREOFFICE_PATH to $soffice"
		setx LIBREOFFICE_PATH "$soffice"

		# Add to user PATH if not present
		$currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
		if ($currentPath -notlike "*${installPath}*") {
			Write-Host "Adding $installPath to user PATH"
			[Environment]::SetEnvironmentVariable("Path", "$currentPath;$installPath", "User")
		} else {
			Write-Host "$installPath already in user PATH"
		}

		Write-Host "LibreOffice installed and environment variables set. Restart Visual Studio or your shell to pick up changes."
	}
}
