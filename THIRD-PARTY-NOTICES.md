# Third-party notices

## Mutagen (GPL-3.0-only)

Groundskeeper links the Mutagen libraries (Mutagen.Bethesda.Skyrim and its dependencies, including
Noggog) by Noggog and the Mutagen-Modding contributors, licensed under the GNU General Public License,
version 3 only. <https://github.com/Mutagen-Modding/Mutagen>

Mutagen keeps its own licence and is not relicensed by Groundskeeper's licence. Its terms are at
<https://www.gnu.org/licenses/gpl-3.0.html>.

## System.Drawing.Common (MIT)

Copyright (c) .NET Foundation and Contributors. Used to read road mask images.

## Nifly / NiflySharp (GPL-3.0)

The floating-object pass reads NIF files and Havok collision with **Nifly** 1.1.0 (the NiflySharp library by
ousnius, <https://github.com/ousnius/NiflySharp>), a pure C# library licensed GPL-3.0. It ships with
Groundskeeper as `NiflySharp.dll` next to the program and keeps its own licence.

## PyNifly (reference only)

An earlier version of the floating-object pass used PyNifly (by BadDogSkyrim,
<https://github.com/BadDogSkyrim/PyNifly>) through Python. That dependency is gone; PyNifly is no longer
used or included. Its output was used once, as the reference to test the in-process reader against.

## ACMOS Road Generator road images (CC BY-NC-SA 4.0), not bundled

The road pass reads the road overlay PNG tiles of ACMOS Road Generator by DoubleYouC
(<https://github.com/DoubleYouC/ACMOS-Road-Generator>, Nexus mod 79205), which are licensed CC BY-NC-SA 4.0 by
their author. They are **not** included in or distributed with Groundskeeper; you supply them from the
original mod. Groundskeeper only reads them, and does not modify or redistribute them.

## Landscape Fixer by Cl3mus33

No code from Landscape Fixer is included. Groundskeeper only leaves its `LandscapeFixer.esp` out of the
terrain scan, so the two can run side by side. See CREDITS.md.
