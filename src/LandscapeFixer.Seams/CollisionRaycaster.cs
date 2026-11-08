// Real Havok collision-geometry awareness for FloatingObjectFixer, replacing
// its previous terrain-heightmap-only "raw terrainZ" concept with an actual
// "what does the engine's own collision say is here" answer where available.
//
// WHY THIS EXISTS: confirmed via real in-game screenshots 2026-09-12 that the
// heightmap-only approach sends objects BELOW the visible ground whenever
// unmodeled rock/architecture geometry sits above the raw landscape at that
// XY - in two unrelated locations the same night (a RoadMaskMerge-carved pool
// bank, and an unrelated Riverwood stone bridge). No amount of heightmap-side
// gate-tuning can close this: the heightmap genuinely does not contain the
// information needed. The user's explicit direction: "do it right" - real
// collision geometry, not another heuristic.
//
// ARCHITECTURE: NIF/Havok reading lives in NifCollisionReader.cs (the pure-C# Nifly library). It was validated triangle-for-triangle against the earlier PyNifly-based extractor on 269 real NIFs
// (compressed mesh, box, convex vertices, list and transform shapes; worst difference 0.05 units, i.e. float rounding), which is why the
// Python and PyNifly dependency is gone. This file does only the transform + raycast math, where the rest of this tool's placement
// logic already lives.
//
// Mutagen itself has NO 3D transform math (confirmed by inspecting the
// Mutagen-Modding/Mutagen source directly - Placement is a pure data record,
// P3Float Position/Rotation with no accompanying matrix logic anywhere in
// the library). The exact Skyrim REFR rotation matrix convention could not
// be independently confirmed via web search before implementation (the
// authoritative CK wiki page blocks automated fetches). Implemented here
// using the standard right-handed Z*Y*X Euler convention, matching NIF's own
// native per-node rotation-matrix representation (which IS unambiguously
// documented) - REQUIRES empirical validation against real, known-problem
// references (position/rotation known, approximate correct world Z inferable
// from context) before this is trusted for auto-correction. See
// ValidateAgainstKnownRefs below and the 2026-09-12 memory notes for the
// specific validation refs used.

using System.Diagnostics;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace SeamFinder.Core;

public readonly record struct Vec3(float X, float Y, float Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
}

public readonly record struct Tri3(Vec3 A, Vec3 B, Vec3 C);

// One placed instance whose collision (if any) should be checked: the NIF to
// read, and the world transform to place it with (from its own REFR).
public readonly record struct CollisionCheckInstance(
    string NifPath, Vec3 Position, Vec3 RotationRadians, float Scale, FormKey RefFormKey);

public record ExtractedCollision(bool Ok, string? Reason, List<Tri3> LocalTriangles, List<string> ShapeTypes, bool Approx);

public static class CollisionRaycaster
{
    // Reads the real Havok collision of every unique NIF in-process (NifCollisionReader, on the pure-C# Nifly library). Cached by PATH, never by placed instance: the same handful of rock/architecture meshes are
    // reused by hundreds of placed instances across the world, which is what keeps this practical at the scale
    // FindCandidates already operates at (100k+ placed objects checked). One bad or collision-less NIF never affects the
    // others: every path gets its own Ok/Reason result.
    public static Dictionary<string, ExtractedCollision> ExtractCollisionBatch(
        IEnumerable<string> nifPaths, Action<string> log)
    {
        var uniquePaths = nifPaths.Distinct().ToList();
        var result = new Dictionary<string, ExtractedCollision>();
        if (uniquePaths.Count == 0) return result;

        foreach (var path in uniquePaths) result[path] = NifCollisionReader.Extract(path);

        var ok = result.Values.Count(r => r.Ok);
        var failed = result.Values.Where(r => !r.Ok && r.Reason is not null && !r.Reason.StartsWith("no collision object") && !r.Reason.StartsWith("no ground-relevant"))
            .GroupBy(r => r.Reason![..Math.Min(80, r.Reason.Length)]).Select(g => $"{g.Count()}x {g.Key}").Take(3).ToList();
        log($"  CollisionRaycaster: read collision for {uniquePaths.Count} meshes ({ok} with usable geometry)." +
            (failed.Count > 0 ? $" Unreadable: {string.Join("; ", failed)}." : ""));
        return result;
    }

    // Standard right-handed Z*Y*X Euler rotation matrix, matching NIF's own
    // native per-node rotation-matrix convention (see file header on why this
    // was chosen absent independent web confirmation of the REFR-specific
    // convention, and why it MUST be validated empirically before trust).
    static (Vec3 Row0, Vec3 Row1, Vec3 Row2) RotationMatrix(Vec3 eulerRadians)
    {
        var (sx, cx) = (MathF.Sin(eulerRadians.X), MathF.Cos(eulerRadians.X));
        var (sy, cy) = (MathF.Sin(eulerRadians.Y), MathF.Cos(eulerRadians.Y));
        var (sz, cz) = (MathF.Sin(eulerRadians.Z), MathF.Cos(eulerRadians.Z));

        // Rx
        var rx = ((1f, 0f, 0f), (0f, cx, -sx), (0f, sx, cx));
        // Ry
        var ry = ((cy, 0f, sy), (0f, 1f, 0f), (-sy, 0f, cy));
        // Rz
        var rz = ((cz, -sz, 0f), (sz, cz, 0f), (0f, 0f, 1f));

        // R = Rz * Ry * Rx (each a 3x3 tuple-of-tuples; multiply then flatten to rows).
        static (float, float, float) MulRow((float, float, float) rowA, ((float, float, float), (float, float, float), (float, float, float)) b)
        {
            var (a0, a1, a2) = rowA;
            var (b0, b1, b2) = b;
            return (
                a0 * b0.Item1 + a1 * b1.Item1 + a2 * b2.Item1,
                a0 * b0.Item2 + a1 * b1.Item2 + a2 * b2.Item2,
                a0 * b0.Item3 + a1 * b1.Item3 + a2 * b2.Item3
            );
        }
        var ryx = (MulRow(ry.Item1, rx), MulRow(ry.Item2, rx), MulRow(ry.Item3, rx));
        var zyx = (MulRow(rz.Item1, ryx), MulRow(rz.Item2, ryx), MulRow(rz.Item3, ryx));

        return (
            new Vec3(zyx.Item1.Item1, zyx.Item1.Item2, zyx.Item1.Item3),
            new Vec3(zyx.Item2.Item1, zyx.Item2.Item2, zyx.Item2.Item3),
            new Vec3(zyx.Item3.Item1, zyx.Item3.Item2, zyx.Item3.Item3)
        );
    }

    static Vec3 ApplyRotation((Vec3 Row0, Vec3 Row1, Vec3 Row2) r, Vec3 v) => new(
        r.Row0.X * v.X + r.Row0.Y * v.Y + r.Row0.Z * v.Z,
        r.Row1.X * v.X + r.Row1.Y * v.Y + r.Row1.Z * v.Z,
        r.Row2.X * v.X + r.Row2.Y * v.Y + r.Row2.Z * v.Z);

    public static List<Tri3> ToWorldSpace(List<Tri3> local, Vec3 position, Vec3 rotationRadians, float scale)
    {
        var r = RotationMatrix(rotationRadians);
        Vec3 Xform(Vec3 v) => position + ApplyRotation(r, v * scale);
        var outTris = new List<Tri3>(local.Count);
        foreach (var t in local)
            outTris.Add(new Tri3(Xform(t.A), Xform(t.B), Xform(t.C)));
        return outTris;
    }

    // Vertical "raycast": for a fixed (x,y) column, find the HIGHEST Z at
    // which any triangle's XY-projection contains the point, via 2D
    // barycentric test + interpolation. This is a simplification of a true
    // 3D ray-triangle intersection that is exactly equivalent for a
    // perfectly vertical ray and is both simpler and faster - no need for
    // Möller–Trumbore here since two of the three ray-direction components
    // are always zero.
    public static float? HighestSurfaceZ(IEnumerable<Tri3> worldTriangles, float x, float y)
    {
        var list = worldTriangles as IReadOnlyList<Tri3> ?? worldTriangles.ToList();
        float? best = null;
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            // Cheap reject: a point clearly outside the triangle's XY box cannot pass the barycentric test below.
            // The margin covers that test's -1e-4 edge tolerance (<= 1e-4 x the triangle's altitude <= its XY diagonal), so results are unchanged.
            float minX = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)), maxX = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
            float minY = MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)), maxY = MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y));
            float m = 2e-4f * ((maxX - minX) + (maxY - minY)) + 1e-3f;
            if (x < minX - m || x > maxX + m || y < minY - m || y > maxY + m) continue;
            var z = TryInterpolateZ(t, x, y);
            if (z.HasValue && (best is null || z.Value > best.Value))
                best = z.Value;
        }
        return best;
    }

    // Added 2026-09-13, per the user's own request: a single raycast at the
    // candidate's own origin point can miss real contact that only exists
    // under a DIFFERENT part of the object's footprint (its actual edge, not
    // its center) - "if nothing is touching it, flag it" needs to actually
    // check more than one point first. Materializes worldTriangles to a list
    // once (the single-point caller iterates the same enumerable multiple
    // times too via ToWorldSpace's own cached List<Tri3>, so this doesn't
    // change allocation behavior) and reuses HighestSurfaceZ per sample
    // point, taking the max found across all of them - same "highest
    // relevant surface, not the lowest" reasoning as
    // HeightmapDecoder.GetMaxHeightInFootprint. Skips the extra ring
    // entirely for a near-zero footprint (most small clutter meshes),
    // since a single point is already sufficient there and the ring only
    // adds raycast cost for objects genuinely large enough to matter.
    public static float? HighestSurfaceZInFootprint(IReadOnlyList<Tri3> worldTriangles, float centerX, float centerY, float footprintRadius)
    {
        var best = HighestSurfaceZ(worldTriangles, centerX, centerY);
        if (footprintRadius < 20f) return best;
        for (int deg = 0; deg < 360; deg += 90)
        {
            var rad = deg * Math.PI / 180.0;
            var sx = centerX + (float)(footprintRadius * Math.Cos(rad));
            var sy = centerY + (float)(footprintRadius * Math.Sin(rad));
            var z = HighestSurfaceZ(worldTriangles, sx, sy);
            if (z.HasValue && (best is null || z.Value > best.Value)) best = z.Value;
        }
        return best;
    }

    static float? TryInterpolateZ(Tri3 t, float px, float py)
    {
        // Barycentric coordinates in the XY plane.
        float x1 = t.A.X, y1 = t.A.Y, x2 = t.B.X, y2 = t.B.Y, x3 = t.C.X, y3 = t.C.Y;
        float denom = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
        if (MathF.Abs(denom) < 1e-6f) return null; // degenerate (near-vertical) triangle, skip

        float w1 = ((y2 - y3) * (px - x3) + (x3 - x2) * (py - y3)) / denom;
        float w2 = ((y3 - y1) * (px - x3) + (x1 - x3) * (py - y3)) / denom;
        float w3 = 1f - w1 - w2;

        const float eps = -1e-4f; // small tolerance for edge/vertex hits
        if (w1 < eps || w2 < eps || w3 < eps) return null;

        return w1 * t.A.Z + w2 * t.B.Z + w3 * t.C.Z;
    }
}
