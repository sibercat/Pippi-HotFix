Conan Exiles build 5.6.1-373004 relocated two engine functions that Pippi 4.0.6 calls. The mod still looks for them at their old address, finds nothing, and the game dies instantly. Four eight-byte values in the mod's cooked data repair it.

Funcom added Stable ID variants of the character and inventory loaders. Rather than duplicate the result-handling logic into the new classes, they hoisted it into a shared base class. The functions were not deleted and not renamed — the names are byte-identical. They changed owner.


![image](https://github.com/sibercat/Pippi-HotFix/blob/main/change.png)

That is normally a harmless refactor. It is fatal here because of how cooked content stores a reference to engine code. In an IoStore package, a script import is not a name lookup — it is a CityHash64 of the function's full object path. Move a function to a different class and the hash becomes unrelated:
![image](https://github.com/sibercat/Pippi-HotFix/blob/main/chage2.png)
