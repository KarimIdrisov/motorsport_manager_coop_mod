using System;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Reflection;
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
        private static CarPartDesign _carDesign;
        private static HQsBuilding_v1 _hqBuilding;
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
        private static string _snapshotExpectedHash;
        private static bool _snapshotReady;
        private static TcpListener _listener;
        private static readonly List<TcpClient> _hostClients = new List<TcpClient>();
        private static readonly object _hostLock = new object();
        private static int _hostRevision;
        private static readonly Dictionary<int, Person> _peopleById = new Dictionary<int, Person>();
        private static bool _saveHooked;
        private static bool _authoritativeSaveInProgress;
        private static bool _autoLoadRequested;
        private static bool _introSkipped;

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
            modEntry.OnFixedGUI = OnGUI;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnToggle = OnToggle;
            modEntry.OnUnload = OnUnload;
            _harmony = new Harmony("codex.motorsportmanager.coop");
            PatchIntroScreen(typeof(AttractIntroScreen), "OnEnter");
            PatchIntroScreen(typeof(MovieScreen), "OnEnter");
            PatchIntroScreen(typeof(BaseMovieScreen), "OnStart");
            PatchIntroScreen(typeof(LegalScreen), "OnEnter");
            PatchIntroScreen(typeof(TitleLoadingScreen), "OnEnter");
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
            _harmony.Patch(AccessTools.Method(typeof(GameTimer), "SetSpeedDontUnpause"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSetSpeed)));
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "SetOrderedLapCount"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSetOrderedLapCount)));
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "SendOutOnTrack"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSendOutOnTrack)));
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "ReturnToGarage"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnReturnToGarage)));
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "Pit"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitCommand)));
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "CancelPit"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnCancelPit)));
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "ApplyQueueOrders"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnApplyQueueOrders)));
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "RemoveQueuedOrder"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnRemoveQueuedOrder)));
            _harmony.Patch(AccessTools.Method(typeof(CarPartDesign), "StartDesigning"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureCarDesign)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnStartDesigning)));
            _harmony.Patch(AccessTools.Method(typeof(CarPartDesign), "BuildTwoParts"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureCarDesign)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnBuildTwoParts)));
            _harmony.Patch(AccessTools.Method(typeof(CarPartDesign), "PartComplete"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPartComplete)));
            _harmony.Patch(AccessTools.Method(typeof(HQsBuilding_v1), "BeginBuilding"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureHQBuilding)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnBeginBuilding)));
            _harmony.Patch(AccessTools.Method(typeof(HQsBuilding_v1), "BeginUpgrade"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureHQBuilding)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnBeginUpgrade)));
            _harmony.Patch(AccessTools.Method(typeof(HQsBuilding_v1), "Build"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureHQBuilding)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnHQBuildComplete)));
            _harmony.Patch(AccessTools.Method(typeof(HQsBuilding_v1), "UpgradeBuilding"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureHQBuilding)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnHQUpgradeComplete)));
            _harmony.Patch(AccessTools.Method(typeof(ContractManagerTeam), "HireNewPerson"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnHirePerson)));
            _harmony.Patch(AccessTools.Method(typeof(ContractManagerTeam), "FirePerson"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnFirePerson)));
            _harmony.Patch(AccessTools.Method(typeof(ContractManagerTeam), "RenewContractForPerson"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnRenewPerson)));
            _harmony.Patch(AccessTools.Method(typeof(Finance), "ProcessTransaction"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnProcessTransaction)));
            _harmony.Patch(AccessTools.Method(typeof(ContractSponsor), "PayUpfrontSponsorship"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSponsorPayment)));
            _harmony.Patch(AccessTools.Method(typeof(PitCrewController), "AssignRoleToPitCrewMember"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitCrewAssign)));
            _harmony.Patch(AccessTools.Method(typeof(PitCrewController), "SwapActivePitCrewMembers"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitCrewSwap)));
            _harmony.Patch(AccessTools.Method(typeof(PitCrewController), "SignupPitCrewMember"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitCrewSignup)));
            _harmony.Patch(AccessTools.Method(typeof(PitCrewController), "FirePitCrewMember"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitCrewFire)));
            _harmony.Patch(AccessTools.Method(typeof(SimulationUtility), "SimulatePractice"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPracticeSimulated)));
            _harmony.Patch(AccessTools.Method(typeof(SimulationUtility), "SimulateQualifying"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnQualifyingSimulated)));
            _harmony.Patch(AccessTools.Method(typeof(SimulationUtility), "SimulateRace"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnRaceSimulated)));
            _harmony.Patch(AccessTools.Method(typeof(SessionSimulation), "SimulateNextSession"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSessionSimulated)));
            _harmony.Patch(AccessTools.Method(typeof(SessionSimulation), "SimulateEvent"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnEventSimulated)));
            _harmony.Patch(AccessTools.Method(typeof(Championship), "RecordChampionshipResult"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnChampionshipResult)));
            _harmony.Patch(AccessTools.Method(typeof(Championship), "OnSeasonStart"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSeasonStart)));
            _harmony.Patch(AccessTools.Method(typeof(Championship), "OnSeasonEnd"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSeasonEnd)));
            _harmony.Patch(AccessTools.Method(typeof(RaceEventResults), "PostSessionResults"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPostSessionResults)));
            _harmony.Patch(
                AccessTools.Method(typeof(SaveSystem), "ManualSave"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureSaveSystem)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnManualSave)));
            _harmony.Patch(
                AccessTools.Method(typeof(SaveSystem), "LoadSaveWithName"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureSaveSystem)));
            _harmony.Patch(
                AccessTools.Method(typeof(SaveSystem), "Load", new[] { typeof(SaveFileInfo), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureSaveSystem)));
            _harmony.Patch(AccessTools.Method(typeof(Game), "OnLoad"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnGameLoaded)));
            string autoRole = Environment.GetEnvironmentVariable("MM_COOP_AUTOSTART");
            if (String.Equals(autoRole, "host", StringComparison.OrdinalIgnoreCase))
            {
                StartHost();
            }
            else if (String.Equals(autoRole, "client", StringComparison.OrdinalIgnoreCase))
            {
                string autoHost = Environment.GetEnvironmentVariable("MM_COOP_HOST");
                if (!String.IsNullOrEmpty(autoHost)) _host = autoHost;
                new Thread(() => { Thread.Sleep(1500); Connect(); }) { IsBackground = true }.Start();
            }
            return true;
        }

        private static void PatchIntroScreen(Type type, string methodName)
        {
            try
            {
                if (type == null) return;
                MethodInfo method = AccessTools.Method(type, methodName);
                if (method == null) { Log("intro method missing type=" + type.Name + " method=" + methodName); return; }
                _harmony.Patch(method, postfix: new HarmonyMethod(typeof(Main), nameof(OnIntroScreenStarted)));
                Log("intro hook installed type=" + type.Name + " method=" + methodName);
            }
            catch (Exception ex) { Log("intro hook failed type=" + (type == null ? "-" : type.Name) + " method=" + methodName + " error=" + ex.Message); }
        }

        private static void OnIntroScreenStarted()
        {
            if (!IsAutoMode()) return;
            try
            {
                Application.LoadLevel("TitleScreen");
                Log("skipped intro screen via Harmony");
            }
            catch (Exception ex) { Log("intro Harmony skip failed=" + ex.Message); }
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

        private static void CaptureCarDesign(CarPartDesign __instance)
        {
            _carDesign = __instance;
        }

        private static void CaptureHQBuilding(HQsBuilding_v1 __instance)
        {
            _hqBuilding = __instance;
        }

        private static void OnBeginBuilding()
        {
            SendStrategyAction("hq_begin_build", 1);
            Log("observed kind=hq_begin_build");
        }

        private static void OnBeginUpgrade()
        {
            SendStrategyAction("hq_begin_upgrade", 1);
            Log("observed kind=hq_begin_upgrade");
        }

        private static void OnHQBuildComplete(bool __0)
        {
            PublishAuthoritativeSave("hq_build_complete");
            Log("observed kind=hq_build_complete success=" + __0);
        }

        private static void OnHQUpgradeComplete(bool __0)
        {
            PublishAuthoritativeSave("hq_upgrade_complete");
            Log("observed kind=hq_upgrade_complete success=" + __0);
        }

        private static void OnStartDesigning()
        {
            SendStrategyAction("car_design_start", 0);
            Log("observed kind=car_design_start");
        }

        private static void OnBuildTwoParts(int __0)
        {
            SendStrategyAction("car_build_two_parts", __0);
            Log("observed kind=car_build_two_parts value=" + __0);
        }

        private static void OnPartComplete()
        {
            PublishAuthoritativeSave("production_part_complete");
            Log("observed kind=production_part_complete");
        }

        private static int PersonId(Person person)
        {
            try
            {
                if (person == null) return -1;
                int id = person.GetPersonIndexInManager();
                if (id >= 0) _peopleById[id] = person;
                return id;
            }
            catch { return -1; }
        }

        private static Person PersonById(int id)
        {
            Person person;
            return _peopleById.TryGetValue(id, out person) ? person : null;
        }

        private static void OnHirePerson(Person __1)
        {
            int id = PersonId(__1);
            if (id >= 0) { SendStrategyAction("contract_hire", id); PublishAuthoritativeSave("contract_hire"); Log("observed kind=contract_hire personId=" + id + " registry=" + _peopleById.Count); }
        }

        private static void OnFirePerson(Person __0)
        {
            int id = PersonId(__0);
            if (id >= 0) { SendStrategyAction("contract_fire", id); PublishAuthoritativeSave("contract_fire"); Log("observed kind=contract_fire personId=" + id + " registry=" + _peopleById.Count); }
        }

        private static void OnRenewPerson(Person __0)
        {
            int id = PersonId(__0);
            if (id >= 0) { SendStrategyAction("contract_renew", id); PublishAuthoritativeSave("contract_renew"); Log("observed kind=contract_renew personId=" + id + " registry=" + _peopleById.Count); }
        }

        private static void OnProcessTransaction(Transaction __0)
        {
            if (__0 == null) return;
            PublishAuthoritativeSave("finance_transaction");
            Log("observed kind=finance_transaction amount=" + __0.amount + " balance=" + __0.fundsAfterTransaction + " group=" + __0.group);
        }

        private static void OnSponsorPayment(bool __0)
        {
            if (__0) { SendStrategyAction("sponsor_payment", 1); PublishAuthoritativeSave("sponsor_payment"); }
            Log("observed kind=sponsor_upfront_payment accepted=" + __0);
        }

        private static void PublishAuthoritativeSave(string reason)
        {
            if (!_isHost || _applyRemoteAction || _authoritativeSaveInProgress) return;
            EnsureSaveSystem();
            if (_saveSystem == null) { Log("state_dirty reason=" + reason + " save_system=unavailable"); return; }
            try
            {
                _authoritativeSaveInProgress = true;
                _saveSystem.ManualSaveAs(Path.GetFileNameWithoutExtension(_snapshotSaveName));
                Log("state_dirty reason=" + reason + " authoritative_save=requested");
            }
            catch (Exception ex) { Log("authoritative_save failed reason=" + reason + " error=" + ex.Message); }
            finally { _authoritativeSaveInProgress = false; }
        }

        private static void EnsureSaveSystem()
        {
            if (_saveSystem != null || Game.instance == null) return;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (FieldInfo field in typeof(Game).GetFields(flags))
                {
                    if (field.FieldType != typeof(SaveSystem)) continue;
                    object owner = field.IsStatic ? null : (object)Game.instance;
                    CaptureSaveSystem((SaveSystem)field.GetValue(owner));
                    if (_saveSystem != null) return;
                }
                foreach (PropertyInfo property in typeof(Game).GetProperties(flags))
                {
                    if (property.PropertyType != typeof(SaveSystem) || !property.CanRead) continue;
                    MethodInfo getter = property.GetGetMethod(true);
                    object owner = getter != null && getter.IsStatic ? null : (object)Game.instance;
                    CaptureSaveSystem((SaveSystem)property.GetValue(owner, null));
                    if (_saveSystem != null) return;
                }
            }
            catch (Exception ex) { Log("save system discovery failed=" + ex.Message); }
        }

        private static void OnSaveComplete()
        {
            if (!_isHost) return;
            Log("authoritative_save completed; broadcasting snapshot");
            lock (_hostLock)
                foreach (TcpClient client in _hostClients.ToArray())
                    if (client.Connected) try { SendSaveSnapshot(client.GetStream()); } catch { }
        }

        private static void OnPitCrewAssign(PitCrewMember __0, PitCrewMember __1)
        {
            PublishAuthoritativeSave("pitcrew_assign");
            Log("observed kind=pitcrew_assign members=" + (__0 == null ? "-" : __0.name) + "," + (__1 == null ? "-" : __1.name));
        }

        private static void OnPitCrewSwap(PitCrewMember __0, PitCrewMember __1)
        {
            PublishAuthoritativeSave("pitcrew_swap");
            Log("observed kind=pitcrew_swap members=" + (__0 == null ? "-" : __0.name) + "," + (__1 == null ? "-" : __1.name));
        }

        private static void OnPitCrewSignup(PitCrewMember __0)
        {
            PublishAuthoritativeSave("pitcrew_signup");
            Log("observed kind=pitcrew_signup member=" + (__0 == null ? "-" : __0.name));
        }

        private static void OnPitCrewFire(PitCrewMember __0)
        {
            PublishAuthoritativeSave("pitcrew_fire");
            Log("observed kind=pitcrew_fire member=" + (__0 == null ? "-" : __0.name));
        }

        private static void OnPracticeSimulated()
        {
            PublishAuthoritativeSave("practice_complete");
            Log("observed kind=practice_complete");
        }

        private static void OnQualifyingSimulated()
        {
            PublishAuthoritativeSave("qualifying_complete");
            Log("observed kind=qualifying_complete");
        }

        private static void OnRaceSimulated()
        {
            PublishAuthoritativeSave("race_complete");
            Log("observed kind=race_complete");
        }

        private static void OnSessionSimulated()
        {
            PublishAuthoritativeSave("session_complete");
            Log("observed kind=session_complete");
        }

        private static void OnEventSimulated()
        {
            PublishAuthoritativeSave("event_complete");
            Log("observed kind=event_complete");
        }

        private static void OnChampionshipResult()
        {
            PublishAuthoritativeSave("championship_result");
            Log("observed kind=championship_result");
        }

        private static void OnSeasonStart()
        {
            PublishAuthoritativeSave("season_start");
            Log("observed kind=season_start");
        }

        private static void OnSeasonEnd()
        {
            PublishAuthoritativeSave("season_end");
            Log("observed kind=season_end");
        }

        private static void OnPostSessionResults()
        {
            PublishAuthoritativeSave("session_results");
            Log("observed kind=session_results");
        }

        private static void OnGameLoaded()
        {
            Log("game loaded career=" + (Game.instance != null && Game.instance.isCareer));
            Log("game managers team=" + (Game.instance != null && Game.instance.teamManager != null) +
                " championship=" + (Game.instance != null && Game.instance.championshipManager != null) +
                " pitCrew=" + (Game.instance != null && Game.instance.pitCrewManager != null));
        }

        private static void CaptureSaveSystem(SaveSystem __instance)
        {
            _saveSystem = __instance;
            if (!_saveHooked)
            {
                _saveHooked = true;
                _saveSystem.OnSaveComplete += OnSaveComplete;
            }
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

        private static void OnSetSpeed(GameTimer.Speed __0)
        {
            SendStrategyAction("simulation_speed", (int)__0);
            Log("observed kind=simulation_speed value=" + (int)__0);
        }

        private static void OnSetOrderedLapCount(int __0)
        {
            SendStrategyAction("ordered_lap_count", __0);
            Log("observed kind=ordered_lap_count value=" + __0);
        }

        private static void OnSendOutOnTrack() { SendStrategyAction("send_out_on_track", 0); }
        private static void OnReturnToGarage() { SendStrategyAction("return_to_garage", 0); }
        private static void OnPitCommand() { SendStrategyAction("pit_command", 0); }
        private static void OnCancelPit() { SendStrategyAction("cancel_pit", 0); }
        private static void OnApplyQueueOrders() { SendStrategyAction("apply_queue_orders", 0); }
        private static void OnRemoveQueuedOrder() { SendStrategyAction("remove_queued_order", 0); }

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
            EnsureSaveSystem();
            if (_snapshotReady && _saveSystem != null)
            {
                _snapshotReady = false;
                try
                {
                    _applyRemoteAction = true;
                    var method = typeof(SaveSystem).GetMethod("LoadSaveWithName");
                    if (method != null) method.Invoke(_saveSystem, new object[] { Path.GetFileNameWithoutExtension(_snapshotSaveName) });
                    _status = "Host save received and load requested";
                    Log("received save snapshot name=" + _snapshotSaveName);
                }
                catch (Exception ex) { _status = "Host save received; load failed: " + ex.Message; Log("snapshot load failed=" + ex.Message); }
                finally { _applyRemoteAction = false; }
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("LAN Coop", GUILayout.Width(100))) _window = !_window;
            GUILayout.Label(_status, GUILayout.Width(220));
            GUILayout.EndHorizontal();
            string incoming = null;
            lock (_incomingLock) if (_incoming.Count > 0) incoming = _incoming.Dequeue();
            if (incoming != null && incoming.IndexOf("\"type\":\"welcome\"", StringComparison.Ordinal) >= 0)
            {
                Log("processed welcome packet");
                _isHost = incoming.IndexOf("\"role\":\"host\"", StringComparison.Ordinal) >= 0;
                _status = _isHost ? "Connected as host" : "Connected as client";
            }
            if (incoming != null && incoming.IndexOf("host_changed", StringComparison.Ordinal) >= 0)
                _status = "Host changed; reconnect required for role refresh";
            if (incoming != null && incoming.IndexOf("action_ack", StringComparison.Ordinal) >= 0)
                _status = "Host confirmed action";
            if (incoming != null && incoming.IndexOf("\"type\":\"action\"", StringComparison.Ordinal) >= 0)
            {
                Log("received broadcast action revision=" + ReadRevision(incoming));
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
                Log("received resync snapshot revision=" + _lastRevision);
            }
            if (incoming != null && incoming.IndexOf("\"type\":\"save_begin\"", StringComparison.Ordinal) >= 0)
            {
                Match nameMatch = Regex.Match(incoming, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                string saveName = nameMatch.Success ? nameMatch.Groups[1].Value : _snapshotSaveName;
                _snapshotSaveName = saveName;
                Match hashMatch = Regex.Match(incoming, "\\\"sha256\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                _snapshotExpectedHash = hashMatch.Success ? hashMatch.Groups[1].Value : null;
                string dir = SaveDirectory();
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
                string actualHash = ComputeSha256(_snapshotTemp);
                if (!String.IsNullOrEmpty(_snapshotExpectedHash) &&
                    !String.Equals(actualHash, _snapshotExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _status = "Snapshot checksum mismatch";
                    Log("resync rejected checksum expected=" + _snapshotExpectedHash + " actual=" + actualHash);
                    try { File.Delete(_snapshotTemp); } catch { }
                    _snapshotExpectedHash = null;
                    return;
                }
                Log("resync checksum verified sha256=" + actualHash);
                if (File.Exists(_snapshotTarget)) File.Copy(_snapshotTarget, _snapshotTarget + ".backup", true);
                File.Copy(_snapshotTemp, _snapshotTarget, true); File.Delete(_snapshotTemp);
                Log("resync file replaced target=" + _snapshotTarget);
                try
                {
                    var method = typeof(SaveSystem).GetMethod("LoadSaveWithName");
                    if (method != null) method.Invoke(_saveSystem, new object[] { Path.GetFileNameWithoutExtension(_snapshotSaveName) });
                    _status = "Host save received and load requested";
                    Log("received save snapshot name=" + _snapshotSaveName);
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
                 incoming.IndexOf("pit_strategy", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("ordered_lap_count", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("send_out_on_track", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("return_to_garage", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("pit_command", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("cancel_pit", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("apply_queue_orders", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("remove_queued_order", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    int value = ReadActionValue(incoming);
                    if (incoming.IndexOf("team_orders", StringComparison.Ordinal) >= 0)
                        _strategy.SetTeamOrders((SessionStrategy.TeamOrders)value);
                    else if (incoming.IndexOf("pit_strategy", StringComparison.Ordinal) >= 0)
                        _strategy.SetPitStrategy((SessionStrategy.PitStrategy)value);
                    else if (incoming.IndexOf("ordered_lap_count", StringComparison.Ordinal) >= 0)
                        _strategy.SetOrderedLapCount(value);
                    else if (incoming.IndexOf("send_out_on_track", StringComparison.Ordinal) >= 0)
                        _strategy.SendOutOnTrack();
                    else if (incoming.IndexOf("return_to_garage", StringComparison.Ordinal) >= 0)
                        _strategy.ReturnToGarage();
                    else if (incoming.IndexOf("pit_command", StringComparison.Ordinal) >= 0)
                        _strategy.Pit();
                    else if (incoming.IndexOf("cancel_pit", StringComparison.Ordinal) >= 0)
                        _strategy.CancelPit();
                    else if (incoming.IndexOf("apply_queue_orders", StringComparison.Ordinal) >= 0)
                        _strategy.ApplyQueueOrders();
                    else
                        _strategy.RemoveQueuedOrder();
                    _status = "Applied remote race strategy";
                }
                catch (Exception ex) { _status = "Remote strategy failed: " + ex.Message; }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && incoming.IndexOf("simulation_speed", StringComparison.Ordinal) >= 0 && _timer != null)
            {
                _applyRemoteAction = true;
                try { _timer.SetSpeedDontUnpause((GameTimer.Speed)ReadActionValue(incoming)); _status = "Applied remote simulation speed"; }
                catch (Exception ex) { _status = "Remote speed failed: " + ex.Message; }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && _carDesign != null &&
                (incoming.IndexOf("car_design_start", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("car_build_two_parts", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    if (incoming.IndexOf("car_design_start", StringComparison.Ordinal) >= 0)
                        _carDesign.StartDesigning();
                    else
                        _carDesign.BuildTwoParts(ReadActionValue(incoming));
                    _status = "Applied remote car design action";
                }
                catch (Exception ex) { _status = "Remote car design failed: " + ex.Message; }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && _hqBuilding != null &&
                (incoming.IndexOf("hq_begin_build", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("hq_begin_upgrade", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    if (incoming.IndexOf("hq_begin_build", StringComparison.Ordinal) >= 0)
                        _hqBuilding.BeginBuilding();
                    else
                        _hqBuilding.BeginUpgrade();
                    _status = "Applied remote HQ action";
                }
                catch (Exception ex) { _status = "Remote HQ action failed: " + ex.Message; }
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

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            if (!_enabled) return;
            SkipIntroScreens();
            EnsureSaveSystem();
            if (!_autoLoadRequested && IsAutoMode() && _saveSystem != null && Game.instance != null)
            {
                _autoLoadRequested = true;
                try
                {
                    _applyRemoteAction = true;
                    _saveSystem.LoadSaveWithName(Path.GetFileNameWithoutExtension(_snapshotSaveName));
                    _status = "Loading shared career save";
                    Log("automatic shared save load requested name=" + _snapshotSaveName);
                }
                catch (Exception ex) { _autoLoadRequested = false; Log("automatic shared save load failed=" + ex.Message); }
                finally { _applyRemoteAction = false; }
            }
            if (!_snapshotReady) return;
            if (_saveSystem == null) return;
            _snapshotReady = false;
            try
            {
                _applyRemoteAction = true;
                var method = typeof(SaveSystem).GetMethod("LoadSaveWithName");
                if (method == null) throw new MissingMethodException("SaveSystem.LoadSaveWithName");
                method.Invoke(_saveSystem, new object[] { Path.GetFileNameWithoutExtension(_snapshotSaveName) });
                _status = "Host save received and load requested";
                Log("received save snapshot name=" + _snapshotSaveName);
            }
            catch (Exception ex) { _status = "Host save received; load failed: " + ex.Message; Log("snapshot load failed=" + ex.Message); }
            finally { _applyRemoteAction = false; }
        }

        private static bool IsAutoMode()
        {
            string role = Environment.GetEnvironmentVariable("MM_COOP_AUTOSTART");
            return String.Equals(role, "host", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(role, "client", StringComparison.OrdinalIgnoreCase);
        }

        private static void SkipIntroScreens()
        {
            if (!IsAutoMode() || _introSkipped) return;
            try
            {
                string scene = Application.loadedLevelName;
                if (scene == "AttractIntroScreen" || scene == "MovieScreen" ||
                    scene == "LegalScreen" || scene == "TitleLoadingScreen")
                {
                    _introSkipped = true;
                    Application.LoadLevel("TitleScreen");
                    Log("skipped intro scene=" + scene);
                }
            }
            catch (Exception ex) { Log("intro skip failed=" + ex.Message); }
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
                if (String.Equals(Environment.GetEnvironmentVariable("MM_COOP_AUTOSYNC"), "1", StringComparison.Ordinal))
                {
                    byte[] request = Encoding.UTF8.GetBytes("{\"type\":\"resync_request\"}\n");
                    _stream.Write(request, 0, request.Length);
                    Log("client requested automatic resync");
                }
                string autoAction = Environment.GetEnvironmentVariable("MM_COOP_AUTOACTION");
                if (!String.IsNullOrEmpty(autoAction))
                {
                    byte[] action = Encoding.UTF8.GetBytes("{\"type\":\"action\",\"id\":\"auto-test\",\"kind\":\"" + autoAction + "\",\"value\":2}\n");
                    _stream.Write(action, 0, action.Length);
                    Log("client sent automatic action kind=" + autoAction);
                }
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();
                _status = "Connected to " + _host + ":" + port;
                Log("client connected host=" + _host + " port=" + port);
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
                string autoAction = Environment.GetEnvironmentVariable("MM_COOP_AUTOACTION");
                if (!String.IsNullOrEmpty(autoAction))
                    new Thread(() => { Thread.Sleep(2500); SendStrategyAction(autoAction, 2); }) { IsBackground = true }.Start();
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
                        {
                            WritePacket(stream, Encoding.UTF8.GetBytes("{\"type\":\"welcome\",\"protocol\":0,\"role\":\"client\"}\n"));
                            Log("peer handshake accepted");
                        }
                        else if (line.IndexOf("\"type\":\"resync_request\"", StringComparison.Ordinal) >= 0)
                        {
                            Log("resync request received");
                            WritePacket(stream, Encoding.UTF8.GetBytes(
                                "{\"type\":\"resync_snapshot\",\"revision\":" + _hostRevision + "}\n"));
                            SendSaveSnapshot(stream);
                        }
                        else if (line.IndexOf("\"type\":\"action\"", StringComparison.Ordinal) >= 0)
                        {
                            Match kindMatch = Regex.Match(line, "\\\"kind\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                            Log("received action kind=" + (kindMatch.Success ? kindMatch.Groups[1].Value : "unknown"));
                            int revision = Interlocked.Increment(ref _hostRevision);
                            int outerEnd = line.LastIndexOf('}');
                            string action = outerEnd >= 0
                                ? line.Substring(0, outerEnd) + ",\"revision\":" + revision + "}"
                                : line;
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
            if (_isHost)
            {
                string line = Encoding.UTF8.GetString(packet).Trim();
                if (line.IndexOf("\"type\":\"action\"", StringComparison.Ordinal) >= 0)
                {
                    int revision = Interlocked.Increment(ref _hostRevision);
                    int outerEnd = line.LastIndexOf('}');
                    if (outerEnd >= 0)
                        line = line.Substring(0, outerEnd) + ",\"revision\":" + revision + "}";
                    packet = Encoding.UTF8.GetBytes(line + "\n");
                    Log("host broadcast action revision=" + revision);
                }
                Broadcast(packet, null);
                return;
            }
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

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(input);
                StringBuilder result = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) result.Append(hash[i].ToString("x2"));
                return result.ToString();
            }
        }

        private static string SaveDirectory()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localLow = Path.Combine(Directory.GetParent(local).FullName, "LocalLow");
            return Path.Combine(Path.Combine(Path.Combine(Path.Combine(localLow, "Playsport Games"), "Motorsport Manager"), "Cloud"), "Saves");
        }

        private static void SendSaveSnapshot(NetworkStream stream)
        {
            string dir = SaveDirectory();
            string path = Path.Combine(dir, "SaveJohn Sina - Scuderia Rossini 7 Coop.sav");
            if (!File.Exists(path)) path = Path.Combine(dir, "SaveJohn Sina - Scuderia Rossini 7.sav");
            if (!File.Exists(path)) { Log("resync snapshot unavailable path=" + path); return; }
            Log("sending resync snapshot path=" + path);
            byte[] all = File.ReadAllBytes(path);
            string hash = ComputeSha256(path);
            WritePacket(stream, Encoding.UTF8.GetBytes("{\"type\":\"save_begin\",\"name\":\"" + Path.GetFileName(path) + "\",\"size\":" + all.Length + ",\"sha256\":\"" + hash + "\"}\n"));
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
                        if (line.Length > 0 && (line.IndexOf("\"type\":\"save_begin\"", StringComparison.Ordinal) >= 0 ||
                            line.IndexOf("\"type\":\"save_chunk\"", StringComparison.Ordinal) >= 0 ||
                            line.IndexOf("\"type\":\"save_end\"", StringComparison.Ordinal) >= 0))
                        {
                            ProcessSnapshotPacket(line);
                        }
                        else if (line.Length > 0)
                        {
                            Log("network packet received=" + line.Substring(0, Math.Min(line.Length, 96)));
                            lock (_incomingLock) _incoming.Enqueue(line);
                        }
                    }
                    text.Length = 0;
                    text.Append(all);
                }
            }
            catch { }
        }

        private static void ProcessSnapshotPacket(string incoming)
        {
            try
            {
                if (incoming.IndexOf("\"type\":\"save_begin\"", StringComparison.Ordinal) >= 0)
                {
                    Match nameMatch = Regex.Match(incoming, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                    Match hashMatch = Regex.Match(incoming, "\\\"sha256\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                    _snapshotSaveName = nameMatch.Success ? nameMatch.Groups[1].Value : _snapshotSaveName;
                    _snapshotExpectedHash = hashMatch.Success ? hashMatch.Groups[1].Value : null;
                    string dir = SaveDirectory();
                    Directory.CreateDirectory(dir);
                    _snapshotTarget = Path.Combine(dir, _snapshotSaveName);
                    _snapshotTemp = _snapshotTarget + ".coop.tmp";
                    if (_snapshotFile != null) _snapshotFile.Close();
                    _snapshotFile = new FileStream(_snapshotTemp, FileMode.Create, FileAccess.Write, FileShare.None);
                }
                else if (incoming.IndexOf("\"type\":\"save_chunk\"", StringComparison.Ordinal) >= 0 && _snapshotFile != null)
                {
                    Match chunk = Regex.Match(incoming, "\\\"data\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"");
                    if (chunk.Success) { byte[] data = Convert.FromBase64String(chunk.Groups[1].Value); _snapshotFile.Write(data, 0, data.Length); }
                }
                else if (incoming.IndexOf("\"type\":\"save_end\"", StringComparison.Ordinal) >= 0 && _snapshotFile != null)
                {
                    _snapshotFile.Close(); _snapshotFile = null;
                    string actualHash = ComputeSha256(_snapshotTemp);
                    if (!String.IsNullOrEmpty(_snapshotExpectedHash) && !String.Equals(actualHash, _snapshotExpectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("resync rejected checksum expected=" + _snapshotExpectedHash + " actual=" + actualHash);
                        File.Delete(_snapshotTemp); return;
                    }
                    Log("resync checksum verified sha256=" + actualHash);
                    if (File.Exists(_snapshotTarget)) File.Copy(_snapshotTarget, _snapshotTarget + ".backup", true);
                    File.Copy(_snapshotTemp, _snapshotTarget, true); File.Delete(_snapshotTemp);
                    Log("resync file replaced target=" + _snapshotTarget);
                    _snapshotReady = true;
                }
            }
            catch (Exception ex) { Log("snapshot receive failed=" + ex.Message); }
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
