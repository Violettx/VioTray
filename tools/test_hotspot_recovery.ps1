# Hotspot recovery test: stop the mobile hotspot, wait for VioTray to bring it back.
# Requires VioTray to be running.

[Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking, ContentType=WindowsRuntime] | Out-Null
[Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime] | Out-Null

function Get-TetheringManager {
    $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
    if (-not $profile) { return $null }
    return [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
}

$mgr = Get-TetheringManager
if (-not $mgr) {
    Write-Output "no internet connection profile, abort"
    exit 1
}

Write-Output ("[0s]   before stop : state=" + $mgr.TetheringOperationalState + " clients=" + $mgr.ClientCount)

$null = $mgr.StopTetheringAsync()
Start-Sleep -Seconds 3

$mgr = Get-TetheringManager
Write-Output ("[3s]   after stop  : state=" + $mgr.TetheringOperationalState)

Write-Output "       waiting for VioTray to recover (up to 30s) ..."
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 3
    $mgr = Get-TetheringManager
    $state = $mgr.TetheringOperationalState
    $elapsed = 3 + (($i + 1) * 3)
    Write-Output ("[" + $elapsed + "s]   state=" + $state + " clients=" + $mgr.ClientCount)
    if ($state.ToString() -eq 'On') {
        Write-Output ""
        Write-Output ("RESULT: hotspot recovered after about " + $elapsed + "s")
        exit 0
    }
}

Write-Output ""
Write-Output "RESULT: not recovered within 30s, check VioTray log"
exit 2
