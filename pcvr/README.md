# SnipeFeed PC

The PC version of SnipeFeed for Beat Saber **1.44.1** and **1.40.8**. Same Snipe Feed tab under Mods in song select, with
your BeatLeader following feed, My Profile with your scores in any of BeatLeader's sorts, skill triangles and
Play / Download & Play.

## Install

Needs BSIPA, BSML 1.12.3+ and SongCore. Extract the zip for your game version from
[Releases](https://github.com/NotNic182/SnipeFeed/releases) into your Beat Saber folder (the dll goes in `Plugins`).

If the BeatLeader mod is installed and signed in it uses that login. Otherwise set `PlayerId` in
`UserData/SnipeFeedPC.json`.

## Building

Needs the .NET SDK and a modded Beat Saber 1.44.1 or 1.40.8 install. The game version in the manifest comes from that
install, and the build copies the dll into its `Plugins` folder.

```
dotnet build SnipeFeed.PC.csproj -c Release -p:BeatSaberDir="C:\Program Files (x86)\Steam\steamapps\common\Beat Saber"
```
