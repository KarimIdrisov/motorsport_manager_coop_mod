using System;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityModManagerNet;
using HarmonyLib;

namespace MotorsportManagerCoop
{
    public static class Main
    {
        private static UnityModManager.ModEntry _mod;
        private static bool _enabled;
        private static bool _window;
        private static string _host = "127.0.0.1";
        private static string _port = "27153";
        private static string _name = "Player 2";
        private static string _status = "Disconnected";
        private static TcpClient _client;
        private static NetworkStream _stream;
        private static Harmony _harmony;
        private static GameTimer _timer;
        private static RaceEventDetails _raceEvent;
        private static SessionStrategy _strategy;
        private static SaveSystem _saveSystem;
        private static Thread _receiveThread;
        private static readonly Queue<string> _incoming = new Queue<string>();
        private static readonly object _incomingLock = new object();
        private static bool _applyRemoteAction;
        private static bool _isHost;
        private static int _lastRevision;
        private static FileStream _snapshotFile;
        private static string _snapshotTemp;
        private static string _snapshotTarget;
        private static string _snapshotSaveName = "SaveJohn Sina - Scuderia Rossini 7 Coop.sav";
        private static TcpListener _listener;
        private static readonly List<TcpClient> _hostClients = new List<TcpClient>();
        private static readonly object _hostLock = new object();
        private static int _hostRevision;

        private static void Log(string message)
        {
            try { if (_mod != null) _mod.Logger.Log("[COOP] " + message); } catch { }
        }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            _mod = modEntry;
            _enabled = true;
            Log("load version=0.1.0");
            modEntry.OnGUI = OnGUI;
            modEntry.OnToggle = OnToggle;
            modEntry.OnUnload = OnUnload;
            _harmony = new Harmony("codex.motorsportmanager.coop");
            _harmony.Patch(
                AccessTools.Method(typeof(GameTimer), "PlaySkipSim"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureTimer)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPlaySkipSim)));
            _harmony.Patch(
                AccessTools.Method(typeof(GameTimer), "PauseOrPlaySkipSim"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureTimer)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPauseOrPlaySkipSim)));
            _harmony.Patch(
                AccessTools.Method(typeof(RaceEventDetails), "GoToNextSession"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureRaceEvent)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnGoToNextSession)));
            _harmony.Patch(
                AccessTools.Method(typeof(SessionStrategy), "SetTeamOrders"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSetTeamOrders)));
            _harmony.Patch(
                AccessTools.Method(typeof(SessionStrategy), "SetPitStrategy"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSetPitStrategy)));
            _harmony.Patch(
                AccessTools.Method(typeof(SaveSystem), "ManualSave"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureSaveSystem)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnManualSave)));
            return true;
        }

        private static void OnPlaySkipSim()
        {
            if (_applyRemoteAction) return;
            // __instance is captured by the prefix below before the postfix runs.
            if (_stream == null && !_isHost) return;
            try
            {
                byte[] action = Encoding.UTF8.GetBytes(
                    "{\"type\":\"action\",\"id\":\"skip-sim\",\"kind\":\"play_skip_sim\",\"payload\":{}}\n");
                SendPacket(action);
                Log("send kind=play_skip_sim");
                _status = "Sent: play/skip simulation";
            }
            catch { Disconnect(); }
        }

        private static void OnPauseOrPlaySkipSim()
        {
            if (_applyRemoteAction || (_stream == null && !_isHost)) return;
            try
            {
                byte[] action = Encoding.UTF8.GetBytes(
                    "{\"type\":\"action\",\"id\":\"pause-play\",\"kind\":\"pause_or_play\",\"payload\":{}}\n");
                SendPacket(action);
                Log("send kind=pause_or_play");
                _status = "Sent: pause/play simulation";
            }
            catch { Disconnect(); }
        }

        private static void CaptureTimer(GameTimer __instance)
        {
            _timer = __instance;
        }

        private static void CaptureRaceEvent(RaceEventDetails __instance)
        {
            _raceEvent = __instance;
        }

        private static void CaptureStrategy(SessionStrategy __instance)
        {
            _strategy = __instance;
        }

        private static void CaptureSaveSystem(SaveSystem __instance)
        {
            _saveSystem = __instance;
        }

        private static void OnManualSave()
        {
            if (_applyRemoteAction || (_stream == null && !_isHost)) return;
            if (!_isHost)
            {
                _status = "Only host may save";
                return;
            }
            try
            {
                byte[] action = Encoding.UTF8.GetBytes(
                    "{\"type\":\"action\",\"kind\":\"manual_save\",\"value\":0}\n");
                SendPacket(action);
                _status = "Sent: manual save";
            }
            catch { Disconnect(); }
        }

        private static void OnSetTeamOrders(SessionStrategy.TeamOrders inTeamOrders)
        {
            SendStrategyAction("team_orders", (int)inTeamOrders);
        }

        private static void OnSetPitStrategy(SessionStrategy.PitStrategy pitStrategy)
        {
            SendStrategyAction("pit_strategy", (int)pitStrategy);
        }

        private static void SendStrategyAction(string kind, int value)
        {
            if (_applyRemoteAction || (_stream == null && !_isHost)) return;
            try
            {
                byte[] action = Encoding.UTF8.GetBytes(
                    "{\"type\":\"action\",\"kind\":\"" + kind + "\",\"value\":" + value + "}\n");
                SendPacket(action);
                _status = "Sent: " + kind;
            }
            catch { Disconnect(); }
        }

        private static int ReadActionValue(string json)
        {
            Match match = Regex.Match(json, "\\\"value\\\"\\s*:\\s*(-?\\d+)");
            int value;
            return match.Success && Int32.TryParse(match.Groups[1].Value, out value) ? value : 0;
        }

        private static int ReadRevision(string json)
        {
            Match match = Regex.Match(json, "\\\"revision\\\"\\s*:\\s*(\\d+)");
            int value;
            return match.Success && Int32.TryParse(match.Groups[1].Value, out value) ? value : 0;
        }

        private static void OnGoToNextSession()
        {
            if (_applyRemoteAction || (_stream == null && !_isHost)) return;
            try
            {
                byte[] action = Encoding.UTF8.GetBytes(
                    "{\"type\":\"action\",\"id\":\"next-session\",\"kind\":\"go_next_session\",\"payload\":{}}\n");
                SendPacket(action);
                _status = "Sent: next race session";
            }
            catch { Disconnect(); }
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            _enabled = value;
            if (!value) Disconnect();
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            if (!_enabled) return;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("LAN Coop", GUILayout.Width(100))) _window = !_window;
            GUILayout.Label(_status, GUILayout.Width(220));
            GUILayout.EndHorizontal();
            string incoming = null;
            lock (_incomingLock) if (_incoming.Count > 0) incoming = _incoming.Dequeue();
            if (incoming != null && incoming.IndexOf("\"type\":\"welcome\"", StringComparison.Ordinal) >= 0)
            {
                _isHost = incoming.IndexOf("\"role\":\"host\"", StringComparison.Ordinal) >= 0;
                _status = _isHost ? "Connected as host" : "Connected as client";
            }
            if (incoming != null && incoming.IndexOf("host_changed", StringComparison.Ordinal) >= 0)
                _status = "Host changed; reconnect required for role refresh";
            if (incoming != null && incoming.IndexOf("action_ack", StringComparison.Ordinal) >= 0)
                _status = "Host confirmed action";
            if (incoming != null && incoming.IndexOf("\"type\":\"action\"", StringComparison.Ordinal) >= 0)
            {
                int revision = ReadRevision(incoming);
                if (revision > 0)
                {
                    if (_lastRevision > 0 && revision != _lastRevision + 1)
                    {
                        _status = "Resync required: missed revision " + _lastRevision + " -> " + revision;
                        if (!_isHost)
                            SendPacket(Encoding.UTF8.GetBytes("{\"type\":\"resync_request\"}\n"));
                    }
                    _lastRevision = Math.Max(_lastRevision, revision);
                }
            }
            if (incoming != null && incoming.IndexOf("\"type\":\"resync_snapshot\"", StringComparison.Ordinal) >= 0)
            {
                _lastRevision = ReadRevision(incoming);
                _status = "Resync complete at revision " + _lastRevision;
            }
            if (incoming != null && incoming.IndexOf("\"type\":\"save_begin\"", StringComparison.Ordinal) >= 0)
            {
                Match nameMatch = Regex.Match(incoming, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                string saveName = nameMatch.Success ? nameMatch.Groups[1].Value : _snapshotSaveName;
                _snapshotSaveName = saveName;
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Low\\Playsport Games\\Motorsport Manager\\Cloud\\Saves";
                _snapshotTarget = Path.Combine(dir, saveName);
                _snapshotTemp = _snapshotTarget + ".coop.tmp";
                Directory.CreateDirectory(dir);
                _snapshotFile = new FileStream(_snapshotTemp, FileMode.Create, FileAccess.Write, FileShare.None);
                _status = "Receiving host save...";
            }
            if (incoming != null && incoming.IndexOf("\"type\":\"save_chunk\"", StringComparison.Ordinal) >= 0 && _snapshotFile != null)
            {
                Match chunk = Regex.Match(incoming, "\\\"data\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
                if (chunk.Success) { byte[] data = Convert.FromBase64String(chunk.Groups[1].Value); _snapshotFile.Write(data, 0, data.Length); }
            }
            if (incoming != null && incoming.IndexOf("\"type\":\"save_end\"", StringComparison.Ordinal) >= 0 && _snapshotFile != null)
            {
                _snapshotFile.Close(); _snapshotFile = null;
                if (File.Exists(_snapshotTarget)) File.Copy(_snapshotTarget, _snapshotTarget + ".backup", true);
                File.Copy(_snapshotTemp, _snapshotTarget, true); File.Delete(_snapshotTemp);
                try
                {
                    var method = typeof(SaveSystem).GetMethod("LoadSaveWithName");
                    if (method != null) method.Invoke(_saveSystem, new object[] { Path.GetFileNameWithoutExtension(_snapshotSaveName) });
                    _status = "Host save received and load requested";
                }
                catch (Exception ex) { _status = "Host save received; load failed: " + ex.Message; }
            }
            if (incoming != null && _timer != null &&
                (incoming.IndexOf("play_skip_sim", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("pause_or_play", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    if (incoming.IndexOf("pause_or_play", StringComparison.Ordinal) >= 0)
                        _timer.PauseOrPlaySkipSim();
                    else
                        _timer.PlaySkipSim();
                    _status = "Applied remote simulation command";
                }
                catch (Exception ex) { _status = "Remote action failed: " + ex.Message; }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && incoming.IndexOf("go_next_session", StringComparison.Ordinal) >= 0 && _raceEvent != null)
            {
                _applyRemoteAction = true;
                try { _raceEvent.GoToNextSession(); _status = "Applied remote next-session command"; }
                catch (Exception ex) { _status = "Remote session change failed: " + ex.Message; }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && _strategy != null &&
                (incoming.IndexOf("team_orders", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("pit_strategy", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    int value = ReadActionValue(incoming);
                    if (incoming.IndexOf("team_orders", StringComparison.Ordinal) >= 0)
                        _strategy.SetTeamOrders((SessionStrategy.TeamOrders)value);
                    else
                        _strategy.SetPitStrategy((SessionStrategy.PitStrategy)value);
                    _status = "Applied remote race strategy";
                }
                catch (Exception ex) { _status = "Remote strategy failed: " + ex.Message; }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && incoming.IndexOf("manual_save", StringComparison.Ordinal) >= 0 && _saveSystem != null)
            {
                _applyRemoteAction = true;
                try { _saveSystem.ManualSave(); _status = "Applied remote save"; }
                catch (Exception ex) { _status = "Remote save failed: " + ex.Message; }
                finally { _applyRemoteAction = false; }
            }
            if (!_window) return;

            GUILayout.BeginVertical("box", GUILayout.Width(360));
            GUILayout.Label("Motorsport Manager Coop - LAN prototype");
            GUILayout.Label("Host IP");
            _host = GUILayout.TextField(_host);
            GUILayout.Label("Port");
            _port = GUILayout.TextField(_port);
            GUILayout.Label("Name");
            _name = GUILayout.TextField(_name);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host")) StartHost();
            if (GUILayout.Button("Join")) Connect();
            if (GUILayout.Button("Disconnect")) Disconnect();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private static void Connect()
        {
            Disconnect();
            int port;
            if (!Int32.TryParse(_port, out port)) { _status = "Invalid port"; return; }
            try
            {
                _client = new TcpClient();
                _client.Connect(_host, port);
                _stream = _client.GetStream();
                byte[] hello = Encoding.UTF8.GetBytes(
                    "{\"type\":\"hello\",\"protocol\":0,\"name\":\"" + _name + "\"}\n");
                _stream.Write(hello, 0, hello.Length);
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();
                _status = "Connected to " + _host + ":" + port;
            }
            catch (Exception ex) { _status = "Connection failed: " + ex.Message; Disconnect(); }
        }

        private static void StartHost()
        {
            Disconnect();
            int port;
            if (!Int32.TryParse(_port, out port)) { _status = "Invalid port"; return; }
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _isHost = true;
                _status = "Hosting LAN on port " + port;
                new Thread(HostLoop) { IsBackground = true }.Start();
            }
            catch (Exception ex) { _status = "Host failed: " + ex.Message; }
        }

        private static void HostLoop()
        {
            try
            {
                while (_listener != null)
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    lock (_hostLock) _hostClients.Add(client);
                    new Thread(() => HostClientLoop(client)) { IsBackground = true }.Start();
                }
            }
            catch { }
        }

        private static void HostClientLoop(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[4096];
                StringBuilder text = new StringBuilder();
                while (client.Connected)
                {
                    int count = stream.Read(buffer, 0, buffer.Length);
                    if (count <= 0) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    string all = text.ToString(); int end;
                    while ((end = all.IndexOf('\n')) >= 0)
                    {
                        string line = all.Substring(0, end).Trim(); all = all.Substring(end + 1);
                        if (line.IndexOf("\"type\":\"hello\"", StringComparison.Ordinal) >= 0)
                            WritePacket(stream, Encoding.UTF8.GetBytes("{\"type\":\"welcome\",\"protocol\":0,\"role\":\"client\"}\n"));
                        else if (line.IndexOf("\"type\":\"resync_request\"", StringComparison.Ordinal) >= 0)
                        {
                            WritePacket(stream, Encoding.UTF8.GetBytes(
                                "{\"type\":\"resync_snapshot\",\"revision\":" + _hostRevision + "}\n"));
                            SendSaveSnapshot(stream);
                        }
                        else if (line.IndexOf("\"type\":\"action\"", StringComparison.Ordinal) >= 0)
                        {
                            int revision = Interlocked.Increment(ref _hostRevision);
                            string action = line.TrimEnd('}').TrimEnd() + ",\"revision\":" + revision + "}";
                            lock (_incomingLock) _incoming.Enqueue(action);
                            WritePacket(stream, Encoding.UTF8.GetBytes(
                                "{\"type\":\"action_ack\",\"revision\":" + revision + "}\n"));
                            Broadcast(Encoding.UTF8.GetBytes(action + "\n"), client);
                        }
                    }
                    text.Length = 0; text.Append(all);
                }
            }
            catch { }
            finally { lock (_hostLock) _hostClients.Remove(client); client.Close(); }
        }

        private static void SendPacket(byte[] packet)
        {
            if (_isHost) { Broadcast(packet, null); return; }
            if (_stream == null) return;
            _stream.Write(packet, 0, packet.Length);
        }

        private static void Broadcast(byte[] packet, TcpClient except)
        {
            lock (_hostLock)
                foreach (TcpClient client in _hostClients.ToArray())
                    if (client != except && client.Connected) try { WritePacket(client.GetStream(), packet); } catch { }
        }

        private static void WritePacket(NetworkStream stream, byte[] packet)
        { stream.Write(packet, 0, packet.Length); }

        private static void SendSaveSnapshot(NetworkStream stream)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Low\\Playsport Games\\Motorsport Manager\\Cloud\\Saves";
            string path = Path.Combine(dir, "SaveJohn Sina - Scuderia Rossini 7 Coop.sav");
            if (!File.Exists(path)) path = Path.Combine(dir, "SaveJohn Sina - Scuderia Rossini 7.sav");
            if (!File.Exists(path)) return;
            byte[] all = File.ReadAllBytes(path);
            WritePacket(stream, Encoding.UTF8.GetBytes("{\"type\":\"save_begin\",\"name\":\"" + Path.GetFileName(path) + "\",\"size\":" + all.Length + "}\n"));
            for (int offset = 0; offset < all.Length; offset += 6144)
            {
                int count = Math.Min(6144, all.Length - offset);
                string data = Convert.ToBase64String(all, offset, count);
                WritePacket(stream, Encoding.UTF8.GetBytes("{\"type\":\"save_chunk\",\"data\":\"" + data + "\"}\n"));
            }
            WritePacket(stream, Encoding.UTF8.GetBytes("{\"type\":\"save_end\"}\n"));
        }

        private static void ReceiveLoop()
        {
            byte[] buffer = new byte[4096];
            StringBuilder text = new StringBuilder();
            try
            {
                while (_stream != null && _stream.CanRead)
                {
                    int count = _stream.Read(buffer, 0, buffer.Length);
                    if (count <= 0) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, count));
                    string all = text.ToString();
                    int end;
                    while ((end = all.IndexOf('\n')) >= 0)
                    {
                        string line = all.Substring(0, end).Trim();
                        all = all.Substring(end + 1);
                        if (line.Length > 0) lock (_incomingLock) _incoming.Enqueue(line);
                    }
                    text.Length = 0;
                    text.Append(all);
                }
            }
            catch { }
        }

        private static void Disconnect()
        {
            if (_listener != null) { try { _listener.Stop(); } catch { } _listener = null; }
            lock (_hostLock) { foreach (TcpClient c in _hostClients) c.Close(); _hostClients.Clear(); }
            _isHost = false;
            if (_stream != null) _stream.Close();
            if (_client != null) _client.Close();
            if (_receiveThread != null && _receiveThread.IsAlive) _receiveThread.Join(100);
            _receiveThread = null;
            _stream = null;
            _client = null;
            if (_status.StartsWith("Connected")) _status = "Disconnected";
        }

        private static bool OnUnload(UnityModManager.ModEntry modEntry)
        {
            if (_harmony != null) _harmony.UnpatchAll("codex.motorsportmanager.coop");
            Disconnect();
            return true;
        }
    }
}
