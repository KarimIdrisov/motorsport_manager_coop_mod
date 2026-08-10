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
        private static GameStateManager _gameStateManager;
        private static Thread _receiveThread;
        private static readonly Queue<string> _incoming = new Queue<string>();
        private static readonly Queue<string> _deferredRaceActions = new Queue<string>();
        private static readonly Queue<string> _deferredSessionLoads = new Queue<string>();
        private static readonly object _incomingLock = new object();
        private static bool _applyRemoteAction;
        private static bool _isHost;
        private static int _lastRevision;
        private static FileStream _snapshotFile;
        private static string _snapshotTemp;
        private static string _snapshotTarget;
        private static string _snapshotSaveName = "SaveJohn Sina - Scuderia Rossini 7 Coop.sav";
        private static string _snapshotExpectedHash;
        private static int _snapshotLoadPending;
        private static bool _snapshotLoadInProgress;
        private static bool _initialSyncComplete;
        private static string _pendingSnapshotHash;
        private static string _lastAcceptedSnapshotHash;
        private static TcpListener _listener;
        private static readonly List<TcpClient> _hostClients = new List<TcpClient>();
        private static readonly object _hostLock = new object();
        private static int _hostRevision;
        private static readonly Dictionary<int, Person> _peopleById = new Dictionary<int, Person>();
        private static readonly Dictionary<int, SessionStrategy> _strategiesByVehicle = new Dictionary<int, SessionStrategy>();
        private static readonly Dictionary<int, DrivingStyle> _drivingStylesByVehicle = new Dictionary<int, DrivingStyle>();
        private static readonly Dictionary<int, Fuel> _fuelByVehicle = new Dictionary<int, Fuel>();
        private static readonly Dictionary<int, ERSController> _ersByVehicle = new Dictionary<int, ERSController>();
        private static readonly Dictionary<int, SessionPitstop> _pitstopsByVehicle = new Dictionary<int, SessionPitstop>();
        private static readonly Dictionary<int, SessionSetup> _setupsByVehicle = new Dictionary<int, SessionSetup>();
        private static readonly HashSet<int> _remoteSetupInitialized = new HashSet<int>();
        private static readonly HashSet<int> _pendingSendOut = new HashSet<int>();
        private static bool _saveHooked;
        private static bool _authoritativeSaveInProgress;
        private static bool _gameReady;
        private static bool _stateDirty;
        private static string _stateDirtyReason;
        private static float _stateDirtyAt;
        private static float _suppressSavesUntil;
        private static bool _autoLoadRequested;
        private static float _autoLoadElapsed;
        private static bool _newCareerOpened;
        private static bool _attractIntroContinued;
        private static bool _movieIntroContinued;
        private static bool _raceRuntimeReady;
        private static float _telemetryElapsed;
        private static float _telemetryLogElapsed;
        private static volatile byte[] _lastTelemetryPacket;
        private static bool _remotePaused;

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
            PatchIntroScreen(typeof(AttractIntroScreen), "Update");
            PatchIntroScreen(typeof(BaseMovieScreen), "Update");
            _harmony.Patch(AccessTools.Method(typeof(QualitySelectScreen), "OnEnter"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnQualityScreenEntered)));
            _harmony.Patch(AccessTools.Method(typeof(TitleScreen), "OnEnter"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnTitleScreenEntered)));
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
                AccessTools.Method(typeof(GameStateManager), "LoadToRaceEvent"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureGameStateManager)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnLoadToRaceEvent)));
            _harmony.Patch(AccessTools.Method(typeof(SessionManager), "StartSession"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnRaceRuntimeReady)));
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "Start"),
                postfix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)));
            _harmony.Patch(
                AccessTools.Method(typeof(SessionStrategy), "SetTeamOrders"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSetTeamOrders)));
            _harmony.Patch(
                AccessTools.Method(typeof(SessionStrategy), "SetPitStrategy"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSetPitStrategy)));
            _harmony.Patch(AccessTools.Method(typeof(GameTimer), "SetSpeedDontUnpause"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureTimer)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSetSpeed)));
            _harmony.Patch(AccessTools.Method(typeof(GameTimer), "SetSpeed"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureTimer)),
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
            _harmony.Patch(AccessTools.Method(typeof(SessionStrategy), "UpdateDrivingStyleAndEngineModes"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureStrategy)));
            _harmony.Patch(AccessTools.Method(typeof(DrivingStyle), "SetDrivingStyle"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnDrivingStyleChanged)));
            _harmony.Patch(AccessTools.Method(typeof(Fuel), "SetEngineMode"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnEngineModeChanged)));
            _harmony.Patch(AccessTools.Method(typeof(ERSController), "SetERSMode"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnERSModeChanged)));
            _harmony.Patch(AccessTools.Method(typeof(SessionPitstop), "SetTargetFuelLevel"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitFuelChanged)));
            _harmony.Patch(AccessTools.Method(typeof(SessionPitstop), "SetRepairParts"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitRepairChanged)));
            _harmony.Patch(AccessTools.Method(typeof(SessionPitstop), "SetTargetBatteryCharge"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitBatteryChanged)));
            _harmony.Patch(AccessTools.Method(typeof(SessionPitstop), "SetTargetTyres"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitTyresChanged)));
            _harmony.Patch(AccessTools.Method(typeof(SessionPitstop), "ResetPitStopSetup"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CapturePitstop)));
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
                prefix: new HarmonyMethod(typeof(Main), nameof(HostOnlyCareerAction)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnHirePerson)));
            _harmony.Patch(AccessTools.Method(typeof(ContractManagerTeam), "FirePerson"),
                prefix: new HarmonyMethod(typeof(Main), nameof(HostOnlyCareerAction)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnFirePerson)));
            _harmony.Patch(AccessTools.Method(typeof(ContractManagerTeam), "RenewContractForPerson"),
                prefix: new HarmonyMethod(typeof(Main), nameof(HostOnlyCareerAction)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnRenewPerson)));
            _harmony.Patch(AccessTools.Method(typeof(Finance), "ProcessTransaction"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnProcessTransaction)));
            _harmony.Patch(AccessTools.Method(typeof(ContractSponsor), "PayUpfrontSponsorship"),
                prefix: new HarmonyMethod(typeof(Main), nameof(HostOnlyCareerAction)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnSponsorPayment)));
            _harmony.Patch(AccessTools.Method(typeof(PitCrewController), "AssignRoleToPitCrewMember"),
                prefix: new HarmonyMethod(typeof(Main), nameof(HostOnlyCareerAction)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitCrewAssign)));
            _harmony.Patch(AccessTools.Method(typeof(PitCrewController), "SwapActivePitCrewMembers"),
                prefix: new HarmonyMethod(typeof(Main), nameof(HostOnlyCareerAction)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitCrewSwap)));
            _harmony.Patch(AccessTools.Method(typeof(PitCrewController), "SignupPitCrewMember"),
                prefix: new HarmonyMethod(typeof(Main), nameof(HostOnlyCareerAction)),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnPitCrewSignup)));
            _harmony.Patch(AccessTools.Method(typeof(PitCrewController), "FirePitCrewMember"),
                prefix: new HarmonyMethod(typeof(Main), nameof(HostOnlyCareerAction)),
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
                AccessTools.Method(typeof(SaveSystem), "ManualSaveAs"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureSaveName)));
            _harmony.Patch(
                AccessTools.Method(typeof(SaveSystem), "LoadSaveWithName"),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureSaveSystemForLoad)));
            _harmony.Patch(
                AccessTools.Method(typeof(SaveSystem), "Load", new[] { typeof(SaveFileInfo), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(Main), nameof(CaptureSaveSystemForLoad)));
            _harmony.Patch(AccessTools.Method(typeof(Game), "OnLoad"),
                postfix: new HarmonyMethod(typeof(Main), nameof(OnGameLoaded)));
            string autoRole = Environment.GetEnvironmentVariable("MM_COOP_AUTOSTART");
            if (String.Equals(autoRole, "host", StringComparison.OrdinalIgnoreCase))
            {
                string newestSave = FindNewestSavePath();
                if (!String.IsNullOrEmpty(newestSave)) _snapshotSaveName = Path.GetFileName(newestSave);
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

        private static void OnIntroScreenStarted(object __instance)
        {
            if (!IsAutoMode()) return;
            Type type = __instance == null ? null : __instance.GetType();
            if (type != typeof(AttractIntroScreen) && type != typeof(MovieScreen)) return;
            if (type == typeof(AttractIntroScreen))
            {
                if (_attractIntroContinued) return;
                _attractIntroContinued = true;
            }
            else
            {
                if (_movieIntroContinued) return;
                _movieIntroContinued = true;
            }
            try
            {
                MethodInfo continueMethod = AccessTools.Method(type, "Continue");
                if (continueMethod == null) return;
                continueMethod.Invoke(__instance, null);
                Log("continued startup screen from Update type=" + type.Name);
            }
            catch (Exception ex) { Log("startup screen continue failed=" + ex.Message); }
        }

        private static void OnQualityScreenEntered(QualitySelectScreen __instance)
        {
            if (!IsAutoMode()) return;
            try
            {
                MethodInfo recommended = AccessTools.Method(typeof(QualitySelectScreen), "SetRecommendedMode");
                if (recommended != null) recommended.Invoke(__instance, null);
                __instance.OnContinueButton();
                Log("continued quality selection via game screen");
            }
            catch (Exception ex) { Log("quality screen continue failed=" + ex.Message); }
        }

        private static void OnTitleScreenEntered(TitleScreen __instance)
        {
            if (!IsNewCareerMode() || _newCareerOpened) return;
            try
            {
                _newCareerOpened = true;
                MethodInfo method = AccessTools.Method(typeof(TitleScreen), "OnNewCareerButton");
                if (method == null) throw new MissingMethodException("TitleScreen.OnNewCareerButton");
                method.Invoke(__instance, null);
                Log("opened new career wizard for coop test");
            }
            catch (Exception ex) { _newCareerOpened = false; Log("new career wizard failed=" + ex.Message); }
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

        private static void CaptureGameStateManager(GameStateManager __instance)
        {
            _gameStateManager = __instance;
            _raceRuntimeReady = false;
        }

        private static void OnLoadToRaceEvent(GameStateManager.StateChangeType __0)
        {
            if (_applyRemoteAction || (_stream == null && !_isHost)) return;
            SendRaceAction("load_race_event", -1, (int)__0);
            Log("sent race event load state=" + __0);
        }

        private static void OnRaceRuntimeReady()
        {
            _raceRuntimeReady = true;
            Log("race runtime ready deferred=" + _deferredRaceActions.Count);
        }

        private static void CaptureStrategy(SessionStrategy __instance)
        {
            _strategy = __instance;
            int vehicleId = VehicleIdFromComponent(__instance);
            if (vehicleId < 0) return;
            _strategiesByVehicle[vehicleId] = __instance;
            try
            {
                RacingVehicle vehicle = VehicleFromComponent(__instance);
                if (vehicle == null) return;
                FieldInfo ersField = AccessTools.Field(typeof(RacingVehicle), "mERSController");
                ERSController ers = ersField == null ? null : ersField.GetValue(vehicle) as ERSController;
                if (ers != null) _ersByVehicle[vehicleId] = ers;
                FieldInfo performanceField = AccessTools.Field(typeof(RacingVehicle), "mPerformance");
                object performance = performanceField == null ? null : performanceField.GetValue(vehicle);
                if (performance == null) return;
                FieldInfo drivingField = AccessTools.Field(performance.GetType(), "mDrivingStyle");
                FieldInfo fuelField = AccessTools.Field(performance.GetType(), "mFuel");
                DrivingStyle driving = drivingField == null ? null : drivingField.GetValue(performance) as DrivingStyle;
                Fuel fuel = fuelField == null ? null : fuelField.GetValue(performance) as Fuel;
                if (driving != null) _drivingStylesByVehicle[vehicleId] = driving;
                if (fuel != null) _fuelByVehicle[vehicleId] = fuel;
                object setup = ReadObject(vehicle, "setup", "mSetup");
                SessionSetup sessionSetup = setup as SessionSetup;
                if (sessionSetup != null) _setupsByVehicle[vehicleId] = sessionSetup;
                SessionPitstop pitstop = ReadObject(setup, "mSessionPitStop", "sessionPitStop") as SessionPitstop;
                if (pitstop != null) _pitstopsByVehicle[vehicleId] = pitstop;
            }
            catch { }
        }

        private static bool CaptureCarDesign(CarPartDesign __instance)
        {
            _carDesign = __instance;
            return HostOnlyCareerAction();
        }

        private static bool CaptureHQBuilding(HQsBuilding_v1 __instance)
        {
            _hqBuilding = __instance;
            return HostOnlyCareerAction();
        }

        private static bool HostOnlyCareerAction()
        {
            bool allowed = _isHost || _stream == null;
            if (!allowed)
            {
                _status = "This career action is Host-only";
                Log("blocked Host-only career action on client");
            }
            return allowed;
        }

        private static int VehicleIdFromComponent(object component)
        {
            RacingVehicle vehicle = VehicleFromComponent(component);
            return vehicle == null ? -1 : vehicle.id;
        }

        private static RacingVehicle VehicleFromComponent(object component)
        {
            if (component == null) return null;
            try
            {
                FieldInfo vehicleField = AccessTools.Field(component.GetType(), "mVehicle");
                return vehicleField == null ? null : vehicleField.GetValue(component) as RacingVehicle;
            }
            catch { return null; }
        }

        private static void OnBeginBuilding()
        {
            if (!_isHost) return;
            PublishAuthoritativeSave("hq_begin_build");
            Log("observed kind=hq_begin_build");
        }

        private static void OnBeginUpgrade()
        {
            if (!_isHost) return;
            PublishAuthoritativeSave("hq_begin_upgrade");
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
            if (!_isHost) return;
            PublishAuthoritativeSave("car_design_start");
            Log("observed kind=car_design_start");
        }

        private static void OnBuildTwoParts(int __0)
        {
            if (!_isHost) return;
            PublishAuthoritativeSave("car_build_two_parts");
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
            if (!_isHost) return;
            int id = PersonId(__1);
            if (id >= 0) { PublishAuthoritativeSave("contract_hire"); Log("observed kind=contract_hire personId=" + id + " registry=" + _peopleById.Count); }
        }

        private static void OnFirePerson(Person __0)
        {
            if (!_isHost) return;
            int id = PersonId(__0);
            if (id >= 0) { PublishAuthoritativeSave("contract_fire"); Log("observed kind=contract_fire personId=" + id + " registry=" + _peopleById.Count); }
        }

        private static void OnRenewPerson(Person __0)
        {
            if (!_isHost) return;
            int id = PersonId(__0);
            if (id >= 0) { PublishAuthoritativeSave("contract_renew"); Log("observed kind=contract_renew personId=" + id + " registry=" + _peopleById.Count); }
        }

        private static void OnProcessTransaction(Transaction __0)
        {
            if (__0 == null || !_gameReady) return;
            PublishAuthoritativeSave("finance_transaction");
            Log("observed kind=finance_transaction amount=" + __0.amount + " balance=" + __0.fundsAfterTransaction + " group=" + __0.group);
        }

        private static void OnSponsorPayment(bool __0)
        {
            if (!_isHost || !_gameReady) return;
            if (__0) PublishAuthoritativeSave("sponsor_payment");
            Log("observed kind=sponsor_upfront_payment accepted=" + __0);
        }

        private static void PublishAuthoritativeSave(string reason)
        {
            if (!_isHost || _applyRemoteAction || !_gameReady || Time.realtimeSinceStartup < _suppressSavesUntil) return;
            EnsureSaveSystem();
            if (_saveSystem == null) { Log("state_dirty reason=" + reason + " save_system=unavailable"); return; }
            _stateDirty = true;
            _stateDirtyReason = reason;
            _stateDirtyAt = Time.realtimeSinceStartup;
            Log("state_dirty queued reason=" + reason);
        }

        private static void FlushAuthoritativeSave()
        {
            if (!_isHost || !_gameReady || !_stateDirty || _authoritativeSaveInProgress || _saveSystem == null) return;
            if (Time.realtimeSinceStartup - _stateDirtyAt < 1f) return;
            string reason = _stateDirtyReason;
            try
            {
                _authoritativeSaveInProgress = true;
                _stateDirty = false;
                _saveSystem.ManualSaveAs(ToSaveSlotName(_snapshotSaveName));
                Log("state_dirty reason=" + reason + " authoritative_save=requested");
            }
            catch (Exception ex)
            {
                _authoritativeSaveInProgress = false;
                _stateDirty = true;
                _stateDirtyAt = Time.realtimeSinceStartup;
                Log("authoritative_save failed reason=" + reason + " error=" + ex.Message);
            }
        }

        private static void EnsureSaveSystem()
        {
            if (_saveSystem != null) return;
            try
            {
                if (App.instance != null)
                {
                    FieldInfo appSave = AccessTools.Field(typeof(App), "saveSystem");
                    if (appSave != null)
                    {
                        CaptureSaveSystem((SaveSystem)appSave.GetValue(App.instance));
                        if (_saveSystem != null) return;
                    }
                }
                if (Game.instance == null) return;
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

        private static void EnsureGameStateManager()
        {
            if (_gameStateManager != null || App.instance == null) return;
            try { _gameStateManager = App.instance.gameStateManager; }
            catch { }
        }

        private static void OnSaveComplete()
        {
            _authoritativeSaveInProgress = false;
            if (!_isHost) return;
            Log("authoritative_save completed; broadcasting snapshot");
            lock (_hostLock)
                foreach (TcpClient client in _hostClients.ToArray())
                    if (client.Connected) try { SendSaveSnapshot(client.GetStream()); } catch { }
        }

        private static void OnPitCrewAssign(PitCrewMember __0, PitCrewMember.PitCrewRole __1)
        {
            if (!_gameReady) return;
            PublishAuthoritativeSave("pitcrew_assign");
            Log("observed kind=pitcrew_assign member=" + (__0 == null ? "-" : __0.name) + " role=" + __1);
        }

        private static void OnPitCrewSwap(PitCrewMember __0, PitCrewMember __1)
        {
            if (!_gameReady) return;
            PublishAuthoritativeSave("pitcrew_swap");
            Log("observed kind=pitcrew_swap members=" + (__0 == null ? "-" : __0.name) + "," + (__1 == null ? "-" : __1.name));
        }

        private static void OnPitCrewSignup(PitCrewMember __0)
        {
            if (!_gameReady) return;
            PublishAuthoritativeSave("pitcrew_signup");
            Log("observed kind=pitcrew_signup member=" + (__0 == null ? "-" : __0.name));
        }

        private static void OnPitCrewFire(PitCrewMember __0)
        {
            if (!_gameReady) return;
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
            bool completedSnapshotLoad = _snapshotLoadInProgress;
            _snapshotLoadInProgress = false;
            if (completedSnapshotLoad && !_isHost)
            {
                _initialSyncComplete = true;
                Log("initial host snapshot loaded; session commands enabled");
            }
            _gameReady = Game.instance != null && Game.instance.isCareer;
            _stateDirty = false;
            _suppressSavesUntil = Time.realtimeSinceStartup + 10f;
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

        private static void CaptureSaveSystemForLoad(SaveSystem __instance)
        {
            _gameReady = false;
            _stateDirty = false;
            CaptureSaveSystem(__instance);
            Log("save load started; authoritative saves suspended");
        }

        private static void CaptureSaveName(SaveSystem __instance, string __0)
        {
            CaptureSaveSystem(__instance);
            if (String.IsNullOrEmpty(__0)) return;
            _snapshotSaveName = ToSaveFileName(__0);
            Log("active shared save name=" + _snapshotSaveName);
        }

        private static string ToSaveSlotName(string saveName)
        {
            string slot = Path.GetFileNameWithoutExtension(saveName) ?? String.Empty;
            while (slot.StartsWith("Save", StringComparison.Ordinal) && slot.Length > 4)
                slot = slot.Substring(4);
            return slot;
        }

        private static string ToSaveFileName(string saveName)
        {
            return "Save" + ToSaveSlotName(saveName) + ".sav";
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

        private static void OnDrivingStyleChanged(DrivingStyle __instance, DrivingStyle.Mode __0)
        {
            RacingVehicle vehicle = VehicleFromComponent(__instance);
            if (vehicle == null || !vehicle.isPlayerDriver) return;
            int vehicleId = VehicleIdFromComponent(__instance);
            if (vehicleId >= 0) _drivingStylesByVehicle[vehicleId] = __instance;
            SendRaceAction("driving_style", vehicleId, (int)__0);
        }

        private static void OnEngineModeChanged(Fuel __instance, Fuel.EngineMode __0)
        {
            RacingVehicle vehicle = VehicleFromComponent(__instance);
            if (vehicle == null || !vehicle.isPlayerDriver) return;
            int vehicleId = VehicleIdFromComponent(__instance);
            if (vehicleId >= 0) _fuelByVehicle[vehicleId] = __instance;
            SendRaceAction("engine_mode", vehicleId, (int)__0);
        }

        private static void OnERSModeChanged(ERSController __instance, ERSController.Mode __0)
        {
            RacingVehicle vehicle = VehicleFromComponent(__instance);
            if (vehicle == null || !vehicle.isPlayerDriver) return;
            int vehicleId = VehicleIdFromComponent(__instance);
            if (vehicleId >= 0) _ersByVehicle[vehicleId] = __instance;
            SendRaceAction("ers_mode", vehicleId, (int)__0);
        }

        private static void CapturePitstop(SessionPitstop __instance)
        {
            int vehicleId = VehicleIdFromComponent(__instance);
            if (vehicleId >= 0) _pitstopsByVehicle[vehicleId] = __instance;
        }

        private static void OnPitFuelChanged(SessionPitstop __instance, int __0)
        {
            if (!_enabled) return;
            CapturePitstop(__instance);
            SendRaceAction("pit_fuel", VehicleIdFromComponent(__instance), __0);
        }

        private static void OnPitRepairChanged(SessionPitstop __instance)
        {
            if (!_enabled) return;
            CapturePitstop(__instance);
            SendRaceAction("pit_repair", VehicleIdFromComponent(__instance), 1);
        }

        private static void OnPitBatteryChanged(SessionPitstop __instance, float __0, float __1)
        {
            if (!_enabled) return;
            CapturePitstop(__instance);
            SendRaceAction("pit_battery", VehicleIdFromComponent(__instance), Mathf.RoundToInt(__0 * 1000f), Mathf.RoundToInt(__1 * 1000f));
        }

        private static void OnPitTyresChanged(SessionPitstop __instance, TyreSet __0, bool __1)
        {
            if (!_enabled) return;
            CapturePitstop(__instance);
            RacingVehicle vehicle = VehicleFromComponent(__instance);
            if (vehicle == null || vehicle.strategy == null || __0 == null) return;
            foreach (SessionStrategy.TyreOption option in Enum.GetValues(typeof(SessionStrategy.TyreOption)))
            {
                int count;
                try { count = vehicle.strategy.GetTyreCount(option); }
                catch (NullReferenceException) { continue; }
                for (int index = 0; index < count; index++)
                {
                    if (!System.Object.ReferenceEquals(vehicle.strategy.GetTyre(option, index), __0)) continue;
                    SendRaceAction("pit_tyres", vehicle.id, (int)option, index, __1 ? 1 : 0);
                    return;
                }
            }
            Log("pit tyre target not found vehicle=" + vehicle.id);
        }

        private static void OnSendOutOnTrack() { SendStrategyAction("send_out_on_track", 0); }
        private static void OnReturnToGarage() { SendStrategyAction("return_to_garage", 0); }
        private static void OnPitCommand() { SendStrategyAction("pit_command", 0); }
        private static void OnCancelPit() { SendStrategyAction("cancel_pit", 0); }
        private static void OnApplyQueueOrders() { SendStrategyAction("apply_queue_orders", 0); }
        private static void OnRemoveQueuedOrder() { SendStrategyAction("remove_queued_order", 0); }

        private static void SendStrategyAction(string kind, int value)
        {
            SendRaceAction(kind, VehicleIdFromComponent(_strategy), value);
        }

        private static void SendRaceAction(string kind, int vehicleId, int value)
        {
            SendRaceAction(kind, vehicleId, value, 0, 0);
        }

        private static void SendRaceAction(string kind, int vehicleId, int value, int aux)
        {
            SendRaceAction(kind, vehicleId, value, aux, 0);
        }

        private static void SendRaceAction(string kind, int vehicleId, int value, int aux, int flag)
        {
            if (_isHost || _applyRemoteAction || _stream == null) return;
            try
            {
                byte[] action = Encoding.UTF8.GetBytes(
                    "{\"type\":\"action\",\"kind\":\"" + kind + "\",\"target\":" + vehicleId + ",\"value\":" + value + ",\"aux\":" + aux + ",\"flag\":" + flag + "}\n");
                SendPacket(action);
                _status = "Sent: " + kind + " vehicle=" + vehicleId;
                Log("sent race action kind=" + kind + " vehicle=" + vehicleId + " value=" + value);
            }
            catch { Disconnect(); }
        }

        private static int ReadActionValue(string json)
        {
            Match match = Regex.Match(json, "\\\"value\\\"\\s*:\\s*(-?\\d+)");
            int value;
            return match.Success && Int32.TryParse(match.Groups[1].Value, out value) ? value : 0;
        }

        private static int ReadActionTarget(string json)
        {
            Match match = Regex.Match(json, "\\\"target\\\"\\s*:\\s*(-?\\d+)");
            int value;
            return match.Success && Int32.TryParse(match.Groups[1].Value, out value) ? value : -1;
        }

        private static int ReadActionInt(string json, string field, int fallback)
        {
            Match match = Regex.Match(json, "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*(-?\\d+)");
            int value;
            return match.Success && Int32.TryParse(match.Groups[1].Value, out value) ? value : fallback;
        }

        private static bool IsDeferredRaceAction(string json)
        {
            return json.IndexOf("driving_style", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("engine_mode", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("ers_mode", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("team_orders", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("pit_strategy", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("ordered_lap_count", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("send_out_on_track", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("return_to_garage", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("pit_command", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("cancel_pit", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("apply_queue_orders", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("remove_queued_order", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("pit_fuel", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("pit_repair", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("pit_battery", StringComparison.Ordinal) >= 0 ||
                   json.IndexOf("pit_tyres", StringComparison.Ordinal) >= 0;
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
            EnsureGameStateManager();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("LAN Coop", GUILayout.Width(100))) _window = !_window;
            GUILayout.Label(_status, GUILayout.Width(220));
            GUILayout.EndHorizontal();
            string incoming = null;
            lock (_incomingLock)
            {
                if (_initialSyncComplete && _deferredSessionLoads.Count > 0)
                    incoming = _deferredSessionLoads.Dequeue();
                else if (_raceRuntimeReady && _deferredRaceActions.Count > 0)
                    incoming = _deferredRaceActions.Dequeue();
                else if (_incoming.Count > 0)
                    incoming = _incoming.Dequeue();
                if (incoming != null && !_raceRuntimeReady && IsDeferredRaceAction(incoming))
                {
                    _deferredRaceActions.Enqueue(incoming);
                    incoming = null;
                }
            }
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
            if (incoming != null && _timer != null &&
                (incoming.IndexOf("play_skip_sim", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("pause_or_play", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    if (incoming.IndexOf("pause_or_play", StringComparison.Ordinal) >= 0)
                    {
                        _remotePaused = !_remotePaused;
                        if (_remotePaused) _timer.Pause(GameTimer.PauseType.Game);
                        else _timer.UnPause(GameTimer.PauseType.Game);
                    }
                    else
                        _timer.PlaySkipSim();
                    _status = "Applied remote simulation command";
                    Log("applied remote simulation command");
                    if (_isHost) BroadcastTelemetry();
                }
                catch (Exception ex) { _status = "Remote action failed: " + ex.Message; Log(_status); }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && incoming.IndexOf("go_next_session", StringComparison.Ordinal) >= 0 && _raceEvent != null)
            {
                _applyRemoteAction = true;
                try { _raceEvent.GoToNextSession(); _status = "Applied remote next-session command"; }
                catch (Exception ex) { _status = "Remote session change failed: " + ex.Message; }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && incoming.IndexOf("load_race_event", StringComparison.Ordinal) >= 0)
            {
                if (!_isHost && (!_initialSyncComplete || _snapshotLoadInProgress || Interlocked.CompareExchange(ref _snapshotLoadPending, 0, 0) != 0))
                {
                    lock (_incomingLock) _deferredSessionLoads.Enqueue(incoming);
                    Log("deferred race event load until host snapshot is loaded");
                    return;
                }
                EnsureGameStateManager();
                _applyRemoteAction = true;
                _raceRuntimeReady = false;
                try
                {
                    if (_gameStateManager == null) throw new InvalidOperationException("GameStateManager unavailable");
                    _gameStateManager.LoadToRaceEvent((GameStateManager.StateChangeType)ReadActionValue(incoming));
                    _status = "Loading remote race event";
                    Log("applied remote race event load");
                }
                catch (Exception ex) { _status = "Remote race load failed: " + ex.Message; Log(_status); }
                finally { _applyRemoteAction = false; }
            }
            int incomingTarget = incoming == null ? -1 : ReadActionTarget(incoming);
            SessionStrategy targetStrategy = null;
            if (incomingTarget >= 0) _strategiesByVehicle.TryGetValue(incomingTarget, out targetStrategy);
            if (targetStrategy == null) targetStrategy = _strategy;
            if (incoming != null && targetStrategy != null && incoming.IndexOf("\"type\":\"action\"", StringComparison.Ordinal) >= 0)
            {
                FieldInfo aiStrategy = AccessTools.Field(typeof(SessionStrategy), "mUsesAIForStrategy");
                if (aiStrategy != null) aiStrategy.SetValue(targetStrategy, false);
            }
            if (incoming != null && targetStrategy != null &&
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
                        targetStrategy.SetTeamOrders((SessionStrategy.TeamOrders)value);
                    else if (incoming.IndexOf("pit_strategy", StringComparison.Ordinal) >= 0)
                        targetStrategy.SetPitStrategy((SessionStrategy.PitStrategy)value);
                    else if (incoming.IndexOf("ordered_lap_count", StringComparison.Ordinal) >= 0)
                        targetStrategy.SetOrderedLapCount(value);
                    else if (incoming.IndexOf("send_out_on_track", StringComparison.Ordinal) >= 0)
                    {
                        if (!TryBeginSendOut(targetStrategy))
                        {
                            _pendingSendOut.Add(incomingTarget);
                            Log("queued remote send out vehicle=" + incomingTarget + " status=" + targetStrategy.status);
                        }
                    }
                    else if (incoming.IndexOf("return_to_garage", StringComparison.Ordinal) >= 0)
                        targetStrategy.ReturnToGarage();
                    else if (incoming.IndexOf("pit_command", StringComparison.Ordinal) >= 0)
                        targetStrategy.Pit();
                    else if (incoming.IndexOf("cancel_pit", StringComparison.Ordinal) >= 0)
                        targetStrategy.CancelPit();
                    else if (incoming.IndexOf("apply_queue_orders", StringComparison.Ordinal) >= 0)
                        targetStrategy.ApplyQueueOrders();
                    else
                        targetStrategy.RemoveQueuedOrder();
                    _status = "Applied remote race strategy vehicle=" + incomingTarget;
                    Log("applied remote race strategy vehicle=" + incomingTarget);
                }
                catch (Exception ex) { _status = "Remote strategy failed: " + ex.Message; Log(_status); }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null &&
                (incoming.IndexOf("driving_style", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("engine_mode", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("ers_mode", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    int value = ReadActionValue(incoming);
                    if (incoming.IndexOf("driving_style", StringComparison.Ordinal) >= 0)
                    {
                        DrivingStyle drivingStyle;
                        if (!_drivingStylesByVehicle.TryGetValue(incomingTarget, out drivingStyle)) throw new InvalidOperationException("DrivingStyle target unavailable");
                        drivingStyle.SetDrivingStyle((DrivingStyle.Mode)value);
                    }
                    else if (incoming.IndexOf("engine_mode", StringComparison.Ordinal) >= 0)
                    {
                        Fuel fuel;
                        if (!_fuelByVehicle.TryGetValue(incomingTarget, out fuel)) throw new InvalidOperationException("Fuel target unavailable");
                        fuel.SetEngineMode((Fuel.EngineMode)value, true);
                    }
                    else
                    {
                        ERSController ers;
                        if (!_ersByVehicle.TryGetValue(incomingTarget, out ers)) throw new InvalidOperationException("ERS target unavailable");
                        ers.SetERSMode((ERSController.Mode)value);
                    }
                    _status = "Applied remote driving mode vehicle=" + incomingTarget;
                    Log("applied remote driving mode vehicle=" + incomingTarget + " value=" + value);
                }
                catch (Exception ex) { _status = "Remote driving mode failed: " + ex.Message; Log(_status); }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null &&
                (incoming.IndexOf("pit_fuel", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("pit_repair", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("pit_battery", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("pit_tyres", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("tyre_select", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    SessionPitstop pitstop;
                    if (!_pitstopsByVehicle.TryGetValue(incomingTarget, out pitstop)) throw new InvalidOperationException("Pitstop target unavailable");
                    int value = ReadActionValue(incoming);
                    int aux = ReadActionInt(incoming, "aux", 0);
                    if (incoming.IndexOf("pit_fuel", StringComparison.Ordinal) >= 0)
                        pitstop.SetTargetFuelLevel(value);
                    else if (incoming.IndexOf("pit_repair", StringComparison.Ordinal) >= 0)
                        pitstop.SetRepairParts();
                    else if (incoming.IndexOf("pit_battery", StringComparison.Ordinal) >= 0)
                        pitstop.SetTargetBatteryCharge(value / 1000f, aux / 1000f);
                    else
                    {
                        SessionStrategy strategy;
                        if (!_strategiesByVehicle.TryGetValue(incomingTarget, out strategy)) throw new InvalidOperationException("Tyre strategy target unavailable");
                        TyreSet tyre = strategy.GetTyre((SessionStrategy.TyreOption)value, aux);
                        if (tyre == null) throw new InvalidOperationException("Tyre target unavailable");
                        pitstop.SetTargetTyres(tyre, ReadActionInt(incoming, "flag", 0) != 0);
                    }
                    _status = "Applied remote pit setup vehicle=" + incomingTarget;
                    Log("applied remote pit setup vehicle=" + incomingTarget);
                }
                catch (Exception ex) { _status = "Remote pit setup failed: " + ex.Message; Log(_status); }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null &&
                (incoming.IndexOf("setup_value", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("setup_apply", StringComparison.Ordinal) >= 0 ||
                 incoming.IndexOf("practice_program", StringComparison.Ordinal) >= 0))
            {
                _applyRemoteAction = true;
                try
                {
                    SessionSetup setup;
                    if (!_setupsByVehicle.TryGetValue(incomingTarget, out setup)) throw new InvalidOperationException("Session setup target unavailable");
                    if (incoming.IndexOf("practice_program", StringComparison.Ordinal) >= 0)
                    {
                        int program = ReadActionValue(incoming);
                        RacingVehicle vehicle = VehicleFromComponent(setup);
                        setup.SetTargetTrim(program == 0 ? SessionSetup.Trim.Qualifying : SessionSetup.Trim.Race);
                        if (vehicle != null && vehicle.practiceKnowledge != null)
                            vehicle.practiceKnowledge.knowledgeType = program == 0
                                ? PracticeReportSessionData.KnowledgeType.QualifyingTrim
                                : PracticeReportSessionData.KnowledgeType.RaceTrim;
                        Log("applied remote practice program vehicle=" + incomingTarget + " program=" + program);
                    }
                    else if (incoming.IndexOf("setup_value", StringComparison.Ordinal) >= 0)
                    {
                        SetupDetails details = ReadObject(setup, "targetSetup", "mTargetSetup") as SetupDetails;
                        SetupDetails current = ReadObject(setup, "currentSetup", "mCurrentSetup") as SetupDetails;
                        if (details == null || details.input == null || current == null || current.input == null) throw new InvalidOperationException("Setup input unavailable");
                        if (!_remoteSetupInitialized.Contains(incomingTarget))
                        {
                            details.input.CopySetupInput(current.input);
                            _remoteSetupInitialized.Add(incomingTarget);
                        }
                        int option = ReadActionInt(incoming, "aux", 0);
                        SetupInput_v1.SetupInputOptions setupOption = (SetupInput_v1.SetupInputOptions)option;
                        if (!details.input.setup.ContainsKey(setupOption)) throw new InvalidOperationException("Setup option unavailable: " + setupOption);
                        details.input.SetSetupValue(setupOption, ReadActionValue(incoming) / 1000f);
                        setup.SetTargetSetupInput(details.input);
                        Log("applied remote setup value vehicle=" + incomingTarget + " option=" + option);
                    }
                    else
                    {
                        Log("accepted remote setup changes vehicle=" + incomingTarget);
                    }
                }
                catch (Exception ex) { _status = "Remote setup failed: " + ex.Message; Log(_status); }
                finally { _applyRemoteAction = false; }
            }
            if (incoming != null && incoming.IndexOf("simulation_speed", StringComparison.Ordinal) >= 0 && _timer != null)
            {
                _applyRemoteAction = true;
                try { _timer.SetSpeed((GameTimer.Speed)ReadActionValue(incoming)); _status = "Applied remote simulation speed"; Log("applied remote simulation speed value=" + ReadActionValue(incoming)); }
                catch (Exception ex) { _status = "Remote speed failed: " + ex.Message; Log(_status); }
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
            _autoLoadElapsed += deltaTime;
            _telemetryElapsed += deltaTime;
            _telemetryLogElapsed += deltaTime;
            if (_isHost && _telemetryElapsed >= 0.5f)
            {
                _telemetryElapsed = 0f;
                BroadcastTelemetry();
            }
            if (_isHost && _telemetryLogElapsed >= 10f)
            {
                _telemetryLogElapsed = 0f;
                Log("telemetry heartbeat runtime=" + _raceRuntimeReady + " vehicles=" + _strategiesByVehicle.Count + " clients=" + _hostClients.Count + " cached=" + (_lastTelemetryPacket == null ? 0 : _lastTelemetryPacket.Length));
            }
            if (_pendingSendOut.Count > 0)
            {
                foreach (int vehicleId in new List<int>(_pendingSendOut))
                {
                    SessionStrategy pendingStrategy;
                    if (!_strategiesByVehicle.TryGetValue(vehicleId, out pendingStrategy)) continue;
                    if (!IsActuallyInGarage(pendingStrategy)) continue;
                    try { _applyRemoteAction = true; if (TryBeginSendOut(pendingStrategy)) { _pendingSendOut.Remove(vehicleId); Log("applied queued send out vehicle=" + vehicleId); } }
                    catch (Exception ex) { Log("queued send out failed=" + ex.Message); }
                    finally { _applyRemoteAction = false; }
                }
            }
            EnsureSaveSystem();
            if (_timer == null && Game.instance != null) _timer = ReadObject(Game.instance, "time", "mTime") as GameTimer;
            FlushAuthoritativeSave();
            if (!_autoLoadRequested && _autoLoadElapsed >= 5f && _isHost && !IsNewCareerMode() && _saveSystem != null)
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
            if (_saveSystem == null) return;
            if (_snapshotLoadInProgress || Interlocked.Exchange(ref _snapshotLoadPending, 0) != 1) return;
            _snapshotLoadInProgress = true;
            try
            {
                _applyRemoteAction = true;
                var method = typeof(SaveSystem).GetMethod("LoadSaveWithName");
                if (method == null) throw new MissingMethodException("SaveSystem.LoadSaveWithName");
                method.Invoke(_saveSystem, new object[] { Path.GetFileNameWithoutExtension(_snapshotSaveName) });
                _status = "Host save received and load requested";
                Log("received save snapshot name=" + _snapshotSaveName);
            }
            catch (Exception ex) { _snapshotLoadInProgress = false; _status = "Host save received; load failed: " + ex.Message; Log("snapshot load failed=" + ex.Message); }
            finally { _applyRemoteAction = false; }
        }

        private static bool IsAutoMode()
        {
            string role = Environment.GetEnvironmentVariable("MM_COOP_AUTOSTART");
            return String.Equals(role, "host", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(role, "client", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNewCareerMode()
        {
            return String.Equals(Environment.GetEnvironmentVariable("MM_COOP_NEW_CAREER"), "1", StringComparison.Ordinal);
        }

        private static void Connect()
        {
            Disconnect();
            _initialSyncComplete = false;
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
                byte[] request = Encoding.UTF8.GetBytes("{\"type\":\"resync_request\"}\n");
                _stream.Write(request, 0, request.Length);
                Log("client requested mandatory initial resync");
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
                    client.SendTimeout = 1000;
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
                        else if (line.IndexOf("\"type\":\"telemetry_request\"", StringComparison.Ordinal) >= 0)
                        {
                            byte[] telemetry = _lastTelemetryPacket;
                            if (telemetry != null) WritePacket(stream, telemetry);
                            Log("telemetry snapshot requested");
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
                    if (client != except && client.Connected)
                        try { WritePacket(client.GetStream(), packet); }
                        catch { _hostClients.Remove(client); try { client.Close(); } catch { } }
        }

        private static void WritePacket(NetworkStream stream, byte[] packet)
        {
            lock (stream) stream.Write(packet, 0, packet.Length);
        }

        private static void BroadcastTelemetry()
        {
            try
            {
                byte[] packet = BuildTelemetryPacket();
                if (packet != null) { _lastTelemetryPacket = packet; Broadcast(packet, null); }
            }
            catch (Exception ex) { Log("telemetry build failed=" + ex.Message); }
        }

        private static byte[] BuildTelemetryPacket()
        {
            try
            {
                StringBuilder json = new StringBuilder("{\"type\":\"telemetry\",\"session\":\"");
                SessionManager manager = Game.instance == null ? null : Game.instance.sessionManager;
                json.Append(JsonEscape(manager == null ? "" : manager.sessionType.ToString()));
                json.Append("\",\"speed\":").Append(_timer == null ? 0 : (int)ReadNumber(_timer, "speed", "mSpeed"));
                json.Append(",\"paused\":").Append(_remotePaused ? "true" : "false");
                json.Append(",\"sessionTime\":").Append((manager == null ? 0f : manager.time).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                json.Append(",\"sessionLap\":").Append(manager == null ? 0 : manager.lap);
                json.Append(",\"sessionLapCount\":").Append(manager == null ? 0 : manager.lapCount);
                json.Append(",\"vehicles\":[");
                bool first = true;
                foreach (KeyValuePair<int, SessionStrategy> pair in _strategiesByVehicle)
                {
                    RacingVehicle vehicle = VehicleFromComponent(pair.Value);
                    if (vehicle == null || !vehicle.isPlayerDriver) continue;
                    if (!first) json.Append(',');
                    first = false;
                    object driver = ReadObject(vehicle, "driver", "mDriver");
                    object fuel = _fuelByVehicle.ContainsKey(pair.Key) ? _fuelByVehicle[pair.Key] : null;
                    json.Append("{\"id\":").Append(pair.Key);
                    json.Append(",\"driver\":\"").Append(JsonEscape(ReadText(driver, "name", "fullName", "mName"))).Append('"');
                    SessionTimer timer = vehicle.timer;
                    SessionTimer.LapData previousLap = timer == null ? null : timer.GetPreviousActiveLapData();
                    TyreSet currentTyre = vehicle.setup == null ? null : vehicle.setup.tyreSet;
                    json.Append(",\"lap\":").Append(timer == null ? 0 : timer.lap);
                    json.Append(",\"position\":").Append(vehicle.standingsPosition + 1);
                    json.Append(",\"gapLeader\":").Append((timer == null ? 0f : timer.gapToLeader).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    json.Append(",\"gapAhead\":").Append((timer == null ? 0f : timer.gapToAhead).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    json.Append(",\"lastLap\":").Append((previousLap == null ? 0f : previousLap.time).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    json.Append(",\"bestLap\":").Append((timer == null ? 0f : timer.GetFastestLapTime()).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    json.Append(",\"fuel\":").Append((fuel == null ? 0f : ((Fuel)fuel).GetFuelLapsRemainingDecimal()).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    json.Append(",\"tyreWear\":").Append((currentTyre == null ? 0f : currentTyre.GetCondition() * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    json.Append(",\"tyreTemperature\":").Append((currentTyre == null ? 0f : currentTyre.GetTemperature() * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    json.Append(",\"currentCompound\":\"").Append(JsonEscape(currentTyre == null ? "" : currentTyre.GetCompound().ToString())).Append('"');
                    json.Append(",\"orderedLaps\":").Append((int)ReadNumber(pair.Value, "orderedLapCount", "mOrderedLapCount"));
                    json.Append(",\"tyres\":[");
                    bool firstTyre = true;
                    for (int tyreOption = 0; tyreOption < 5; tyreOption++)
                    {
                        int tyreCount = pair.Value.GetTyreCount((SessionStrategy.TyreOption)tyreOption);
                        for (int tyreIndex = 0; tyreIndex < tyreCount; tyreIndex++)
                        {
                            TyreSet tyre = pair.Value.GetTyre((SessionStrategy.TyreOption)tyreOption, tyreIndex);
                            if (!firstTyre) json.Append(',');
                            firstTyre = false;
                            json.Append("{\"option\":").Append(tyreOption).Append(",\"index\":").Append(tyreIndex);
                            json.Append(",\"name\":\"").Append(JsonEscape(tyre == null ? "" : tyre.GetCompound().ToString())).Append("\"}");
                        }
                    }
                    json.Append(']');
                    int selectedTyreOption = -1;
                    int selectedTyreIndex = -1;
                    SessionPitstop selectedPitstop;
                    if (_pitstopsByVehicle.TryGetValue(pair.Key, out selectedPitstop))
                    {
                        SetupDetails targetDetails = ReadObject(selectedPitstop, "targetSetup", "mTargetSetup") as SetupDetails;
                        TyreSet selectedTyre = targetDetails == null ? null : targetDetails.tyreSet;
                        if (selectedTyre != null)
                        {
                            for (int tyreOption = 0; tyreOption < 5 && selectedTyreOption < 0; tyreOption++)
                            {
                                int tyreCount = pair.Value.GetTyreCount((SessionStrategy.TyreOption)tyreOption);
                                for (int tyreIndex = 0; tyreIndex < tyreCount; tyreIndex++)
                                    if (object.ReferenceEquals(pair.Value.GetTyre((SessionStrategy.TyreOption)tyreOption, tyreIndex), selectedTyre))
                                    {
                                        selectedTyreOption = tyreOption;
                                        selectedTyreIndex = tyreIndex;
                                        break;
                                    }
                            }
                        }
                    }
                    json.Append(",\"selectedTyreOption\":").Append(selectedTyreOption);
                    json.Append(",\"selectedTyreIndex\":").Append(selectedTyreIndex);
                    SessionSetup sessionSetup;
                    json.Append(",\"setup\":[");
                    if (_setupsByVehicle.TryGetValue(pair.Key, out sessionSetup))
                    {
                        SetupDetails currentDetails = ReadObject(sessionSetup, "targetSetup", "mTargetSetup") as SetupDetails;
                        for (int option = 0; option < 7; option++)
                        {
                            if (option > 0) json.Append(',');
                            float setupValue = ReadSetupValue(currentDetails, option);
                            json.Append(setupValue.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        json.Append("],\"trim\":\"").Append(JsonEscape(ReadText(currentDetails, "trim", "mTrim"))).Append('"');
                        SessionSetup.SetupOutput setupOutput = new SessionSetup.SetupOutput();
                        if (currentDetails != null && currentDetails.input != null)
                            currentDetails.input.GetSetupOutput(ref setupOutput, vehicle.driver.weight);
                        SetupPerformance.OptimalSetup optimal = vehicle.performance.setupPerformance.GetOptimalSetup();
                        float knowledge = vehicle.practiceKnowledge == null ? 0f : vehicle.practiceKnowledge.GetSetupKnowledgeNormalised();
                        float[] minRange; float[] maxRange;
                        vehicle.performance.setupPerformance.GetVisualKnowledgeRangeFromNormalisedValue(knowledge, out minRange, out maxRange);
                        float aeroDelta = setupOutput.aerodynamics - optimal.setupOutput.aerodynamics;
                        float speedDelta = setupOutput.speedBalance - optimal.setupOutput.speedBalance;
                        float handlingDelta = setupOutput.handling - optimal.setupOutput.handling;
                        float quality = (Mathf.Clamp01(1f - Mathf.Abs(aeroDelta)) + Mathf.Clamp01(1f - Mathf.Abs(speedDelta)) + Mathf.Clamp01(1f - Mathf.Abs(handlingDelta))) / 3f;
                        json.Append(",\"setupBalance\":[").Append(setupOutput.aerodynamics.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(setupOutput.speedBalance.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(setupOutput.handling.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                        json.Append(",\"setupRecommendedMin\":[").Append((optimal.setupOutput.aerodynamics + minRange[0]).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append((optimal.setupOutput.speedBalance + minRange[1]).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append((optimal.setupOutput.handling + minRange[2]).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                        json.Append(",\"setupRecommendedMax\":[").Append((optimal.setupOutput.aerodynamics + maxRange[0]).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append((optimal.setupOutput.speedBalance + maxRange[1]).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append((optimal.setupOutput.handling + maxRange[2]).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                        json.Append(",\"setupQuality\":").Append((quality * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                        json.Append(",\"setupKnowledge\":").Append((knowledge * 100f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else json.Append(']');
                    json.Append(",\"status\":\"").Append(JsonEscape(ReadText(pair.Value, "status", "mStatus"))).Append("\"}");
                }
                json.Append("]}\n");
                return Encoding.UTF8.GetBytes(json.ToString());
            }
            catch (Exception ex) { Log("telemetry build failed=" + ex.Message); return null; }
        }

        private static object ReadObject(object instance, params string[] names)
        {
            if (instance == null) return null;
            foreach (string name in names)
            {
                FieldInfo field = AccessTools.Field(instance.GetType(), name);
                if (field != null) return field.GetValue(instance);
                PropertyInfo property = AccessTools.Property(instance.GetType(), name);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(instance, null);
            }
            return null;
        }

        private static float ReadSetupValue(SetupDetails details, int option)
        {
            if (details == null || details.input == null) return -1f;
            SetupInput_v1.SetupInputOptions key = (SetupInput_v1.SetupInputOptions)option;
            return details.input.setup.ContainsKey(key) ? details.input.setup[key] : -1f;
        }

        private static bool IsActuallyInGarage(SessionStrategy strategy)
        {
            RacingVehicle vehicle = VehicleFromComponent(strategy);
            return vehicle != null && vehicle.pathState != null &&
                (vehicle.pathState.pathStateGroup == PathStateManager.PathStateGroup.InGarage ||
                 vehicle.pathState.pathStateGroup == PathStateManager.PathStateGroup.InPitbox);
        }

        private static bool TryBeginSendOut(SessionStrategy strategy)
        {
            RacingVehicle vehicle = VehicleFromComponent(strategy);
            if (vehicle == null || vehicle.setup == null || !IsActuallyInGarage(strategy)) return false;
            vehicle.setup.MakeSetupChanges();
            Log("started standard send out sequence vehicle=" + vehicle.id + " status=" + strategy.status);
            return true;
        }

        private static string ReadText(object instance, params string[] names)
        {
            object value = ReadObject(instance, names);
            return value == null ? "" : value.ToString();
        }

        private static double ReadNumber(object instance, params string[] names)
        {
            object value = ReadObject(instance, names);
            try { return value == null ? 0d : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return 0d; }
        }

        private static bool ReadBool(object instance, params string[] names)
        {
            object value = ReadObject(instance, names);
            try { return value != null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        private static string JsonEscape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

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

        private static string FindNewestSavePath()
        {
            string dir = SaveDirectory();
            if (!Directory.Exists(dir)) return null;
            string newest = null;
            DateTime newestTime = DateTime.MinValue;
            foreach (string candidate in Directory.GetFiles(dir, "*.sav"))
            {
                DateTime time;
                try { time = File.GetLastWriteTimeUtc(candidate); }
                catch { continue; }
                if (time <= newestTime) continue;
                newest = candidate;
                newestTime = time;
            }
            return newest;
        }

        private static void SendSaveSnapshot(NetworkStream stream)
        {
            string dir = SaveDirectory();
            string path = FindNewestSavePath();
            if (!File.Exists(path)) { Log("resync snapshot unavailable path=" + path); return; }
            _snapshotSaveName = Path.GetFileName(path);
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
                    if (String.Equals(actualHash, _lastAcceptedSnapshotHash, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(_snapshotTemp);
                        Log("duplicate save snapshot ignored sha256=" + actualHash);
                        return;
                    }
                    if (File.Exists(_snapshotTarget)) File.Copy(_snapshotTarget, _snapshotTarget + ".backup", true);
                    File.Copy(_snapshotTemp, _snapshotTarget, true); File.Delete(_snapshotTemp);
                    Log("resync file replaced target=" + _snapshotTarget);
                    _pendingSnapshotHash = actualHash;
                    _lastAcceptedSnapshotHash = actualHash;
                    Interlocked.Exchange(ref _snapshotLoadPending, 1);
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
