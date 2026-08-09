param(
    [string]$LogPath = 'D:\R.G. Catalyst\MM-Coop-Host\MM_Data\output_log.txt'
)

$text = Get-Content -LiteralPath $LogPath -Raw -ErrorAction Stop
$crashPatterns = @(
    'Collection was modified; enumeration operation may not execute',
    'FullSerializer.fsSerializer.TrySerialize',
    'MotorsportManagerCoop.Main.OnPitCrewAssign',
    '**** Crash! ****',
    '[Manager] Destroying when exit.'
)

$found = @($crashPatterns | Where-Object { $text.Contains($_) })
$saveRequests = ([regex]::Matches($text, 'authoritative_save=requested')).Count

if ($found.Count -gt 0 -or $saveRequests -gt 3) {
    Write-Error "HOST LOAD FAIL: crashPatterns=$($found.Count), authoritativeSaveRequests=$saveRequests"
    exit 1
}

Write-Output "HOST LOAD PASS: no serializer crash, authoritativeSaveRequests=$saveRequests"
