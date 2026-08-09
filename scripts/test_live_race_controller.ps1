param([int]$Port = 27153, [int]$VehicleId = 10)
$ErrorActionPreference = 'Stop'

$tcp = [Net.Sockets.TcpClient]::new()
$tcp.Connect('127.0.0.1', $Port)
$stream = $tcp.GetStream()
$stream.ReadTimeout = 6000
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
$writer.WriteLine('{"type":"hello","protocol":0,"name":"final-functional-test"}')

function Read-State([string]$label) {
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline) {
        try {
            $line = $reader.ReadLine()
            if ($line -match '"type":"telemetry"') {
                $json = $line | ConvertFrom-Json
                $vehicle = $json.vehicles | Where-Object id -eq $VehicleId
                return [pscustomobject]@{
                    step = $label; speed = $json.speed; paused = $json.paused
                    status = $vehicle.status; setup0 = $vehicle.setup[0]
                    setup1 = $vehicle.setup[1]; tyres = $vehicle.tyres.Count
                }
            }
        } catch { }
    }
    return [pscustomobject]@{ step=$label; speed='NO_DATA'; paused='NO_DATA'; status='NO_DATA'; setup0='NO_DATA'; setup1='NO_DATA'; tyres='NO_DATA' }
}

function Send-Action([string]$kind, [int]$target, [int]$value, [int]$aux = 0) {
    $packet = @{ type='action'; kind=$kind; target=$target; value=$value; aux=$aux; flag=0 } | ConvertTo-Json -Compress
    $writer.WriteLine($packet)
    Start-Sleep -Milliseconds 900
}

$states = @()
$writer.WriteLine('{"type":"telemetry_request"}')
$states += Read-State 'baseline'
Send-Action 'simulation_speed' -1 2
$states += Read-State 'speed_fast'
Send-Action 'pause_or_play' -1 0
$states += Read-State 'pause_toggle_1'
Send-Action 'pause_or_play' -1 0
$states += Read-State 'pause_toggle_2'
Send-Action 'setup_value' $VehicleId 650 0
Send-Action 'setup_apply' $VehicleId 0
Start-Sleep -Seconds 2
$states += Read-State 'setup_applied'
Send-Action 'pit_tyres' $VehicleId 0 0
Send-Action 'ordered_lap_count' $VehicleId 4
$states += Read-State 'program_applied'
Send-Action 'send_out_on_track' $VehicleId 0
$states += Read-State 'sendout_1s'
Start-Sleep -Seconds 8
$states += Read-State 'sendout_9s'
$tcp.Dispose()
$states | ConvertTo-Json -Depth 4
