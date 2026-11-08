# SnipeFeed

Your BeatLeader following feed inside Beat Saber, so you can see what your friends just played and go snipe it without
taking the headset off. It's a **Snipe Feed** tab under **Mods** on the left panel in song select, and it also shows your
own profile.

Quest build is **3.4.0** for Beat Saber **1.45.3**. Still on 1.40.8? Get
[3.4.0 for 1.40.8](https://github.com/NotNic182/SnipeFeed/releases/tag/BS-1.40.8-v3.4.0). The PC version lives in
[pcvr/](pcvr/).

<img width="1707" height="960" alt="0c6e45dd00c9487bbaaa30c780d7ea7e" src="https://github.com/user-attachments/assets/d5dd1f6a-66b1-4db8-92da-e232a11088ff" />

<img width="1707" height="960" alt="0c6e45dd00c9487bbaaa30c780d7ea7e" src="https://github.com/user-attachments/assets/03d5690f-c39e-4863-81f2-cd2300d44764" />

<img width="3840" height="2160" alt="915e0a83408e430386ea61d8ac17cca7" src="https://github.com/user-attachments/assets/80392e62-a826-49ed-a03b-92fd119c0e15" />

<img width="3840" height="2160" alt="6970d58f2e0d419395f96ee3aab8494d" src="https://github.com/user-attachments/assets/dbe2ecc0-7f42-4397-b61a-5c257a632ec5" />

## Install

Get it from the mod list in [ModsBeforeFriday](https://mbf.bsquest.xyz), or drag the qmod from
[Releases](https://github.com/NotNic182/SnipeFeed/releases) into MBF or QuestPatcher. BSML and SongCore get installed
with it.

If you're logged into the BeatLeader mod it just uses that. If not, put your BeatLeader ID (the number in your profile
URL) in `BeatLeader Player ID` in `ModData/com.beatgames.beatsaber/Configs/SnipeFeed.json`.

## Using it

- **Following** shows your friends' newest scores, filter by player with the dropdown
- **My Profile** shows your scores. Sort by date, pp, acc/pass/tech pp, acc, score, rank, stars, plays, pauses, streak,
  nominations, watched or mistakes, and flip the order with the button next to it
- The triangle on each score is BeatLeader's pass/acc/tech rating for the map
- Tap a score for details and **Play** / **Download & Play**. In a multiplayer lobby it only downloads, pick the song in
  the lobby after. OST/DLC songs can't be downloaded
- **Scores** sets how many load (10-100)

## Building

Needs qpm, CMake, Ninja and PowerShell 7. qpm grabs the right NDK.

```
qpm restore
qpm s build
qpm s qmod
```

Not affiliated with BeatLeader, BeatSaver or Beat Games.
