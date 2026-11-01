# FSX Live Weather Injector  (v0.5)

Authors: Claude (Anthropic) and FridgeOp

What's new in 0.5: real-time UTC clock sync, new icon, tray right-click menu
(Update Weather now / Sync Time / Open / Exit), top menu bar and About box.

Small tray-friendly app that feeds real-world METARs into Microsoft Flight Simulator X through SimConnect.

## How it works
- Connects to FSX via SimConnect (auto-retries every 5 s until FSX is running).
- Reads your aircraft position **once per refresh** (default 10 min), downloads the nearest METARs
  from NOAA's Aviation Weather Center, creates the stations in FSX and injects the observations.
- Between refreshes it does nothing: no polling thread, no per-frame requests, ~no CPU.

## Requirements
Tested so far only on FSX Steam Edition (SimConnect 10.0.62615.0). Other setups should work but are not confirmed.

**Needed**
- **Windows 7 SP1 or newer.** Windows 10 and 11 are the safe choice. It runs fine on 64-bit Windows,
  since the app is built as 32-bit to match FSX.
- **.NET Framework 4.8.** Windows 10 (May 2019 update, version 1903, or later) and Windows 11 already include it.
  Older Windows 10 builds and Windows 7 may need the free installer from Microsoft.
- **Microsoft FSX installed on the same PC**, either Steam Edition (tested) or the retail version (not tested).
  FSX installs the native SimConnect runtime itself.
- **`Microsoft.FlightSimulator.SimConnect.dll` in the app folder.** Copy it from the FSX SDK folder:
  `SDK\Core Utilities Kit\SimConnect SDK\lib\managed`. If it is missing or the wrong version, the startup
  check shows a message explaining the fix.
- **An internet connection** that can reach `aviationweather.gov` over HTTPS. Weather is downloaded from
  there and nothing else is needed. Strict firewalls or proxies could block it.

**Not needed**
- No installer, administrator rights, or separate runtime beyond the above.
- No meaningful CPU, memory or disk use. It does a small download every 10 minutes by default and idles in between.

**Things to know**
- **FSX on another PC:** the app is aimed at FSX on the same PC. Running it against FSX on a different machine
  would need SimConnect's network setup, which has not been tested.
- **Run both normally:** SimConnect can refuse to connect when one program runs as administrator and the other
  does not. If the app never connects, check that both are started the same way.
- **Time sync and weather mode:** the app switches FSX to custom weather mode and can change the sim clock
  (see Notes below).

## Build
1. Install **Visual Studio 2019/2022** (or the .NET SDK + .NET Framework 4.8 targeting pack).
2. Install SimConnect client for FSX if not present:
   `<FSX>\SDK\Core Utilities Kit\SimConnect SDK\lib\SimConnect.msi`
   (Steam/Gold: it is on the FSX disc under `SDK\SimConnect SDK\lib`.)
3. Copy `Microsoft.FlightSimulator.SimConnect.dll` from
   `<FSX>\SDK\Core Utilities Kit\SimConnect SDK\lib\managed\` into `FsxWeatherInjector\lib\`.
4. `dotnet build -c Release` (or open the .csproj in Visual Studio). Output is x86 on purpose.

## Use
1. Start FSX, load a flight, then run the app (it can be started first too).
2. Leave "Inject live weather" ticked. Adjust refresh interval, number of stations and search radius.
   **Radius** (25-1000 nm) is the furthest station the app will use. It searches 100 nm first and only
   widens (200, 400, 800 ... up to your setting) when it has not found enough stations, so a large
   value costs nothing in busy areas and still finds coastal stations on Atlantic/Pacific crossings.
   Searches that cross the 180-degree meridian (Pacific) are handled.
   For oceanic routes use 500-1000 nm.
3. Minimizing sends it to the system tray; double-click the tray icon to restore.

## Notes
- The app switches FSX to *custom weather* mode. To go back to normal, choose a weather option
  in FSX (World > Weather) after closing the app.
- Settings are stored in `%AppData%\FsxWeatherInjector\settings.ini`.
- `WeatherSetObservation(0, ...)` applies weather immediately; edit `SimLink.Inject` for slower transitions.
- Everything is logged to `%AppData%\FsxWeatherInjector\log.txt` (Help > Open Log File).

## SimConnect startup check
On startup the app verifies that SimConnect can really be loaded. If not, it shows a plain-language
message with the fix instead of crashing, and logs the managed DLL version/path, the FSX folder and
the FSX `SimConnect.dll` version. Help > Check SimConnect Installation runs the same check on demand.

### Testing the check
1. **Normal case:** start the app; the log contains `SimConnect check: Ok ...`. Help > Check SimConnect
   Installation shows the versions it found.
2. **Simulate every failure dialog** (Visual Studio: Project > Properties > Debug > Command line arguments,
   or run the exe from a Command Prompt):
   `FsxWeatherInjector.exe --simulate-simconnect-error=managed`   (also: `native`, `bitness`, `unknown`, `ok`)
   `managed`, `native` and `bitness` show the error dialog and exit, exactly like the real failure.
   `unknown` is logged and the app keeps running. Simulated dialogs say "(SIMULATED)" in the title.
3. **Real failure test (missing managed DLL):** close the app, rename
   `bin\Debug\net48\Microsoft.FlightSimulator.SimConnect.dll` to `.bak`, run the exe, expect the
   "managed" dialog, then rename it back. (If the DLL is also installed in the Windows GAC this test will
   not trigger.) The "native" failure is only practical to test with the simulate switch.

## Release checklist
1. Build in **Release**, platform **x86**.
2. Zip the contents of `bin\Release\net48` (exe, app.config, and the SimConnect managed DLL if you are
   allowed to include it; see the note below).
3. Tell users: FSX must be installed (it installs the SimConnect runtime); if the app reports a SimConnect
   problem, follow the message in the dialog.
4. Test once on a machine/profile without your SDK folder, and with FSX closed and open.

Note on distribution: Microsoft's SDK ReadMe does not contain a licence. Until the SDK's EULA is checked,
ask users to copy `Microsoft.FlightSimulator.SimConnect.dll` from their own FSX SDK folder.
