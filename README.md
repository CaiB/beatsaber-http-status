# Beat Saber HTTP Status: CaiB's 1.11.1 Fork

This plugin exposes information about the current game status, live over a WebSocket and over HTTP.

This fork is where I've made some enhancements and additions to support [my custom stream overlay](https://github.com/CaiB/SaberStream), and is only intended to run on Beat Saber version 1.11.1 with the unofficial multiplayer mod.

Additional data that is available compared to the upstream 1.11.1 version:
- Statistics for each difficulty of the current map
  - Whether or not you have gotten a full combo previously
  - Previous best score
  - Previous best rank
  - Previous max combo
  - Number of times map has been played
  - Whether a score has been set
- Location of the currently played level on disk
- The time in the song of every object that is hit

SteamVR's poor reliability and performance has caused me to lose interest in VR, hence I sold my hardware and no longer play Beat Saber. As such, this repository is archived and unmaintained.

Below is the remainder of the general info about the mod.

# Upstream Mod Info

## Installation

### Recommended (using a mod manager)

You can install Beat Saber HTTP Status by using [one of the mod installers listed here](https://bsmg.wiki/pc-modding.html). Follow the steps required to run the program of your choice, then in the mods section find "HTTP Status" and install it. This will automatically install and keep everything you need up to date.

Next you will need to [get additional software](https://github.com/opl-/beatsaber-http-status/wiki/Software-using-this-plugin) that uses this plugin. **This plugin does nothing useful on its own; it simply exposes information for other programs to use.**

### Manual

1. Install [BSIPA](https://bsmg.github.io/BeatSaber-IPA-Reloaded/) [(BSMG guide)](https://bsmg.wiki/pc-modding.html#manual-installation).

2. Download the latest release from the [releases page](https://github.com/opl-/beatsaber-http-status/releases).

3. Extract the zip into your Beat Saber directory.

4. Download and extract the following plugins and their dependencies:

	- BS Utils from [BeatMods](https://beatmods.com/#/mods)

5. [Get additional software](https://github.com/opl-/beatsaber-http-status/wiki/Software-using-this-plugin) that makes use of this plugin. This mod does nothing on its own; it simply exposes information for other programs to use.


## Developers

### Using HTTP Status

Protocol documentation can be found in [protocol.md](protocol.md).

### Contributing to HTTP Status

Before opening a pull request, please read the [contributing guide](CONTRIBUTING.md).

This project uses the `websocket-sharp` library included as a git submodule. To download it, use `git submodule update --init` or clone the repository with the `--recursive` flag.

To build this project you will need to create a `BeatSaberHTTPStatus/BeatSaberHTTPStatusPlugin.csproj.user` file specifying where the game is located on your disk:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <!-- Change this path if necessary. Make sure it ends with a backslash. -->
    <GameDirPath>C:\Program Files\Steam\steamapps\common\Beat Saber\</GameDirPath>
  </PropertyGroup>
</Project>
```

Alternatively you can provide the game DLLs in the `libs/beatsaber` directory using the standard Beat Saber directory structure. For a full list see the [project file](BeatSaberHTTPStatus/BeatSaberHTTPStatusPlugin.csproj).

The following properties can be specified either in the `.csproj.user` file or through the command line (`/p:<name>=<value>`):

- `GameDirPath`: Path ending with a backslash pointing to the Beat Saber directory. Used to locate required game DLLs.

- `OutputZip` = `true`/`false`: Enable/disable generating the .zip file. Can be used to get a zip for the `Debug` configuration.

- `CopyToPlugins` = `true`/`false`: Enable/disable copying of the websocket library and HTTP Status DLLs to the Beat Saber installation. Depends on `GameDirPath`.


## Credits

**xyonico** for the [Beat Saber Discord Presence](https://github.com/xyonico/BeatSaberDiscordPresence) plugin, on which this plugin was initially based.

**sta** for the [websocket-sharp](https://github.com/sta/websocket-sharp) library.

**Maxaxik** for testing and helping with research.
