param([int]$Port = 27153, [int]$VehicleId = 10, [int]$Samples = 7)
$states = @()
for ($sample = 0; $sample -lt $Samples; $sample++) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.Connect('127.0.0.1', $Port)
        $stream = $client.GetStream(); $stream.ReadTimeout = 3500
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false)); $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
        $writer.WriteLine('{"type":"hello","protocol":0,"name":"long-observer"}')
        $writer.WriteLine('{"type":"telemetry_request"}')
        for ($lineIndex = 0; $lineIndex -lt 3; $lineIndex++) {
            try {
                $line = $reader.ReadLine()
                if ($line -match '"type":"telemetry"') {
                    $json = $line | ConvertFrom-Json
                    $vehicle = $json.vehicles | Where-Object id -eq $VehicleId
                    $states += [pscustomobject]@{
                        time=(Get-Date).ToString('HH:mm:ss'); speed=$json.speed; paused=$json.paused
                        status=$vehicle.status; orderedLaps=$vehicle.orderedLaps; setup0=$vehicle.setup[0]
                    }
                    break
                }
            } catch { }
        }
    } finally { $client.Dispose() }
    Start-Sleep -Seconds 5
}
$states | Format-Table -AutoSize
