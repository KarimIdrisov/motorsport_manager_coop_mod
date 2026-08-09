using System.Diagnostics;
using System.Text.Json;

namespace MotorsportManagerCoopLauncher;

internal sealed class Settings
{
    public string GamePath { get; set; } = @"D:\R.G. Catalyst\Motorsport Manager";
    public string RepositoryUrl { get; set; } = "https://github.com/KarimIdrisov/motorsport_manager_coop_mod.git";
    public string Branch { get; set; } = "main";
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 27153;
}

internal static class Program
{
    private static readonly string Root = AppContext.BaseDirectory;
    private static readonly string SettingsFile = Path.Combine(Root, "launcher.json");
    private static Settings _settings = LoadSettings();
    private static Process? _server;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LauncherForm());
    }

    private static Settings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { return new(); }
    }

    internal static void SaveSettings() => File.WriteAllText(SettingsFile,
        JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));

    internal static Settings Settings => _settings;

    internal static string UpdateMod(Action<string> log)
    {
        EnsureLoader(log);
        if (string.IsNullOrWhiteSpace(_settings.RepositoryUrl))
            return "Git URL не настроен — используется локальная версия.";
        string repo = Path.Combine(Root, "repo");
        if (!Directory.Exists(Path.Combine(repo, ".git")))
            Run("git", $"clone --depth 1 --branch {_settings.Branch} \"{_settings.RepositoryUrl}\" \"{repo}\"", Root, log);
        else
            Run("git", $"-C \"{repo}\" pull --ff-only", Root, log);
        string mod = Path.Combine(repo, "Mod");
        string target = Path.Combine(_settings.GamePath, "Mods", "MotorsportManagerCoop");
        if (!Directory.Exists(mod)) return "В Git-репозитории отсутствует папка Mod.";
        CopyDirectory(mod, target);
        return "Мод обновлён.";
    }

    internal static void StartServer(Action<string> log)
    {
        if (_server is { HasExited: false }) return;
        string script = Path.Combine(Root, "lan_server.js");
        if (!File.Exists(script)) { log("lan_server.js не найден рядом с лаунчером."); return; }
        _server = Process.Start(new ProcessStartInfo("node", $"\"{script}\"")
        {
            WorkingDirectory = Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
            Environment = { ["MM_COOP_PORT"] = _settings.ServerPort.ToString() }
        });
        log($"LAN-сервер запущен на порту {_settings.ServerPort}.");
    }

    internal static void StartGame(Action<string> log)
    {
        EnsureLoader(log);
        string exe = Path.Combine(_settings.GamePath, "MM.exe");
        if (!File.Exists(exe)) { log("MM.exe не найден."); return; }
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = _settings.GamePath });
        log("Игра запущена.");
    }

    internal static string[] GetSaveFiles()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Low", "Playsport Games", "Motorsport Manager", "Cloud", "Saves");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.sav").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : Array.Empty<string>();
    }

    internal static string PrepareCoopSave(string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Сейв не найден", source);
        string target = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + " Coop.sav");
        if (File.Exists(target)) File.Copy(target, target + ".backup", true);
        File.Copy(source, target, true);
        return target;
    }

    private static void EnsureLoader(Action<string> log)
    {
        string game = _settings.GamePath;
        string bundledRoot = Path.Combine(Root, "Loader");
        string[] required = { "winhttp.dll", "doorstop_config.ini" };
        foreach (string file in required)
        {
            string source = Path.Combine(bundledRoot, file);
            string target = Path.Combine(game, file);
            if (!File.Exists(target) && File.Exists(source)) { File.Copy(source, target); log($"Установлен loader: {file}"); }
        }
        string sourceManaged = Path.Combine(bundledRoot, "MM_Data", "Managed", "UnityModManager");
        string targetManaged = Path.Combine(game, "MM_Data", "Managed", "UnityModManager");
        if (!Directory.Exists(targetManaged) && Directory.Exists(sourceManaged))
        {
            CopyDirectory(sourceManaged, targetManaged);
            log("Установлен Unity Mod Manager.");
        }
        Directory.CreateDirectory(Path.Combine(game, "Mods"));
    }

    private static void Run(string file, string args, string cwd, Action<string> log)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        if (p == null) throw new InvalidOperationException($"Не удалось запустить {file}");
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (output.Length > 0) log(output.Trim());
        if (p.ExitCode != 0) throw new InvalidOperationException(error.Trim());
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}

internal sealed class LauncherForm : Form
{
    private readonly TextBox _gamePath = new();
    private readonly TextBox _repo = new();
    private readonly TextBox _port = new();
    private readonly TextBox _log = new();
    private readonly ComboBox _saves = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    public LauncherForm()
    {
        Text = "Motorsport Manager Coop";
        Width = 720; Height = 480; StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 6, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(layout, "Путь к игре", _gamePath, 0, Program.Settings.GamePath);
        Add(layout, "Git URL", _repo, 1, Program.Settings.RepositoryUrl);
        Add(layout, "LAN порт", _port, 2, Program.Settings.ServerPort.ToString());
        layout.Controls.Add(new Label { Text = "Кооп-сейв", AutoSize = true }, 0, 3);
        _saves.Dock = DockStyle.Fill;
        foreach (string save in Program.GetSaveFiles()) _saves.Items.Add(save);
        if (_saves.Items.Count > 0) _saves.SelectedIndex = 0;
        layout.Controls.Add(_saves, 1, 3);
        var update = new Button { Text = "Обновить мод" }; update.Click += (_, _) => Run(Update);
        var start = new Button { Text = "Обновить и запустить игру" }; start.Click += (_, _) => Run(Start);
        var prepare = new Button { Text = "Подготовить кооп-сейв" }; prepare.Click += (_, _) => PrepareSave();
        layout.Controls.Add(update, 0, 4); layout.Controls.Add(start, 1, 4); layout.Controls.Add(prepare, 0, 5); layout.SetColumnSpan(prepare, 2);
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical; _log.Dock = DockStyle.Fill;
        layout.Controls.Add(_log, 0, 6); layout.SetColumnSpan(_log, 2); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
    }

    private void Add(TableLayoutPanel panel, string label, TextBox box, int row, string value)
    { panel.Controls.Add(new Label { Text = label, AutoSize = true }, 0, row); box.Text = value; box.Dock = DockStyle.Fill; panel.Controls.Add(box, 1, row); }
    private void Read()
    { Program.Settings.GamePath = _gamePath.Text; Program.Settings.RepositoryUrl = _repo.Text; Program.Settings.ServerPort = int.TryParse(_port.Text, out var p) ? p : 27153; Program.SaveSettings(); }
    private void Run(Action<Action<string>> action) { try { Read(); action(Log); } catch (Exception ex) { Log("ОШИБКА: " + ex.Message); } }
    private void Update(Action<string> log) => log(Program.UpdateMod(log));
    private void Start(Action<string> log)
    {
        if (_saves.SelectedItem is string source)
            log("Кооп-сейв: " + Program.PrepareCoopSave(source));
        Program.UpdateMod(log);
        Program.StartGame(log);
    }
    private void PrepareSave()
    {
        try
        {
            if (_saves.SelectedItem is not string source) { Log("Сейв не выбран."); return; }
            Log("Подготовлен: " + Program.PrepareCoopSave(source));
        }
        catch (Exception ex) { Log("ОШИБКА: " + ex.Message); }
    }
    private void Log(string message) => BeginInvoke(() => _log.AppendText(message + Environment.NewLine));
}
