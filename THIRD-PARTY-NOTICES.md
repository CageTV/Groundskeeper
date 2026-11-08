# Third-party notices

## Mutagen (GPL-3.0-only)

Groundskeeper links the Mutagen libraries (Mutagen.Bethesda.Skyrim and its dependencies, including
Noggog) by Noggog and the Mutagen-Modding contributors, licensed under the GNU General Public License,
version 3 only. <https://github.com/Mutagen-Modding/Mutagen>

Because a built Groundskeeper links Mutagen, **the built program is distributed under GPL-3.0**. You
may use, study, change and share it under those terms. The full source of this project is available in
this repository, which is the "Corresponding Source" for any binary built from it. The full licence text
is at <https://www.gnu.org/licenses/gpl-3.0.html>.

## System.Drawing.Common (MIT)

Copyright (c) .NET Foundation and Contributors. Used to read road mask images.

## niflysharp and nifly (GPL-3.0)

The floating-object pass reads NIF files and Havok collision with **niflysharp** 2.0.4 (C# bindings by
SteveTownsend, <https://github.com/SteveTownsend/niflyswig>) of **nifly** (by ousnius,
<https://github.com/ousnius/nifly>), licensed GPL-3.0. It ships with Groundskeeper as `nifly.dll` and
`niflycpp.dll` next to the program, and is part of the combined work distributed under the GPL-3.0.

## PyNifly (reference only)

An earlier version of the floating-object pass used PyNifly (by BadDogSkyrim,
<https://github.com/BadDogSkyrim/PyNifly>) through Python. That dependency is gone; PyNifly is no longer
used or included. Its output was used once, as the reference to test the in-process reader against.

## Landscape Fixer by Cl3mus33

No code from Landscape Fixer is included. Groundskeeper only leaves its `LandscapeFixer.esp` out of the
terrain scan, so the two can run side by side. See CREDITS.md.
