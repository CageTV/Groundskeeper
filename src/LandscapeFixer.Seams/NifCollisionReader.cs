// Reads Havok collision geometry from a NIF in-process, replacing the old extract_collision.py + PyNifly +
// Python dependency (the cause of the "Missing PyNifly & SciPy" bug reports).
//
// NIF/Havok structure comes from the pure-C# `Nifly` package (NiflySharp by ousnius, GPL-3.0; block classes generated from nif.xml):
// no native DLLs and no Visual C++ runtime needed. It exposes the typed shape chain and the compressed-mesh chunks, big vertices
// and big triangles directly, so there is no raw byte decoding any more.
//
// Output matches the old extractor: triangles in NIF-local engine units (Havok units x 69.99125), per-path result with
// Ok/Reason, never throwing for one bad file. Shape coverage mirrors the Python version: bhkListShape, bhkConvexTransformShape /
// bhkTransformShape, bhkMoppBvTreeShape, bhkCompressedMeshShape (the common case), bhkBoxShape, bhkConvexVerticesShape (hull rebuilt,
// flagged approx). Capsule/sphere/anything else is deliberately skipped, as before.

using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

namespace SeamFinder.Core;

public static class NifCollisionReader
{
    const double HavokScale = 69.99125;

    public static ExtractedCollision Extract(string path)
    {
        try { return ExtractCore(path); }
        catch (Exception ex) { return new ExtractedCollision(false, $"collision read failed: {ex.GetType().Name}: {ex.Message}", new(), new(), false); }
    }

    static ExtractedCollision Fail(string reason) => new(false, reason, new(), new(), false);

    sealed class Ctx
    {
        public required NifFile Nif;
        public HashSet<string> ShapeTypes = new();
        public int Depth;
        public int BlockCount => Nif.Blocks.Count;
    }

    static ExtractedCollision ExtractCore(string path)
    {
        if (!File.Exists(path)) return Fail("file not found");
        var nif = new NifFile();
        var rc = nif.Load(path, new NifFileLoadOptions());
        if (rc != 0) return Fail($"NIF load failed (code {rc})");
        var root = nif.GetRootNode();
        if (root == null) return Fail("no root node");
        var ctx = new Ctx { Nif = nif };
        var collIdx = root.CollisionObject.Index;
        if (!Valid(ctx, collIdx)) return Fail("no collision object on root node");

        if (nif.GetBlock(collIdx) is not bhkNiCollisionObject obj) return Fail("collision object is not a bhkNiCollisionObject (unsupported)");
        var bodyIdx = obj.Body.Index;
        if (!Valid(ctx, bodyIdx)) return Fail("collision object has no body");
        if (nif.GetBlock(bodyIdx) is not bhkRigidBody body) return Fail("collision body is not a bhkRigidBody (unsupported here)");
        var shapeIdx = body.Shape.Index;
        if (!Valid(ctx, shapeIdx)) return Fail("body has no shape");

        var local = Resolve(ctx, shapeIdx);
        if (local.Count == 0)
            return Fail($"no ground-relevant triangles (shape types seen: [{string.Join(", ", ctx.ShapeTypes.OrderBy(x => x))}])");

        // bhkRigidBodyT carries its own rotation + translation (Havok units); a plain bhkRigidBody applies none (same rule as before).
        double[,] rot = Ident();
        double tx = 0, ty = 0, tz = 0;
        if (body is bhkRigidBodyT)
        {
            var (q, t) = BodyTransform(body);
            rot = QuatToMatrix(q.X, q.Y, q.Z, q.W);
            tx = t.X; ty = t.Y; tz = t.Z;
        }
        var tris = new List<Tri3>(local.Count);
        foreach (var (a, b, c) in local)
            tris.Add(new Tri3(Out(rot, a, tx, ty, tz), Out(rot, b, tx, ty, tz), Out(rot, c, tx, ty, tz)));
        return new ExtractedCollision(true, null, tris, ctx.ShapeTypes.OrderBy(x => x).ToList(), ctx.ShapeTypes.Any(t => t.Contains("approx")));
    }

    static bool Valid(Ctx c, int index) => index >= 0 && index < c.BlockCount;

    // The rigid-body info struct that holds rotation + translation depends on the file version (Skyrim SE uses the 2010 layout);
    // take the first one that carries a rotation.
    static (hkQuaternion Rotation, System.Numerics.Vector4 Translation) BodyTransform(bhkRigidBody b)
    {
        static bool Has(hkQuaternion q) => q.X != 0 || q.Y != 0 || q.Z != 0 || q.W != 0;
        if (Has(b.RigidBodyInfo_bRBCI2010.Rotation)) return (b.RigidBodyInfo_bRBCI2010.Rotation, b.RigidBodyInfo_bRBCI2010.Translation);
        if (Has(b.RigidBodyInfo_bRBCI2014.Rotation)) return (b.RigidBodyInfo_bRBCI2014.Rotation, b.RigidBodyInfo_bRBCI2014.Translation);
        return (b.RigidBodyInfo_bRBCI550660.Rotation, b.RigidBodyInfo_bRBCI550660.Translation);
    }

    static Vec3 Out(double[,] r, (double X, double Y, double Z) v, double tx, double ty, double tz)
    {
        var x = r[0, 0] * v.X + r[0, 1] * v.Y + r[0, 2] * v.Z + tx;
        var y = r[1, 0] * v.X + r[1, 1] * v.Y + r[1, 2] * v.Z + ty;
        var z = r[2, 0] * v.X + r[2, 1] * v.Y + r[2, 2] * v.Z + tz;
        return new Vec3((float)(x * HavokScale), (float)(y * HavokScale), (float)(z * HavokScale));
    }

    // ---- shape chain ----

    static List<((double X, double Y, double Z) A, (double X, double Y, double Z) B, (double X, double Y, double Z) C)> Resolve(Ctx c, int blockIndex)
    {
        var empty = new List<((double, double, double), (double, double, double), (double, double, double))>();
        if (!Valid(c, blockIndex) || c.Depth > 32) return empty;
        c.Depth++;
        try
        {
            var block = c.Nif.GetBlock(blockIndex);
            switch (block)
            {
                case bhkListShape list:
                {
                    var all = empty;
                    foreach (var sub in list.SubShapes.Indices) all.AddRange(Resolve(c, sub));
                    return all;
                }
                case bhkTransformShape tf:
                    return Transformed(Resolve(c, tf.Shape.Index), tf.Transform);
                case bhkConvexTransformShape ctf:
                    return Transformed(Resolve(c, ctf.Shape.Index), ctf.Transform);
                case bhkMoppBvTreeShape mopp:
                    return Resolve(c, mopp.Shape.Index);
                case bhkCompressedMeshShape cms:
                {
                    c.ShapeTypes.Add("bhkCompressedMeshShape");
                    return DecodeCompressedMesh(c, cms.Data.Index);
                }
                case bhkBoxShape box:
                {
                    c.ShapeTypes.Add("bhkBoxShape");
                    return BoxTriangles(box.Dimensions.X, box.Dimensions.Y, box.Dimensions.Z);
                }
                case bhkConvexVerticesShape cv:
                {
                    c.ShapeTypes.Add("bhkConvexVerticesShape (approx: convex hull reconstructed)");
                    var pts = new List<(double, double, double)>();
                    foreach (var v in cv.Vertices) pts.Add((v.X, v.Y, v.Z));
                    return HullTriangles(pts);
                }
                default:
                    c.ShapeTypes.Add((block?.GetType().Name ?? "null") + " (SKIPPED - not ground-relevant)");
                    return empty;
            }
        }
        finally { c.Depth--; }
    }

    // NiflySharp's Matrix44 names its fields M<column><row> in file order, so file row r (the old nifly Matrix4 row r) is M1r..M3r:
    // rows 0-2 hold the rotation (row r = [r][0..2]), row 3 holds the translation.
    static List<((double, double, double), (double, double, double), (double, double, double))> Transformed(
        List<((double X, double Y, double Z) A, (double X, double Y, double Z) B, (double X, double Y, double Z) C)> child, Matrix44 m)
    {
        var R = new double[3, 3]
        {
            { m.M11, m.M21, m.M31 },
            { m.M12, m.M22, m.M32 },
            { m.M13, m.M23, m.M33 },
        };
        var t = (X: (double)m.M14, Y: (double)m.M24, Z: (double)m.M34);
        var res = new List<((double, double, double), (double, double, double), (double, double, double))>(child.Count);
        foreach (var (a, b, cc) in child) res.Add((Xf(R, a, t), Xf(R, b, t), Xf(R, cc, t)));
        return res;
    }

    static (double, double, double) Xf(double[,] R, (double X, double Y, double Z) v, (double X, double Y, double Z) t) =>
        (R[0, 0] * v.X + R[0, 1] * v.Y + R[0, 2] * v.Z + t.X, R[1, 0] * v.X + R[1, 1] * v.Y + R[1, 2] * v.Z + t.Y, R[2, 0] * v.X + R[2, 1] * v.Y + R[2, 2] * v.Z + t.Z);

    static List<((double, double, double), (double, double, double), (double, double, double))> BoxTriangles(double dx, double dy, double dz)
    {
        (double, double, double)[] v =
        {
            (-dx, -dy, -dz), (dx, -dy, -dz), (dx, dy, -dz), (-dx, dy, -dz),
            (-dx, -dy, dz), (dx, -dy, dz), (dx, dy, dz), (-dx, dy, dz),
        };
        int[][] faces = { new[] { 0, 1, 2 }, new[] { 0, 2, 3 }, new[] { 4, 6, 5 }, new[] { 4, 7, 6 }, new[] { 0, 4, 5 }, new[] { 0, 5, 1 },
                          new[] { 1, 5, 6 }, new[] { 1, 6, 2 }, new[] { 2, 6, 7 }, new[] { 2, 7, 3 }, new[] { 3, 7, 4 }, new[] { 3, 4, 0 } };
        return faces.Select(f => (v[f[0]], v[f[1]], v[f[2]])).ToList();
    }

    // Brute-force hull (every point triple whose plane has all other points on one side), dependency-free; same approach as the
    // old Python version, with the same 200-point safety cap.
    static List<((double, double, double), (double, double, double), (double, double, double))> HullTriangles(List<(double X, double Y, double Z)> pts)
    {
        var res = new List<((double, double, double), (double, double, double), (double, double, double))>();
        var n = pts.Count;
        if (n < 4 || n > 200) return res;
        const double eps = 1e-6;
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                for (var k = j + 1; k < n; k++)
                {
                    var (ux, uy, uz) = (pts[j].X - pts[i].X, pts[j].Y - pts[i].Y, pts[j].Z - pts[i].Z);
                    var (vx, vy, vz) = (pts[k].X - pts[i].X, pts[k].Y - pts[i].Y, pts[k].Z - pts[i].Z);
                    var (nx, ny, nz) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
                    if (nx * nx + ny * ny + nz * nz < eps) continue;
                    bool pos = false, neg = false;
                    for (var m = 0; m < n && !(pos && neg); m++)
                    {
                        if (m == i || m == j || m == k) continue;
                        var d = nx * (pts[m].X - pts[i].X) + ny * (pts[m].Y - pts[i].Y) + nz * (pts[m].Z - pts[i].Z);
                        if (d > eps) pos = true; else if (d < -eps) neg = true;
                    }
                    if (pos && neg) continue;
                    res.Add(pos ? (pts[i], pts[k], pts[j]) : (pts[i], pts[j], pts[k]));
                }
        return res;
    }

    // ---- bhkCompressedMeshShapeData ----

    static List<((double, double, double), (double, double, double), (double, double, double))> DecodeCompressedMesh(Ctx c, int dataBlock)
    {
        var res = new List<((double, double, double), (double, double, double), (double, double, double))>();
        if (!Valid(c, dataBlock)) return res;
        if (c.Nif.GetBlock(dataBlock) is not bhkCompressedMeshShapeData data) throw new InvalidDataException("compressed mesh data block has an unexpected type");
        if (data.NumNamedMaterials != 0) throw new InvalidDataException("named materials present (unsupported)");

        var transforms = data.ChunkTransforms;
        var bigVerts = data.BigVerts;
        foreach (var bt in data.BigTris)
        {
            int a = bt.Triangle.V1, b = bt.Triangle.V2, cc = bt.Triangle.V3;
            if (a < bigVerts.Count && b < bigVerts.Count && cc < bigVerts.Count)
                res.Add(((bigVerts[a].X, bigVerts[a].Y, bigVerts[a].Z), (bigVerts[b].X, bigVerts[b].Y, bigVerts[b].Z), (bigVerts[cc].X, bigVerts[cc].Y, bigVerts[cc].Z)));
        }

        var error = data.Error;
        var step = error > 0 && error < 1 ? error : 0.001;
        foreach (var chunk in data.Chunks)
        {
            double ox = chunk.Translation.X, oy = chunk.Translation.Y, oz = chunk.Translation.Z;
            int tIndex = chunk.TransformIndex;
            var verts = chunk.Vertices; var idx = chunk.Indices; var strips = chunk.Strips;
            var nv = verts.Count;
            var pts = new (double, double, double)[nv];
            double[,]? R = null; (double, double, double) T = (0, 0, 0);
            if (tIndex != 0xFFFF && tIndex < transforms.Count)
            {
                var t = transforms[tIndex];
                R = QuatToMatrix(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W); T = (t.Translation.X, t.Translation.Y, t.Translation.Z);
            }
            for (var v = 0; v < nv; v++)
            {
                double x = ox + verts[v].X * step, y = oy + verts[v].Y * step, z = oz + verts[v].Z * step;
                if (R != null)
                {
                    var rx = R[0, 0] * x + R[0, 1] * y + R[0, 2] * z + T.Item1;
                    var ry = R[1, 0] * x + R[1, 1] * y + R[1, 2] * z + T.Item2;
                    var rz = R[2, 0] * x + R[2, 1] * y + R[2, 2] * z + T.Item3;
                    (x, y, z) = (rx, ry, rz);
                }
                pts[v] = (x, y, z);
            }
            var o = 0;
            foreach (var len in strips)
            {
                for (var k = 0; k + 2 < len; k++)
                {
                    int a = idx[o + k], b = idx[o + k + 1], cc = idx[o + k + 2];
                    if (a >= nv || b >= nv || cc >= nv || a == b || b == cc || a == cc) continue; // degenerate strip joins
                    res.Add((k % 2 == 0) ? (pts[a], pts[b], pts[cc]) : (pts[b], pts[a], pts[cc]));
                }
                o += len;
            }
            for (; o + 2 < idx.Count; o += 3)
            {
                int a = idx[o], b = idx[o + 1], cc = idx[o + 2];
                if (a < nv && b < nv && cc < nv) res.Add((pts[a], pts[b], pts[cc]));
            }
        }
        return res;
    }

    // ---- small helpers ----

    static double[,] Ident() => new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

    static double[,] QuatToMatrix(double x, double y, double z, double w)
    {
        var n = x * x + y * y + z * z + w * w;
        if (n < 1e-12) return Ident();
        var s = 2.0 / n;
        double xx = x * x * s, yy = y * y * s, zz = z * z * s, xy = x * y * s, xz = x * z * s, yz = y * z * s, wx = w * x * s, wy = w * y * s, wz = w * z * s;
        return new[,] { { 1 - (yy + zz), xy - wz, xz + wy }, { xy + wz, 1 - (xx + zz), yz - wx }, { xz - wy, yz + wx, 1 - (xx + yy) } };
    }
}
