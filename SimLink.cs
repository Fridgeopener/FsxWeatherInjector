using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.FlightSimulator.SimConnect;

namespace FsxWeatherInjector
{
    enum DEFS { Pos, Zulu }
    enum REQ { Pos, Station, Zulu }
    enum EVT { ZuluYear, ZuluDay, ZuluHours, ZuluMinutes }
    enum GRP { Time }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct PosStruct
    {
        public double Lat;
        public double Lon;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct ZuluStruct
    {
        public double Year;
        public double Month;
        public double Day;
        public double Time; // seconds since Zulu midnight
    }

    /// <summary>
    /// Thin wrapper around SimConnect. Event driven: messages arrive through the form's
    /// window procedure, so there is no polling thread and no per-frame work.
    /// Every call is logged with its SimConnect packet ID so that an asynchronous exception
    /// reported by FSX can be traced back to the exact call that caused it.
    /// </summary>
    public class SimLink : IDisposable
    {
        public const int WM_USER_SIMCONNECT = 0x0402;

        readonly IntPtr hwnd;
        SimConnect sc;
        readonly HashSet<string> stations = new HashSet<string>();
        readonly Dictionary<uint, string> sent = new Dictionary<uint, string>(); // packet id -> description
        DateTime lastConnectFailLog = DateTime.MinValue;
        System.Windows.Forms.Timer readbackTimer;

        // The FSX managed library has no GetLastSentPacketID, so we number our own calls. FSX numbers packets
        // the same way (one per call), possibly offset by one for the connection packet; the offset is
        // calibrated from the first "unable to create station" exception, which we know belongs to a
        // WeatherCreateStation call.
        uint seq;
        int idOffset;
        bool idCalibrated;

        public event Action<bool> ConnectionChanged;
        public event Action<double, double> PositionReceived;
        public event Action<string> Log;
        /// <summary>Raised when SimConnect cannot be initialised at all (library problem, not "FSX not running").</summary>
        public event Action<string> Fatal;

        public bool Connected { get { return sc != null; } }
        public string FatalError { get; private set; }
        public void ClearFatal() { FatalError = null; }

        public SimLink(IntPtr windowHandle) { hwnd = windowHandle; }

        // ---- tracing helper ----------------------------------------------------------

        /// <summary>Runs a SimConnect call, then records which packet ID it was sent as.</summary>
        void Call(string desc, Action action)
        {
            try
            {
                action();
                uint id = ++seq;
                if (sent.Count > 2000) sent.Clear();
                sent[id] = desc;
                Logger.Info("SEND #" + id + "  " + desc);
            }
            catch (COMException ex)
            {
                Logger.Error("CALL FAILED  " + desc + "  (HRESULT 0x" + ex.ErrorCode.ToString("X8") + ")", ex);
                throw;
            }
        }

        // ---- connection --------------------------------------------------------------

        public bool TryConnect()
        {
            if (sc != null) return true;
            if (FatalError != null) return false; // library problem: don't retry every 5 s (Help > Check SimConnect retries)
            try
            {
                sc = new SimConnect("FSX Weather Injector", hwnd, WM_USER_SIMCONNECT, null, 0);
                Logger.Info("SimConnect object created, registering handlers and definitions");

                sc.OnRecvOpen += (s, d) =>
                {
                    Logger.Info(string.Format("OPEN: {0} version {1}.{2} build {3}.{4}; SimConnect {5}.{6} build {7}.{8}",
                        d.szApplicationName, d.dwApplicationVersionMajor, d.dwApplicationVersionMinor,
                        d.dwApplicationBuildMajor, d.dwApplicationBuildMinor,
                        d.dwSimConnectVersionMajor, d.dwSimConnectVersionMinor,
                        d.dwSimConnectBuildMajor, d.dwSimConnectBuildMinor));
                    if (ConnectionChanged != null) ConnectionChanged(true);
                };
                sc.OnRecvQuit += (s, d) => { Logger.Info("QUIT received from FSX"); Disconnect(); };
                sc.OnRecvException += OnException;
                sc.OnRecvSimobjectDataBytype += OnData;

                Call("AddToDataDefinition PLANE LATITUDE", () =>
                    sc.AddToDataDefinition(DEFS.Pos, "PLANE LATITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED));
                Call("AddToDataDefinition PLANE LONGITUDE", () =>
                    sc.AddToDataDefinition(DEFS.Pos, "PLANE LONGITUDE", "degrees", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED));
                sc.RegisterDataDefineStruct<PosStruct>(DEFS.Pos);

                // Read-back of the sim's Zulu clock, used only to verify time sync in the log.
                Call("AddToDataDefinition ZULU YEAR", () =>
                    sc.AddToDataDefinition(DEFS.Zulu, "ZULU YEAR", "number", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED));
                Call("AddToDataDefinition ZULU MONTH OF YEAR", () =>
                    sc.AddToDataDefinition(DEFS.Zulu, "ZULU MONTH OF YEAR", "number", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED));
                Call("AddToDataDefinition ZULU DAY OF MONTH", () =>
                    sc.AddToDataDefinition(DEFS.Zulu, "ZULU DAY OF MONTH", "number", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED));
                Call("AddToDataDefinition ZULU TIME", () =>
                    sc.AddToDataDefinition(DEFS.Zulu, "ZULU TIME", "seconds", SIMCONNECT_DATATYPE.FLOAT64, 0f, SimConnect.SIMCONNECT_UNUSED));
                sc.RegisterDataDefineStruct<ZuluStruct>(DEFS.Zulu);

                // Events used to set the sim clock (Zulu time). Each is registered separately so one
                // bad event name shows up in the log without affecting the others.
                MapTimeEvent(EVT.ZuluYear, "ZULU_YEAR_SET");
                // FSX has ZULU_DAY_SET (ZULU_DAYOFYEAR_SET is not recognised by FSX: NAME_UNRECOGNIZED).
                MapTimeEvent(EVT.ZuluDay, "ZULU_DAY_SET");
                MapTimeEvent(EVT.ZuluHours, "ZULU_HOURS_SET");
                MapTimeEvent(EVT.ZuluMinutes, "ZULU_MINUTES_SET");
                Call("SetNotificationGroupPriority Time HIGHEST", () =>
                    sc.SetNotificationGroupPriority(GRP.Time, SimConnect.SIMCONNECT_GROUP_PRIORITY_HIGHEST));
                return true;
            }
            catch (COMException ex)
            {
                if ((DateTime.Now - lastConnectFailLog).TotalSeconds > 60)
                {
                    Logger.Info("Cannot connect to FSX yet (HRESULT 0x" + ex.ErrorCode.ToString("X8") + "): " + ex.Message);
                    lastConnectFailLog = DateTime.Now;
                }
                if (sc != null) { try { sc.Dispose(); } catch { } }
                sc = null; // FSX not running yet
                return false;
            }
            catch (Exception ex)
            {
                // Anything that is not a COM error means SimConnect itself could not be loaded/initialised
                // (e.g. native SimConnect.dll missing or wrong version), not just "FSX isn't running".
                Logger.Error("SimConnect could not be initialised (library problem, not a connection problem)", ex);
                if (sc != null) { try { sc.Dispose(); } catch { } }
                sc = null;
                FatalError = ex.GetType().Name + ": " + ex.Message;
                if (Fatal != null) Fatal(FatalError);
                return false;
            }
        }

        void MapTimeEvent(EVT evt, string simEvent)
        {
            Call("MapClientEventToSimEvent " + evt + " = " + simEvent, () => sc.MapClientEventToSimEvent(evt, simEvent));
            Call("AddClientEventToNotificationGroup Time <- " + evt, () => sc.AddClientEventToNotificationGroup(GRP.Time, evt, false));
        }

        public void HandleMessage(ref Message m)
        {
            if (m.Msg != WM_USER_SIMCONNECT || sc == null) return;
            try { sc.ReceiveMessage(); }
            catch (COMException ex)
            {
                Logger.Error("ReceiveMessage failed, disconnecting (HRESULT 0x" + ex.ErrorCode.ToString("X8") + ")", ex);
                Disconnect();
            }
        }

        // ---- FSX -> app --------------------------------------------------------------

        void OnException(SimConnect s, SIMCONNECT_RECV_EXCEPTION d)
        {
            var ex = (SIMCONNECT_EXCEPTION)d.dwException;
            long id = d.dwSendID;

            // Calibrate our call numbering against FSX's packet numbering.
            if (!idCalibrated && ex == SIMCONNECT_EXCEPTION.WEATHER_UNABLE_TO_CREATE_STATION)
            {
                string c0, c1;
                sent.TryGetValue((uint)id, out c0);
                sent.TryGetValue((uint)(id - 1), out c1);
                if (c0 != null && c0.StartsWith("WeatherCreateStation")) { idOffset = 0; idCalibrated = true; }
                else if (c1 != null && c1.StartsWith("WeatherCreateStation")) { idOffset = 1; idCalibrated = true; }
                if (idCalibrated) Logger.Info("Packet numbering calibrated: FSX SendID = our call # + " + idOffset);
            }

            string what;
            if (idCalibrated)
            {
                if (!sent.TryGetValue((uint)(id - idOffset), out what)) what = "unknown call";
            }
            else
            {
                string a, b;
                sent.TryGetValue((uint)id, out a);
                sent.TryGetValue((uint)(id - 1), out b);
                what = "not calibrated yet; either [" + (a ?? "?") + "] or [" + (b ?? "?") + "]";
            }

            Logger.Warn(string.Format("EXCEPTION {0} (code {1}) for SendID {2}, parameter index {3}  <- caused by: {4}",
                ex, d.dwException, d.dwSendID, d.dwIndex, what));

            // FSX already has most airports as stations; creating them again is expected to fail
            // and the observation is still applied to the existing station.
            if (ex == SIMCONNECT_EXCEPTION.WEATHER_UNABLE_TO_CREATE_STATION) return;
            if (Log != null) Log("SimConnect exception " + ex + " (" + what + ") – see log");
        }

        void OnData(SimConnect s, SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
        {
            if (data.dwData != null && data.dwData.Length > 0 && data.dwRequestID == (uint)REQ.Zulu)
            {
                var z = (ZuluStruct)data.dwData[0];
                int secs = (int)z.Time;
                Logger.Info(string.Format("SIM CLOCK readback (Zulu): {0:0000}-{1:00}-{2:00} {3:00}:{4:00}:{5:00}",
                    z.Year, z.Month, z.Day, secs / 3600, (secs / 60) % 60, secs % 60));
                return;
            }
            if (data.dwRequestID != (uint)REQ.Pos || data.dwData == null || data.dwData.Length == 0)
            {
                Logger.Warn("Unexpected data packet (request " + data.dwRequestID + ")");
                return;
            }
            var p = (PosStruct)data.dwData[0];
            Logger.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "RECV position lat={0:F4} lon={1:F4}", p.Lat, p.Lon));
            if (PositionReceived != null) PositionReceived(p.Lat, p.Lon);
        }

        // ---- app -> FSX --------------------------------------------------------------

        /// <summary>One-shot request for the user aircraft position.</summary>
        public void RequestPosition()
        {
            if (sc == null) return;
            try { Call("RequestDataOnSimObjectType position (user aircraft)", () => sc.RequestDataOnSimObjectType(REQ.Pos, DEFS.Pos, 0, SIMCONNECT_SIMOBJECT_TYPE.USER)); }
            catch (COMException) { Disconnect(); }
        }

        /// <summary>Stop FSX from using its own weather so injected METARs take over.</summary>
        public void SetCustomMode()
        {
            if (sc == null) return;
            try { Call("WeatherSetModeCustom", () => sc.WeatherSetModeCustom()); }
            catch (COMException) { Disconnect(); }
        }

        public void Inject(IEnumerable<Metar> metars)
        {
            if (sc == null) return;
            try
            {
                foreach (var m in metars)
                {
                    var mm = m;
                    if (stations.Add(mm.Icao))
                    {
                        // Fails harmlessly (logged) if FSX already knows this station.
                        string name = SafeName(mm.Name, mm.Icao);
                        Call(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "WeatherCreateStation {0} \"{1}\" lat={2} lon={3} elev={4}m", mm.Icao, name, mm.Lat, mm.Lon, mm.ElevM),
                            () => sc.WeatherCreateStation(REQ.Station, mm.Icao, name, (float)mm.Lat, (float)mm.Lon, (float)mm.ElevM));
                    }
                    // 0 = apply immediately; raise this if you want slower transitions.
                    Call("WeatherSetObservation " + mm.Raw, () => sc.WeatherSetObservation(0, mm.Raw));
                }
            }
            catch (COMException) { Disconnect(); }
        }

        /// <summary>Set the sim's Zulu clock to the current real-world UTC time.</summary>
        public bool SyncTimeToUtc()
        {
            if (sc == null) return false;
            var u = DateTime.UtcNow;
            Logger.Info("Syncing sim clock to UTC " + u.ToString("yyyy-MM-dd HH:mm") + " (day-of-year " + u.DayOfYear + ")");

            bool ok = true;
            // Each event is sent on its own so a failure on one is visible and doesn't block the rest.
            ok &= SendTime(EVT.ZuluYear, (uint)u.Year, "year");
            ok &= SendTime(EVT.ZuluDay, (uint)u.DayOfYear, "day-of-year");
            ok &= SendTime(EVT.ZuluHours, (uint)u.Hour, "hours");
            ok &= SendTime(EVT.ZuluMinutes, (uint)u.Minute, "minutes");

            // Read the clock back a moment later and log it, to verify the sim accepted the new time.
            if (readbackTimer == null)
            {
                readbackTimer = new System.Windows.Forms.Timer { Interval = 3000 };
                readbackTimer.Tick += (s, e) =>
                {
                    readbackTimer.Stop();
                    if (sc == null) return;
                    try
                    {
                        Call("RequestDataOnSimObjectType zulu clock readback",
                            () => sc.RequestDataOnSimObjectType(REQ.Zulu, DEFS.Zulu, 0, SIMCONNECT_SIMOBJECT_TYPE.USER));
                    }
                    catch (COMException) { }
                };
            }
            readbackTimer.Stop();
            readbackTimer.Start();
            return ok;
        }

        bool SendTime(EVT evt, uint value, string label)
        {
            if (sc == null) return false;
            try
            {
                Call("TransmitClientEvent " + evt + " (" + label + ") = " + value,
                    () => sc.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, evt, value, GRP.Time, SIMCONNECT_EVENT_FLAG.DEFAULT));
                return true;
            }
            catch (COMException)
            {
                return false; // already logged by Call()
            }
        }

        // Plain ASCII, short, no commas: odd station names can make creation fail.
        static string SafeName(string name, string fallback)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in name ?? "")
                if (c >= ' ' && c < 127 && c != ',') sb.Append(c);
            string s = sb.ToString().Trim();
            if (s.Length == 0) s = fallback;
            return s.Length > 30 ? s.Substring(0, 30) : s;
        }

        public void Disconnect()
        {
            if (sc == null) return;
            Logger.Info("Disconnecting from SimConnect");
            if (readbackTimer != null) readbackTimer.Stop();
            stations.Clear();
            sent.Clear();
            seq = 0;
            idOffset = 0;
            idCalibrated = false;
            try { sc.Dispose(); } catch { }
            sc = null;
            if (ConnectionChanged != null) ConnectionChanged(false);
        }

        public void Dispose() { Disconnect(); }
    }
}
