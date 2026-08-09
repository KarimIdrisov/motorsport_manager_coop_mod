# Motorsport Manager Coop Launcher

The launcher stores its settings in `launcher.json`, updates the `Mod` folder
from the configured Git repository, installs missing UMM/Doorstop files from
the bundled `Loader` directory, starts the LAN server, and launches `MM.exe`.

Build a self-contained Windows executable with:

```powershell
dotnet publish .\MotorsportManagerCoopLauncher.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```
