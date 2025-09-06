<img align="left" width="128" height="128" src="https://github.com/remiteeple/PerfectPlacement/blob/main/About/ModIcon.png">

### Perfect Placement 

This mod enhances object placement by allowing you to rotate objects with the mouse before placing them. It also provides options to either keep the original rotation when reinstalling or to force a specific rotation for any build, install, or reinstall action.

---

### Mod Features
- **Mouse Rotation:** Hold the left mouse button to pin an object and rotate it with your mouse before placing it, similar to building in The Sims.
- **Keep Original Rotation:** When reinstalling an object, it will maintain its original rotation by default.
- **Rotation Override:** Set a specific rotation (North, East, South, West) that will be applied to all newly built, installed, or reinstalled objects.

### Mod Settings
All features can be configured in the mod settings menu under "Placement Plus". You can:
- Enable or disable mouse rotation globally.
- Choose between keeping the original rotation or using an override for reinstalling.
- Enable and configure override rotations for building and installing objects.

### Build (dotnet CLI)

#### Prerequisites
- .NET SDK 8.0 or newer (8.x/9.x supported)
- Windows: no extra setup required.
- Linux/macOS: if you see a ".NET Framework targeting pack not found" error for `net472`, add reference assemblies once:
  - `dotnet add Source/PerfectPlacement.csproj package Microsoft.NETFramework.ReferenceAssemblies -v 1.0.3`

#### Quick builds
- Latest available RimWorld refs:
  - `dotnet build Source/PerfectPlacement.csproj -c Release`
- Pin to a specific series (example 1.6):
  - `dotnet restore Source/PerfectPlacement.csproj -p:GameVersion=1.6`
  - `dotnet build Source/PerfectPlacement.csproj -c Release -p:GameVersion=1.6 --no-restore`

Output DLLs are placed under `<MAJOR.MINOR>/Assemblies/PerfectPlacement.dll` (e.g., `1.6/Assemblies/PerfectPlacement.dll`).

### Build (Visual Studio)
- Open `PerfectPlacement.sln` in Visual Studio 2022 or newer.
- Select a configuration that includes the game version:
  - `Debug-1.4`, `Debug-1.5`, `Debug-1.6`
  - `Release-1.4`, `Release-1.5`, `Release-1.6`
- Build the project. Output is written to `<MAJOR.MINOR>/Assemblies/PerfectPlacement.dll` (e.g., `1.5/Assemblies/PerfectPlacement.dll`).
- These configurations set `GameVersion` internally to match the CLI output location, so VS builds and `dotnet build -p:GameVersion=...` produce identical folder layouts.

### Install
- Copy this mod folder into `RimWorld/Mods/`.
- Enable **Placement Plus** in the in-game mod list and restart.

### Supported Versions
- 1.4, 1.5, 1.6 (CI builds for each)
