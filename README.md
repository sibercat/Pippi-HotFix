## Install

**Easiest way — download `PippiHotFix-Installer.exe` from the
[latest release](https://github.com/sibercat/Pippi-HotFix/releases/latest) and
run it with the game closed.**

It finds Pippi across every Steam library on your machine, checks what you
currently have by checksum, and replaces it with one button — keeping a copy of
your original so the change can be undone. It also tells you when a newer hot
fix is out.

Windows will warn you the first time you run it: click **More info** →
**Run anyway**. That warning appears because the program is not code-signed
(certificates cost money), not because anything is wrong with it. If you would
rather not run a program at all, install by hand instead.

### By hand

1. Fully close Conan Exiles and Steam.
2. Download `Pippi.pak` from the latest release.
3. Replace the file at
   `steamapps\workshop\content\440900\3725018456\Pippi.pak`
4. Start the game and connect as normal.

**Check it worked:** right-click `Pippi.pak` → Properties.
**47,969,641 bytes** is fixed. **45,970,706 bytes** is the broken file.

### Server owners

Replace the same file in your server's workshop folder, then delete the cached
copies at `ConanSandbox\Saved\ExtractedMods\Pippi-*`. If your launcher runs
SteamCMD with the `validate` flag, turn it off — it restores the broken file on
every start and will make a correct fix look like it failed. The installer
clears the extracted cache for you automatically.

### Important

Steam puts the broken file back whenever it updates Workshop items or verifies
your game files. If crashes suddenly return, run the installer again. To stop it
happening, turn off auto-update for Pippi in your Workshop subscriptions.

Everyone connecting to a fixed server needs the fixed file — client and server
must match.

Nothing is lost: no character, building, or inventory data is touched.

## Verify what you downloaded

```
Pippi.pak         47,969,641 bytes
  sha256  2c3f49638decf3542f0c757b3a16f569a0576cbc39941890b149c30355e1521f

PippiHotFix-Installer.exe       25,600 bytes
  sha256  7f7a496b6852170d9ebfae6e7ee31b989a9f352127724d1574a6a38d765083bb
```

On Windows:

```
certutil -hashfile Pippi.pak SHA256
certutil -hashfile PippiHotFix-Installer.exe SHA256
```

### Don't trust me — rebuild the installer

The installer's full source is in [`launcher/`](launcher/), and the build is
**reproducible**: run `build.bat` and you get a byte-identical
`PippiHotFix-Installer.exe` with exactly the SHA-256 above. If your rebuild matches, the
published exe provably contains nothing but the source you just read.

Rebuilding needs any .NET SDK plus the .NET Framework 4.8 reference assemblies.
Running the installer needs neither — it targets the .NET Framework already
built into Windows 10 and 11.

## What the installer does

- Finds `Pippi.pak` across every Steam library, via the registry and
  `libraryfolders.vdf`
- Identifies what you have by SHA-256 rather than guessing from the file name
- Refuses to run while Conan Exiles is open
- Backs up your original before replacing anything
- Verifies the download's SHA-256 — published by GitHub — before installing
- Clears a dedicated server's `ExtractedMods` cache
- **Restore original file** puts the stock pak back, for when Pippi is updated
  officially
- `PippiHotFix-Installer.exe /check` prints the whole diagnosis as text, which is handy
  for helping someone over Discord

It reads this repository's releases feed, so a future hot fix needs a new
release, not a new installer.

## Credits and scope

Pippi is by **Joshtech**. This is an unofficial community stopgap produced while
he is away, so servers can keep running. It is not affiliated with or endorsed
by Funcom, and it should be replaced by an official build the moment one exists.
When that happens, use **Restore original file** in the installer, or re-enable
Workshop auto-update, so Steam puts the official version back.

This repairs the two relocated calls and nothing else. It does not certify Pippi
4.0.6 against every other change in the Enhanced patch — other breakage would
show up as different symptoms, not this crash.
