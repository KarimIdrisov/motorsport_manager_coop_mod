using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MotorsportManagerCoopLauncher;

internal sealed class DashboardForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(13, 16, 20);
    private static readonly Color Surface = Color.FromArgb(24, 29, 35);
    private static readonly Color SurfaceRaised = Color.FromArgb(32, 38, 45);
    private static readonly Color TextMain = Color.FromArgb(238, 242, 244);
    private static readonly Color TextMuted = Color.FromArgb(159, 170, 178);
    private static readonly Color Accent = Color.FromArgb(78, 218, 128);

    private readonly TextBox _gamePath = Field();
    private readonly TextBox _repo = Field();
    private readonly TextBox _host = Field();
    private readonly TextBox _port = Field();
    private readonly ComboBox _saves = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Height = 32 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Canvas, ForeColor = TextMuted, BorderStyle = BorderStyle.None };
    private readonly Panel _controllerHost = new() { Dock = DockStyle.Fill };
    private RaceControlPanel? _controller;

    public DashboardForm()
    {
        Text = "Motorsport Manager Coop — Race Command";
        MinimumSize = new Size(1180, 720); Size = new Size(1360, 820); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Canvas; ForeColor = TextMain; Font = new Font("Segoe UI", 10f);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(20), BackColor = Canvas };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Control header = Header(); shell.Controls.Add(header, 0, 0);
        shell.Controls.Add(_controllerHost, 0, 1); Controls.Add(shell);
        LoadSettings(); RebuildController();
        Shown += async (_, _) =>
        {
            bool updating = await Task.Run(() => Program.TryScheduleLauncherUpdate(Log));
            if (updating) Close();
        };
    }

    private Control Header()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(new Label { Text = "RACE COMMAND", Font = new Font("Segoe UI Semibold", 23f), ForeColor = TextMain, AutoSize = true, Location = new Point(0, 8) });
        panel.Controls.Add(new Label { Text = "Один Host. Один пилот. Полный контроль сессии.", ForeColor = TextMuted, AutoSize = true, Location = new Point(4, 47) });
        var network = ActionButton("ПОДКЛЮЧЕНИЕ", SurfaceRaised, TextMain);
        network.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        network.Location = new Point(panel.Width - 170, 18);
        network.Width = 160;
        network.Click += (_, _) => EditConnection();
        panel.Resize += (_, _) => network.Left = panel.ClientSize.Width - network.Width;
        panel.Controls.Add(network);
        return panel;
    }

    private void EditConnection()
    {
        using var dialog = new Form
        {
            Text = "Подключение к Host", ClientSize = new Size(410, 190),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, BackColor = Surface, ForeColor = TextMain,
            Font = Font
        };
        var host = Field(); host.Text = Program.Settings.ServerHost; host.Location = new Point(28, 45); host.Width = 354;
        var port = Field(); port.Text = Program.Settings.ServerPort.ToString(); port.Location = new Point(28, 105); port.Width = 170;
        dialog.Controls.Add(new Label { Text = "IP компьютера Host", AutoSize = true, ForeColor = TextMuted, Location = new Point(28, 22) });
        dialog.Controls.Add(host);
        dialog.Controls.Add(new Label { Text = "LAN-порт", AutoSize = true, ForeColor = TextMuted, Location = new Point(28, 82) });
        dialog.Controls.Add(port);
        var save = ActionButton("СОХРАНИТЬ", Accent, Color.FromArgb(8, 25, 15));
        save.Location = new Point(230, 104); save.Width = 152; save.DialogResult = DialogResult.OK;
        dialog.Controls.Add(save); dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!int.TryParse(port.Text, out int parsedPort) || parsedPort < 1 || parsedPort > 65535)
        {
            MessageBox.Show(this, "Порт должен быть числом от 1 до 65535.", "Неверный порт", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Program.Settings.ServerHost = string.IsNullOrWhiteSpace(host.Text) ? "127.0.0.1" : host.Text.Trim();
        Program.Settings.ServerPort = parsedPort;
        Program.SaveSettings();
        RebuildController();
    }

    private Control SetupPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(18) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        flow.Controls.Add(Title("Host и установка"));
        flow.Controls.Add(Labeled("Путь к игре", _gamePath)); flow.Controls.Add(Labeled("Git-репозиторий", _repo));
        flow.Controls.Add(Labeled("IP Host", _host)); flow.Controls.Add(Labeled("LAN-порт", _port)); flow.Controls.Add(Labeled("Сейв Host", _saves));
        var launch = ActionButton("ОБНОВИТЬ И ЗАПУСТИТЬ HOST", Accent, Color.FromArgb(8, 25, 15)); launch.Width = 294; launch.Click += (_, _) => RunHost(); flow.Controls.Add(launch);
        var update = ActionButton("Только обновить мод", SurfaceRaised, TextMain); update.Width = 294; update.Click += (_, _) => Run(() => Log(Program.UpdateMod(Log))); flow.Controls.Add(update);
        flow.Controls.Add(new Label { Text = "Журнал", ForeColor = TextMuted, AutoSize = true, Margin = new Padding(0, 20, 0, 8) });
        _log.Width = 294; _log.Height = 150; flow.Controls.Add(_log); panel.Controls.Add(flow); return panel;
    }

    private void LoadSettings()
    {
        _gamePath.Text = Program.Settings.GamePath; _repo.Text = Program.Settings.RepositoryUrl;
        _host.Text = Program.Settings.ServerHost; _port.Text = Program.Settings.ServerPort.ToString();
        foreach (string save in Program.GetSaveFiles()) _saves.Items.Add(save); if (_saves.Items.Count > 0) _saves.SelectedIndex = 0;
    }

    private void SaveSettings()
    {
        Program.Settings.GamePath = _gamePath.Text; Program.Settings.RepositoryUrl = _repo.Text;
        Program.Settings.ServerHost = string.IsNullOrWhiteSpace(_host.Text) ? "127.0.0.1" : _host.Text.Trim();
        Program.Settings.ServerPort = int.TryParse(_port.Text, out int port) ? port : 27153; Program.SaveSettings();
    }

    private void RunHost()
    {
        Run(() => { if (_saves.SelectedItem is string save) Log("Сейв Host: " + Program.PrepareCoopSave(save)); Log(Program.UpdateMod(Log)); Program.StartGame(Log); });
    }

    private void Run(Action action) { try { SaveSettings(); action(); RebuildController(); } catch (Exception ex) { Log("Ошибка: " + ex.Message); } }
    private void RebuildController() { _controller?.Dispose(); _controllerHost.Controls.Clear(); _controller = new RaceControlPanel(Program.Settings.ServerHost, Program.Settings.ServerPort) { Dock = DockStyle.Fill }; _controllerHost.Controls.Add(_controller); }
    private void Log(string message) { if (InvokeRequired) { BeginInvoke(() => Log(message)); return; } _log.AppendText(message + Environment.NewLine); }

    private static TextBox Field() => new() { Width = 294, Height = 32, BackColor = SurfaceRaised, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
    private static Label Title(string text) => new() { Text = text, Font = new Font("Segoe UI Semibold", 16f), ForeColor = TextMain, AutoSize = true, Margin = new Padding(0, 0, 0, 16) };
    private static Control Labeled(string label, Control control) { var p = new FlowLayoutPanel { Width = 300, Height = 62, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 6) }; p.Controls.Add(new Label { Text = label, ForeColor = TextMuted, AutoSize = true }); control.Width = 294; control.BackColor = SurfaceRaised; control.ForeColor = TextMain; p.Controls.Add(control); return p; }
    internal static Button ActionButton(string text, Color back, Color fore) { var b = new Button { Text = text, AutoSize = true, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore, Cursor = Cursors.Hand, Margin = new Padding(4) }; b.FlatAppearance.BorderSize = 0; return b; }
    internal static Color UiCanvas => Canvas; internal static Color UiSurface => Surface; internal static Color UiRaised => SurfaceRaised; internal static Color UiText => TextMain; internal static Color UiMuted => TextMuted; internal static Color UiAccent => Accent;
}

internal sealed class RaceControlPanel : UserControl
{
    private readonly string _host; private readonly int _port;
    private readonly Label _state = new() { AutoSize = true }; private readonly Label _session = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 18f) };
    private readonly Label _metrics = new() { AutoSize = true, ForeColor = DashboardForm.UiMuted };
    private readonly ComboBox _driver = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 300 };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, SizeMode = TabSizeMode.Fixed, ItemSize = new Size(170, 38) };
    private readonly TrackBar[] _setupBars = new TrackBar[7];
    private readonly List<ComboBox> _tyreSelectors = new();
    private TcpClient? _client; private StreamWriter? _writer; private CancellationTokenSource? _stop;
    private JsonElement? _latestTelemetry;
    private readonly Label _testState = new() { AutoSize = true, ForeColor = DashboardForm.UiMuted };
    private bool _testRunning;

    public RaceControlPanel(string host, int port)
    {
        _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host; _port = port; BackColor = DashboardForm.UiCanvas; ForeColor = DashboardForm.UiText; Padding = new Padding(22, 0, 0, 0);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = BackColor }; root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(ConnectionBar(), 0, 0); root.Controls.Add(DriverBar(), 0, 1); root.Controls.Add(BuildTabs(), 0, 2); Controls.Add(root);
        _driver.SelectedIndexChanged += (_, _) => ApplySelectedSetup();
        HandleCreated += async (_, _) => await ConnectAsync(); Disposed += (_, _) => Disconnect();
    }

    private Control ConnectionBar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = DashboardForm.UiSurface, Padding = new Padding(18) };
        _state.Text = "●  Подключение…"; _state.ForeColor = DashboardForm.UiMuted; _state.Location = new Point(18, 18); p.Controls.Add(_state);
        _session.Text = "Ожидание сессии"; _session.Location = new Point(18, 44); p.Controls.Add(_session);
        var refresh = DashboardForm.ActionButton("ОБНОВИТЬ СОСТОЯНИЕ", DashboardForm.UiRaised, DashboardForm.UiText); refresh.Location = new Point(620, 24); refresh.Click += async (_, _) => await RefreshAsync(); p.Controls.Add(refresh); return p;
    }

    private Control DriverBar()
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = DashboardForm.UiCanvas, Padding = new Padding(0, 14, 0, 6) };
        p.Controls.Add(new Label { Text = "ПИЛОТ", ForeColor = DashboardForm.UiMuted, AutoSize = true, Padding = new Padding(0, 7, 8, 0) }); _driver.BackColor = DashboardForm.UiRaised; _driver.ForeColor = DashboardForm.UiText; p.Controls.Add(_driver); p.Controls.Add(_metrics);
        var test = DashboardForm.ActionButton("БЫСТРЫЙ ТЕСТ", DashboardForm.UiRaised, DashboardForm.UiText); test.Click += async (_, _) => await RunSmokeTestAsync(); p.Controls.Add(test); p.Controls.Add(_testState); return p;
    }

    private Control BuildTabs()
    {
        _tabs.Appearance = TabAppearance.Normal; _tabs.Controls.Add(Page("Практика", PracticeControls())); _tabs.Controls.Add(Page("Квалификация", QualifyingControls())); _tabs.Controls.Add(Page("Гонка", RaceControls())); return _tabs;
    }
    private TabPage Page(string title, Control controls) { var page = new TabPage(title) { BackColor = DashboardForm.UiSurface, ForeColor = DashboardForm.UiText, Padding = new Padding(18) }; controls.Dock = DockStyle.Fill; page.Controls.Add(controls); return page; }
    private Control PracticeControls() => Sections(SetupSection(), StintSection(), ModeSection(), EngineSection(), Row(("НА ТРАССУ", "send_out_on_track", 0), ("В ГАРАЖ", "return_to_garage", 0)), SpeedSection());
    private Control QualifyingControls() => Sections(StintSection(), ModeSection(), EngineSection(), Row(("НА ТРАССУ", "send_out_on_track", 0), ("В ГАРАЖ", "return_to_garage", 0), ("ПИТ", "pit_command", 0)), SpeedSection());
    private Control RaceControls() => Sections(ModeSection(), EngineSection(), Row(("ERS: ЗАРЯД", "ers_mode", 0), ("ERS: ГИБРИД", "ers_mode", 1), ("ERS: МОЩНОСТЬ", "ers_mode", 2)), Row(("ПИТ-СТОП", "pit_command", 0), ("ОТМЕНИТЬ ПИТ", "cancel_pit", 0), ("РЕМОНТ", "pit_repair", 1)), SpeedSection());
    private Control ModeSection() => Row(("АТАКА", "driving_style", 0), ("PUSH", "driving_style", 1), ("НЕЙТРАЛЬНО", "driving_style", 2), ("БЕРЕЧЬ", "driving_style", 3), ("ОТСТУПАТЬ", "driving_style", 4));
    private Control EngineSection() => Row(("СУПЕР-ОБГОН", "engine_mode", 0), ("ОБГОН", "engine_mode", 1), ("ВЫСОКИЙ", "engine_mode", 2), ("СРЕДНИЙ", "engine_mode", 3), ("НИЗКИЙ", "engine_mode", 4));
    private Control SpeedSection() => Row(("ПАУЗА / ПУСК", "pause_or_play", 0), ("1×", "simulation_speed", 0), ("2×", "simulation_speed", 1), ("4×", "simulation_speed", 2));
    private Control SetupSection()
    {
        string[] names = { "Давление шин", "Развал", "Подвеска", "Передаточные числа", "Переднее крыло", "Заднее крыло", "Балласт" };
        var grid = new TableLayoutPanel { Width = 900, Height = 245, ColumnCount = 2, RowCount = 8, BackColor = DashboardForm.UiSurface, Margin = new Padding(0, 0, 0, 14) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < names.Length; i++)
        {
            int option = i; grid.Controls.Add(new Label { Text = names[i], ForeColor = DashboardForm.UiMuted, AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, 0, i);
            var bar = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 650, BackColor = DashboardForm.UiSurface };
            bar.MouseUp += (_, _) => Send("setup_value", bar.Value * 10, option); _setupBars[i] = bar; grid.Controls.Add(bar, 1, i);
        }
        var apply = DashboardForm.ActionButton("ПРИМЕНИТЬ НАСТРОЙКИ БОЛИДА", DashboardForm.UiAccent, Color.FromArgb(8, 25, 15)); apply.Click += (_, _) => Send("setup_apply", 0); grid.Controls.Add(apply, 1, 7); return grid;
    }
    private Control StintSection()
    {
        var row = new FlowLayoutPanel { Width = 900, Height = 62, Margin = new Padding(0, 0, 0, 12), BackColor = DashboardForm.UiSurface };
        row.Controls.Add(new Label { Text = "Резина", ForeColor = DashboardForm.UiMuted, AutoSize = true, Padding = new Padding(0, 11, 6, 0) });
        var tyres = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 220, BackColor = DashboardForm.UiRaised, ForeColor = DashboardForm.UiText, Margin = new Padding(4, 7, 14, 0) }; _tyreSelectors.Add(tyres); row.Controls.Add(tyres);
        row.Controls.Add(new Label { Text = "Кругов в программе", ForeColor = DashboardForm.UiMuted, AutoSize = true, Padding = new Padding(0, 11, 6, 0) });
        var laps = new NumericUpDown { Minimum = 1, Maximum = 30, Value = 5, Width = 64, BackColor = DashboardForm.UiRaised, ForeColor = DashboardForm.UiText, Margin = new Padding(4, 7, 14, 0) }; row.Controls.Add(laps);
        var apply = DashboardForm.ActionButton("ПРИМЕНИТЬ ПРОГРАММУ", DashboardForm.UiAccent, Color.FromArgb(8, 25, 15));
        apply.Click += (_, _) =>
        {
            if (tyres.SelectedItem is TyreChoice tyre) Send("pit_tyres", tyre.Option, tyre.Index);
            Send("pit_fuel", (int)laps.Value + 2);
            Send("ordered_lap_count", (int)laps.Value);
        };
        row.Controls.Add(apply); return row;
    }
    private Control Sections(params Control[] controls) { var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true }; foreach (Control c in controls) p.Controls.Add(c); return p; }
    private Control Row(params (string Text, string Kind, int Value)[] commands) { var p = new FlowLayoutPanel { Width = 850, Height = 62, Margin = new Padding(0, 0, 0, 12) }; foreach (var c in commands) { var b = DashboardForm.ActionButton(c.Text, DashboardForm.UiRaised, DashboardForm.UiText); b.Click += (_, _) => Send(c.Kind, c.Value); p.Controls.Add(b); } return p; }

    private async Task ConnectAsync()
    {
        Disconnect(); try { _client = new TcpClient(); await _client.ConnectAsync(_host, _port); _writer = new StreamWriter(_client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" }; await _writer.WriteLineAsync("{\"type\":\"hello\",\"protocol\":0,\"name\":\"Integrated Race Command\"}"); _stop = new(); _state.Text = $"●  Host {_host}:{_port}"; _state.ForeColor = DashboardForm.UiAccent; _ = ReceiveAsync(_client.GetStream(), _stop.Token); await RequestTelemetryAsync(); } catch (Exception ex) { _state.Text = "●  Нет связи — " + ex.Message; _state.ForeColor = Color.FromArgb(240, 105, 105); }
    }
    private async Task RefreshAsync() { if (_writer == null) await ConnectAsync(); else await RequestTelemetryAsync(); }
    private Task RequestTelemetryAsync() => _writer?.WriteLineAsync("{\"type\":\"telemetry_request\"}") ?? Task.CompletedTask;
    private async Task ReceiveAsync(NetworkStream stream, CancellationToken token) { try { using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true); while (!token.IsCancellationRequested) { string? line = await reader.ReadLineAsync(token); if (line == null) throw new IOException("Host закрыл соединение"); JsonElement? t = TelemetryJson.Parse(line); if (t.HasValue) BeginInvoke(() => ApplyTelemetry(t.Value)); } } catch (Exception ex) when (!token.IsCancellationRequested) { BeginInvoke(() => { Disconnect(); _state.Text = "●  Связь потеряна — " + ex.Message; _state.ForeColor = Color.FromArgb(240, 105, 105); }); } }
    private void ApplyTelemetry(JsonElement root)
    {
        _latestTelemetry = root;
        string session = root.TryGetProperty("session", out var s) ? s.GetString() ?? "" : ""; _session.Text = string.IsNullOrWhiteSpace(session) ? "Host подключён — сессия не запущена" : session;
        if (session.Contains("Practice", StringComparison.OrdinalIgnoreCase)) _tabs.SelectedIndex = 0; else if (session.Contains("Qual", StringComparison.OrdinalIgnoreCase)) _tabs.SelectedIndex = 1; else if (session.Contains("Race", StringComparison.OrdinalIgnoreCase)) _tabs.SelectedIndex = 2;
        int selected = (_driver.SelectedItem as VehicleTelemetry)?.Id ?? -1; var vehicles = new List<VehicleTelemetry>(); if (root.TryGetProperty("vehicles", out var a)) foreach (var item in a.EnumerateArray()) vehicles.Add(new(item.GetProperty("id").GetInt32(), item.GetProperty("driver").GetString() ?? "", item.GetProperty("lap").GetInt32(), item.GetProperty("position").GetInt32(), item.GetProperty("fuel").GetDouble(), item.GetProperty("tyreWear").GetDouble(), item.GetProperty("status").GetString() ?? "", ReadSetup(item), ReadTyres(item), ReadOptionalInt(item, "selectedTyreOption", -1), ReadOptionalInt(item, "selectedTyreIndex", -1)));
        _driver.Items.Clear(); foreach (var v in vehicles) _driver.Items.Add(v); if (_driver.Items.Count == 0) { _metrics.Text = "Пилоты появятся после входа Host в сессию"; return; } int index = vehicles.FindIndex(v => v.Id == selected); _driver.SelectedIndex = index >= 0 ? index : 0; var current = (VehicleTelemetry)_driver.SelectedItem!; _metrics.Text = $"   P{current.Position}   Круг {current.Lap}   Топливо {current.Fuel:0.0}   Шины {current.TyreWear:0}%"; ApplySelectedSetup();
    }
    private void ApplySelectedSetup() { var vehicle = _driver.SelectedItem as VehicleTelemetry; var setup = vehicle?.Setup; if (setup != null) for (int i = 0; i < Math.Min(setup.Length, _setupBars.Length); i++) if (_setupBars[i] != null) { _setupBars[i].Enabled = setup[i] >= 0; if (setup[i] >= 0) _setupBars[i].Value = Math.Max(0, Math.Min(100, (int)Math.Round(setup[i] * 100))); } foreach (ComboBox selector in _tyreSelectors) { int selectedOption = vehicle?.SelectedTyreOption ?? -1; int selectedIndex = vehicle?.SelectedTyreIndex ?? -1; selector.Items.Clear(); foreach (TyreChoice tyre in vehicle?.Tyres ?? new List<TyreChoice>()) selector.Items.Add(tyre); int match = (vehicle?.Tyres ?? new List<TyreChoice>()).FindIndex(t => t.Option == selectedOption && t.Index == selectedIndex); if (selector.Items.Count > 0) selector.SelectedIndex = match >= 0 ? match : 0; } }
    private static double[] ReadSetup(JsonElement vehicle) { if (!vehicle.TryGetProperty("setup", out var values)) return Array.Empty<double>(); return values.EnumerateArray().Select(value => value.GetDouble()).ToArray(); }
    private static List<TyreChoice> ReadTyres(JsonElement vehicle) { var result = new List<TyreChoice>(); if (!vehicle.TryGetProperty("tyres", out var values)) return result; foreach (JsonElement value in values.EnumerateArray()) result.Add(new(value.GetProperty("option").GetInt32(), value.GetProperty("index").GetInt32(), value.GetProperty("name").GetString() ?? "")); return result; }
    private static int ReadOptionalInt(JsonElement value, string name, int fallback) => value.TryGetProperty(name, out var property) && property.TryGetInt32(out int result) ? result : fallback;
    private void Send(string kind, int value, int aux = 0) { if (_writer == null) { _state.Text = "●  Нажмите «Обновить состояние»"; return; } int target = (_driver.SelectedItem as VehicleTelemetry)?.Id ?? -1; try { _writer.WriteLine(JsonSerializer.Serialize(new { type = "action", kind, target, value, aux, flag = 0 })); } catch (Exception ex) { Disconnect(); _state.Text = "●  Команда не отправлена — " + ex.Message; _state.ForeColor = Color.FromArgb(240, 105, 105); } }

    private async Task RunSmokeTestAsync()
    {
        if (_testRunning) return;
        if (_writer == null || _driver.SelectedItem is not VehicleTelemetry vehicle) { _testState.Text = "Нет подключения или пилота"; return; }
        _testRunning = true;
        var results = new List<string>();
        try
        {
            SmokeTestConfig config = SmokeTestConfig.Load();
            await CheckAsync("скорость", () => Send("simulation_speed", config.Speed), root => ReadInt(root, "speed") == config.Speed, results, config.CommandTimeoutMs);
            await CheckAsync("пауза", () => Send("pause_or_play", 0), root => ReadBool(root, "paused"), results);
            Send("pause_or_play", 0);
            await WaitForAsync(root => !ReadBool(root, "paused"), 4000);

            double[] setup = vehicle.Setup ?? Array.Empty<double>();
            int setupOption = Array.FindIndex(setup, value => value >= 0);
            if (setupOption >= 0)
            {
                int setupValue = setup[setupOption] > 0.55 ? config.SetupLow : config.SetupHigh;
                await CheckAsync("настройки", () => { Send("setup_value", setupValue, setupOption); Send("setup_apply", 0); },
                    root => Math.Abs(ReadVehicle(root, vehicle.Id).GetProperty("setup")[setupOption].GetDouble() - setupValue / 1000d) < 0.01, results);
            }
            else results.Add("SKIP настройки");

            await CheckAsync("программа", () => { Send("pit_fuel", config.OrderedLaps + 2); Send("ordered_lap_count", config.OrderedLaps); },
                root => ReadVehicle(root, vehicle.Id).TryGetProperty("orderedLaps", out var laps) && laps.GetInt32() == config.OrderedLaps, results, config.CommandTimeoutMs);

            JsonElement currentVehicle = ReadVehicle(_latestTelemetry!.Value, vehicle.Id);
            string status = currentVehicle.TryGetProperty("status", out var statusValue) ? statusValue.GetString() ?? "" : "";
            if (status == "NoActionRequired")
                await CheckAsync("выпуск", () => Send("send_out_on_track", 0),
                    root => (ReadVehicle(root, vehicle.Id).GetProperty("status").GetString() ?? "") != "NoActionRequired", results, 12000);
            else results.Add("SKIP выпуск (" + status + ")");
        }
        catch (Exception ex) { results.Add("FAIL " + ex.Message); }
        finally { _testRunning = false; _testState.Text = string.Join("  •  ", results); }
    }

    private async Task CheckAsync(string name, Action action, Func<JsonElement, bool> condition, List<string> results, int timeoutMs = 6000)
    {
        _testState.Text = "Проверка: " + name; action();
        results.Add(await WaitForAsync(condition, timeoutMs) ? "PASS " + name : "FAIL " + name);
    }

    private async Task<bool> WaitForAsync(Func<JsonElement, bool> condition, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_latestTelemetry.HasValue && condition(_latestTelemetry.Value)) return true;
            await RequestTelemetryAsync(); await Task.Delay(250);
        }
        return false;
    }

    private static int ReadInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetInt32() : -1;
    private static bool ReadBool(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.GetBoolean();
    private static JsonElement ReadVehicle(JsonElement root, int id)
    {
        if (root.TryGetProperty("vehicles", out var vehicles)) foreach (JsonElement vehicle in vehicles.EnumerateArray()) if (vehicle.GetProperty("id").GetInt32() == id) return vehicle;
        throw new InvalidOperationException("Пилот исчез из телеметрии");
    }
    private void Disconnect() { _stop?.Cancel(); _stop?.Dispose(); _stop = null; _writer?.Dispose(); _writer = null; _client?.Dispose(); _client = null; }
}

internal sealed class SmokeTestConfig
{
    public int Speed { get; set; } = 1;
    public int SetupLow { get; set; } = 450;
    public int SetupHigh { get; set; } = 650;
    public int OrderedLaps { get; set; } = 4;
    public int CommandTimeoutMs { get; set; } = 6000;

    public static SmokeTestConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "race-tests.json");
        try { return JsonSerializer.Deserialize<SmokeTestConfig>(File.ReadAllText(path)) ?? new SmokeTestConfig(); }
        catch { return new SmokeTestConfig(); }
    }
}
