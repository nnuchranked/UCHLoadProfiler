# UCH Load Profiler

A BepInEx 5 / Harmony plugin for profiling slow frames and method execution in *Ultimate Chicken Horse*.

## Requirements

- Windows and PowerShell 5.1 or later
- A .NET Framework 4.x-compatible C# compiler (`csc.exe`)
- Local reference assemblies from the game and BepInEx 5:
  - `UltimateChickenHorse_Data/Managed/*.dll`
  - `BepInEx/core/BepInEx.dll`
  - `BepInEx/core/0Harmony.dll`

The game and BepInEx reference DLLs are intentionally not included. They are supplied by the game/BepInEx installation; do not commit or redistribute them in this repository.

## Build

Run PowerShell from the repository root. The DLL is written to `build/LoadProfiler.dll`.

If the game is installed in the default Steam library location:

```powershell
.\build.ps1
```

For a custom Steam library/game path:

```powershell
.\build.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Ultimate Chicken Horse' -Name LoadProfiler
```

Or provide the two reference directories separately:

```powershell
.\build.ps1 `
  -ManagedPath 'D:\Games\UCH\UltimateChickenHorse_Data\Managed' `
  -BepInExCorePath 'D:\Games\UCH\BepInEx\core' `
  -Name LoadProfiler
```

If the compiler is not found automatically:

```powershell
.\build.ps1 -CompilerPath 'C:\Path\To\csc.exe' -GamePath 'D:\Games\UCH'
```

The script compiles all `Source\*.cs` files using C# 5 compatibility and validates the required assemblies before compiling.

## Optional: build and install

```powershell
.\build.ps1 `
  -GamePath 'D:\SteamLibrary\steamapps\common\Ultimate Chicken Horse' `
  -Name LoadProfiler `
  -PluginFolder 'LoadProfiler' `
  -Install
```

This copies the DLL to `BepInEx\plugins\LoadProfiler\LoadProfiler.dll`. Without `-Install`, the script only builds and does not modify the game installation.

## Optional local reference layout

You can keep local copies of the references in this layout, then run `.\build.ps1` without specifying paths (assuming the files are in these locations):

```text
refs/
  Managed/             # local game Managed DLLs
  BepInEx/
    core/              # BepInEx.dll and 0Harmony.dll
```

The `refs/` folder is local-only and must not be committed. It must contain `Assembly-CSharp.dll` in `refs/Managed` and `BepInEx.dll` plus `0Harmony.dll` in `refs/BepInEx/core`.

## GitHub repository hygiene

Add this `.gitignore` content to avoid accidentally committing generated output or local reference assemblies:

```gitignore
/build/
/refs/
*.user
*.suo
```

GitHub Actions cannot build this project from source alone unless the required game/BepInEx reference assemblies are supplied to the runner through a lawful, private mechanism. Do not commit proprietary game assemblies just to make CI pass.
