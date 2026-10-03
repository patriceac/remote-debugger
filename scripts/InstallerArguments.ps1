function Get-InstallerArguments {
    param([switch]$Unattended)
    if ($Unattended) {
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/LOG'
    }
}
