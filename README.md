https://github.com/sibercat/Pippi-HotFix/releases/download/1/Pippi.pak

Conan Exiles build 5.6.1-373004 relocated two engine functions that Pippi 4.0.6 calls. The mod still looks for them at their old address, finds nothing, and the game dies instantly. Four eight-byte values in the mod's cooked data.

### What actually changed

Funcom added Stable ID variants of the character and inventory loaders. Rather than duplicate the result-handling logic into the new classes, they hoisted it into a shared base class. The functions were not deleted and not renamed — the names are byte-identical. They changed owner.

![image](https://github.com/sibercat/Pippi-HotFix/blob/main/change.png)

That is normally a harmless refactor. It is fatal here because of how cooked content stores a reference to engine code. In an IoStore package, a script import is not a name lookup — it is a CityHash64 of the function's full object path. Move a function to a different class and the hash becomes unrelated:
![image](https://github.com/sibercat/Pippi-HotFix/blob/main/chage2.png)


There is no name-based fallback and no redirector for script imports. The loader hashes, misses, and stores null — silently, with no warning in the log. The Blueprint VM then reaches an EX_CallMath instruction holding that null function pointer and dereferences it at offset +0x20 to find its owning class. Hence an access violation reading address 0x20: the offset is the null pointer plus the field it tried to read.

### What the fix does

Four import-table entries — one per affected Blueprint package — are repointed to the new path hashes, across all three platform containers in the mod:
![image](https://github.com/sibercat/Pippi-HotFix/blob/main/chage3.png)

Eight bytes each, same size, in a package header. No bytecode is modified and no script offset moves, so no jump target, skip offset, or switch table is disturbed. It is also semantically exact rather than a workaround: the same function runs with the same signature, named at its new location. Because the old proxy classes inherit from the new base classes, this is the identical code that was always executing.

![image](https://github.com/sibercat/Pippi-HotFix/blob/main/chage4.png)

## Install

**Easiest way — download `PippiHotFix.exe` from the
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

PippiHotFix.exe       24,576 bytes
  sha256  08362ac81bde78447ee425b0fef4c21a426ba5cfbc3a186f413b718b751649ea
```

On Windows:

```
certutil -hashfile Pippi.pak SHA256
certutil -hashfile PippiHotFix.exe SHA256
```

### Don't trust me — rebuild the installer

The installer's full source is in [`launcher/`](launcher/), and the build is
**reproducible**: run `build.bat` and you get a byte-identical
`PippiHotFix.exe` with exactly the SHA-256 above. If your rebuild matches, the
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
- `PippiHotFix.exe /check` prints the whole diagnosis as text, which is handy
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
