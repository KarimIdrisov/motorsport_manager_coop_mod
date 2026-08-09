using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MotorsportManagerCoopLauncher;

internal sealed record TyreChoice(int Option, int Index, string Name)
{
    public override string ToString() => $"{(string.IsNullOrWhiteSpace(Name) ? "Комплект" : Name)} #{Index + 1}";
}

internal sealed record VehicleTelemetry(int Id, string Driver, int Lap, int Position, double Fuel, double TyreWear, string Status, double[]? Setup = null, List<TyreChoice>? Tyres = null)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Driver) ? $"Машина {Id}" : $"{Driver} (#{Id})";
}

internal sealed class RaceControllerForm : Form
{
    private readonly string _host;
    private readonly int _port;
    private readonly Label _connection = new() { AutoSize = true, Text = "Отключено" };
    private readonly Label _session = new() { AutoSize = true, Text = "Сессия: —" };
    private readonly Label _telemetry = new() { AutoSize = true, Text = "Ожидание телеметрии…" };
    private readonly ComboBox _driver = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown _fuel = new() { Minimum = 0, Maximum = 200, Value = 20, Width = 70 };
    private TcpClient? _client;
    private StreamWriter? _writer;
    private CancellationTokenSource? _stop;

    public RaceControllerForm(string host, int port)
    {
        _host = host; _port = port;
        Text = "Motorsport Manager — пульт пилота";
        Width = 680; Height = 560; StartPosition = FormStartPosition.CenterParent;
        FormClosed += (_, _) => Disconnect();
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var connect = new Button { Text = "Подключиться к Host", AutoSize = true }; connect.Click += async (_, _) => await ConnectAsync();
        root.Controls.Add(Row(connect, _connection)); root.Controls.Add(_session);
        root.Controls.Add(Row(new Label { Text = "Пилот:", AutoSize = true }, _driver)); root.Controls.Add(_telemetry);
        root.Controls.Add(Group("Темп", Commands(("Атака", "driving_style", 0), ("Push", "driving_style", 1), ("Нормально", "driving_style", 2), ("Беречь", "driving_style", 3), ("Отступать", "driving_style", 4))));
        root.Controls.Add(Group("Двигатель", Commands(("Супер-обгон", "engine_mode", 0), ("Обгон", "engine_mode", 1), ("Высокий", "engine_mode", 2), ("Средний", "engine_mode", 3), ("Низкий", "engine_mode", 4))));
        root.Controls.Add(Group("ERS", Commands(("Сохранение", "ers_mode", 0), ("Гибрид", "ers_mode", 1), ("Атака", "ers_mode", 2))));
        var track = Commands(("На трассу", "send_out_on_track", 0), ("В гараж", "return_to_garage", 0), ("Пит-стоп", "pit_command", 0), ("Отменить пит", "cancel_pit", 0));
        track.Controls.Add(new Label { Text = "Топливо:", AutoSize = true, Padding = new Padding(8, 7, 0, 0) }); track.Controls.Add(_fuel);
        var setFuel = new Button { Text = "Задать" }; setFuel.Click += (_, _) => Send("pit_fuel", (int)_fuel.Value); track.Controls.Add(setFuel);
        var repair = new Button { Text = "Ремонтировать" }; repair.Click += (_, _) => Send("pit_repair", 1); track.Controls.Add(repair);
        root.Controls.Add(Group("Трасса и пит", track));
        root.Controls.Add(Group("Скорость Host", Commands(("Пауза / продолжить", "pause_or_play", 0), ("1×", "simulation_speed", 0), ("2×", "simulation_speed", 1), ("4×", "simulation_speed", 2))));
        Controls.Add(root);
    }

    private static FlowLayoutPanel Row(params Control[] controls) { var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true }; row.Controls.AddRange(controls); return row; }
    private static GroupBox Group(string title, Control content) { var box = new GroupBox { Text = title, Width = 625, Height = 78, Padding = new Padding(8) }; content.Dock = DockStyle.Fill; box.Controls.Add(content); return box; }
    private FlowLayoutPanel Commands(params (string Label, string Kind, int Value)[] commands)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        foreach (var command in commands) { var button = new Button { Text = command.Label, AutoSize = true }; button.Click += (_, _) => Send(command.Kind, command.Value); row.Controls.Add(button); }
        return row;
    }

    private async Task ConnectAsync()
    {
        Disconnect();
        try
        {
            _client = new TcpClient(); await _client.ConnectAsync(_host, _port);
            _writer = new StreamWriter(_client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await _writer.WriteLineAsync("{\"type\":\"hello\",\"protocol\":0,\"name\":\"Race Controller\"}");
            _stop = new CancellationTokenSource(); _connection.Text = $"Подключено: {_host}:{_port}";
            _ = ReceiveAsync(_client.GetStream(), _stop.Token);
        }
        catch (Exception ex) { _connection.Text = "Ошибка: " + ex.Message; Disconnect(false); }
    }

    private async Task ReceiveAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);
            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(token); if (line == null) break;
                JsonElement? telemetry = TelemetryJson.Parse(line);
                if (telemetry.HasValue) BeginInvoke(() => ApplyTelemetry(telemetry.Value));
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { BeginInvoke(() => _connection.Text = "Связь потеряна: " + ex.Message); }
    }

    private void ApplyTelemetry(JsonElement root)
    {
        _session.Text = "Сессия: " + (root.TryGetProperty("session", out var session) ? session.GetString() : "—");
        int selectedId = (_driver.SelectedItem as VehicleTelemetry)?.Id ?? -1; var vehicles = new List<VehicleTelemetry>();
        if (root.TryGetProperty("vehicles", out var array)) foreach (var item in array.EnumerateArray()) vehicles.Add(new(item.GetProperty("id").GetInt32(), item.GetProperty("driver").GetString() ?? "", item.GetProperty("lap").GetInt32(), item.GetProperty("position").GetInt32(), item.GetProperty("fuel").GetDouble(), item.GetProperty("tyreWear").GetDouble(), item.GetProperty("status").GetString() ?? ""));
        _driver.Items.Clear(); foreach (var vehicle in vehicles) _driver.Items.Add(vehicle);
        if (_driver.Items.Count == 0) return;
        int index = vehicles.FindIndex(v => v.Id == selectedId); _driver.SelectedIndex = index >= 0 ? index : 0;
        var current = (VehicleTelemetry)_driver.SelectedItem!;
        _telemetry.Text = $"Позиция: {current.Position}   Круг: {current.Lap}   Топливо: {current.Fuel:0.0}   Износ шин: {current.TyreWear:0.0}%   {current.Status}";
    }

    private void Send(string kind, int value)
    {
        if (_writer == null) { _connection.Text = "Сначала подключитесь к Host"; return; }
        int target = (_driver.SelectedItem as VehicleTelemetry)?.Id ?? -1;
        try { _writer.WriteLine(JsonSerializer.Serialize(new { type = "action", kind, target, value, aux = 0, flag = 0 })); }
        catch (Exception ex) { _connection.Text = "Команда не отправлена: " + ex.Message; }
    }

    private void Disconnect(bool updateLabel = true)
    {
        _stop?.Cancel(); _stop?.Dispose(); _stop = null; _writer?.Dispose(); _writer = null; _client?.Dispose(); _client = null;
        if (updateLabel) _connection.Text = "Отключено";
    }
}
