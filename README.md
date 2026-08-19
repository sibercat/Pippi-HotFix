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
