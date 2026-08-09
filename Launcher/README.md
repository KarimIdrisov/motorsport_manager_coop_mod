# Motorsport Manager Coop Launcher

The launcher stores its settings in `launcher.json`, updates the `Mod` folder
from the configured Git repository, installs missing UMM/Doorstop files from
the bundled `Loader` directory, starts the LAN server, and launches `MM.exe`.

Build a self-contained Windows executable with:

```powershell
dotnet publish .\MotorsportManagerCoopLauncher.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```
# Launcher modes

- **Host:** install/update the mod and start Motorsport Manager. The launcher sets
  `MM_COOP_AUTOSTART=host`, so the embedded LAN server starts automatically.
- **Race controller:** enter the Host computer's LAN IP and open **Пульт второго
  пилота**. The second computer does not need the game, Unity Mod Manager, or a save.

The controller becomes active when Host enters Practice, Qualifying, or Race and
telemetry for the player cars is available.
