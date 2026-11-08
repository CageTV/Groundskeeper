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

## PyNifly (GPL-3.0), optional and not bundled

The floating-object pass can use PyNifly (by BadDogSkyrim, <https://github.com/BadDogSkyrim/PyNifly>) to
read mesh collision. It would run as a separate process, exchanging JSON over a pipe; it is **not**
included in or linked into Groundskeeper.

## Landscape Fixer by Cl3mus33

No code from Landscape Fixer is included. Groundskeeper only leaves its `LandscapeFixer.esp` out of the
terrain scan, so the two can run side by side. See CREDITS.md.
