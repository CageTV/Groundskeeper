// Reads Havok collision geometry from a NIF in-process, replacing the old extract_collision.py + PyNifly +
// Python dependency (the cause of the "Missing PyNifly & SciPy" bug reports).
//
// NIF/Havok structure comes from niflysharp (SWIG C# bindings of ousnius' nifly, GPL-3.0): header, block types and sizes,
// and the shape chain. niflysharp exposes the compressed-mesh chunk and big-triangle lists only as opaque wrappers
// (no element access), so the one block that holds the actual triangles, bhkCompressedMeshShapeData, is decoded here
// from the raw file bytes, and the parse is checked against the block size: a layout mismatch fails loudly.
//
// Output matches the old extractor: triangles in NIF-local engine units (Havok units x 69.99125), per-path result with
// Ok/Reason, never throwing for one bad file. Shape coverage mirrors the Python version: bhkListShape, bhkConvexTransformShape /
// bhkTransformShape, bhkMoppBvTreeShape, bhkCompressedMeshShape (the common case), bhkBoxShape, bhkConvexVerticesShape (hull rebuilt,
// flagged approx). Capsule/sphere/anything else is deliberately skipped, as before.

using nifly;

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
        public required NiHeader Header;
        public required byte[] Bytes;
        public required long[] BlockStart;
        public HashSet<string> ShapeTypes = new();
        public int Depth;
    }

    static ExtractedCollision ExtractCore(string path)
    {
        if (!File.Exists(path)) return Fail("file not found");
        var bytes = File.ReadAllBytes(path);
        using var nif = new NifFile();
        var rc = nif.Load(path);
        if (rc != 0) return Fail($"NIF load failed (code {rc})");
        var header = nif.GetHeader();
        var root = nif.GetRootNode();
        if (root == null) return Fail("no root node");
        var collIdx = root.collisionRef.index;
        if (collIdx == uint.MaxValue || collIdx >= header.GetNumBlocks()) return Fail("no collision object on root node");

        if (header.GetBlockById(collIdx) is not bhkNiCollisionObject obj) return Fail("collision object is not a bhkNiCollisionObject (unsupported)");
        var bodyIdx = obj.bodyRef.index;
        if (bodyIdx == uint.MaxValue || bodyIdx >= header.GetNumBlocks()) return Fail("collision object has no body");
        if (header.GetBlockById(bodyIdx) is not bhkRigidBody body) return Fail("collision body is not a bhkRigidBody (unsupported here)");
        var shapeIdx = body.shapeRef.index;
        if (shapeIdx == uint.MaxValue || shapeIdx >= header.GetNumBlocks()) return Fail("body has no shape");

        var ctx = new Ctx { Nif = nif, Header = header, Bytes = bytes, BlockStart = ComputeBlockStarts(header, bytes) };
        var local = Resolve(ctx, shapeIdx);
        if (local.Count == 0)
            return Fail($"no ground-relevant triangles (shape types seen: [{string.Join(", ", ctx.ShapeTypes.OrderBy(x => x))}])");

        // bhkRigidBodyT carries its own rotation + translation (Havok units); a plain bhkRigidBody applies none (same rule as before).
        double[,] rot = Ident();
        double tx = 0, ty = 0, tz = 0;
        if (body is bhkRigidBodyT)
        {
            rot = QuatToMatrix(body.rotation.x, body.rotation.y, body.rotation.z, body.rotation.w);
            tx = body.translation.x; ty = body.translation.y; tz = body.translation.z;
        }
        var tris = new List<Tri3>(local.Count);
        foreach (var (a, b, c) in local)
            tris.Add(new Tri3(Out(rot, a, tx, ty, tz), Out(rot, b, tx, ty, tz), Out(rot, c, tx, ty, tz)));
        return new ExtractedCollision(true, null, tris, ctx.ShapeTypes.OrderBy(x => x).ToList(), ctx.ShapeTypes.Any(t => t.Contains("approx")));
    }

    static Vec3 Out(double[,] r, (double X, double Y, double Z) v, double tx, double ty, double tz)
    {
        var x = r[0, 0] * v.X + r[0, 1] * v.Y + r[0, 2] * v.Z + tx;
        var y = r[1, 0] * v.X + r[1, 1] * v.Y + r[1, 2] * v.Z + ty;
        var z = r[2, 0] * v.X + r[2, 1] * v.Y + r[2, 2] * v.Z + tz;
        return new Vec3((float)(x * HavokScale), (float)(y * HavokScale), (float)(z * HavokScale));
    }

    // ---- shape chain ----

    static List<((double X, double Y, double Z) A, (double X, double Y, double Z) B, (double X, double Y, double Z) C)> Resolve(Ctx c, uint blockIndex)
    {
        var empty = new List<((double, double, double), (double, double, double), (double, double, double))>();
        if (blockIndex == uint.MaxValue || blockIndex >= c.Header.GetNumBlocks() || c.Depth > 32) return empty;
        c.Depth++;
        try
        {
            var block = c.Header.GetBlockById(blockIndex);
            switch (block)
            {
                case bhkListShape list:
                {
                    var all = empty;
                    var n = list.subShapeRefs.GetSize();
                    for (uint i = 0; i < n; i++) all.AddRange(Resolve(c, list.subShapeRefs.GetBlockRef(i)));
                    return all;
                }
                case bhkTransformShape tf: // bhkConvexTransformShape derives from it
                {
                    var child = Resolve(c, tf.shapeRef.index);
                    // nifly's Matrix4: rows 0-2 hold the rotation (row r = [r][0..2]), row 3 holds the translation.
                    var m = tf.xform;
                    var row = new Vector3(0, 0, 0);
                    var R = new double[3, 3];
                    for (var r = 0; r < 3; r++) { m.GetRow(r, row); R[r, 0] = row.x; R[r, 1] = row.y; R[r, 2] = row.z; }
                    m.GetRow(3, row);
                    var t = new Vector3(row.x, row.y, row.z);
                    var res = empty;
                    foreach (var (a, b, cc) in child) res.Add((Xf(R, a, t), Xf(R, b, t), Xf(R, cc, t)));
                    return res;
                }
                case bhkMoppBvTreeShape mopp:
                    return Resolve(c, mopp.shapeRef.index);
                case bhkCompressedMeshShape cms:
                {
                    c.ShapeTypes.Add("bhkCompressedMeshShape");
                    return DecodeCompressedMesh(c, cms.dataRef.index);
                }
                case bhkBoxShape box:
                {
                    c.ShapeTypes.Add("bhkBoxShape");
                    return BoxTriangles(box.dimensions.x, box.dimensions.y, box.dimensions.z);
                }
                case bhkConvexVerticesShape cv:
                {
                    c.ShapeTypes.Add("bhkConvexVerticesShape (approx: convex hull reconstructed)");
                    var pts = new List<(double, double, double)>();
                    for (var i = 0; i < cv.verts.size(); i++) { var v = cv.verts.items()[i]; pts.Add((v.x, v.y, v.z)); }
                    return HullTriangles(pts);
                }
                default:
                    c.ShapeTypes.Add((block?.GetType().Name ?? "null") + " (SKIPPED - not ground-relevant)");
                    return empty;
            }
        }
        finally { c.Depth--; }
    }

    static (double, double, double) Xf(double[,] R, (double X, double Y, double Z) v, Vector3 t) =>
        (R[0, 0] * v.X + R[0, 1] * v.Y + R[0, 2] * v.Z + t.x, R[1, 0] * v.X + R[1, 1] * v.Y + R[1, 2] * v.Z + t.y, R[2, 0] * v.X + R[2, 1] * v.Y + R[2, 2] * v.Z + t.z);

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

    // ---- bhkCompressedMeshShapeData, decoded from the raw block bytes ----

    static long[] ComputeBlockStarts(NiHeader header, byte[] bytes)
    {
        var fileLength = bytes.Length;
        var n = (int)header.GetNumBlocks();
        long total = 0;
        for (uint i = 0; i < n; i++) total += header.GetBlockSize(i);
        // blocks are followed by the footer: uint root count + that many int root indices
        long footer = -1;
        for (var roots = 1; roots <= 8 && footer < 0; roots++)
        {
            var f = 4 + 4L * roots;
            var at = fileLength - f;
            if (at < 0 || BitConverter.ToUInt32(bytes, (int)at) != roots) continue;
            var ok = true;
            for (var r = 0; r < roots; r++) ok &= BitConverter.ToUInt32(bytes, (int)(at + 4 + 4L * r)) < n;
            if (ok) footer = f;
        }
        if (footer < 0) throw new InvalidDataException("could not locate the NIF footer");
        var starts = new long[n];
        starts[0] = fileLength - footer - total;
        if (starts[0] < 0) throw new InvalidDataException("block sizes exceed the file length");
        for (var i = 1; i < n; i++) starts[i] = starts[i - 1] + header.GetBlockSize((uint)(i - 1));
        return starts;
    }

    static List<((double, double, double), (double, double, double), (double, double, double))> DecodeCompressedMesh(Ctx c, uint dataBlock)
    {
        var res = new List<((double, double, double), (double, double, double), (double, double, double))>();
        if (dataBlock == uint.MaxValue || dataBlock >= c.Header.GetNumBlocks()) return res;
        if (c.Header.GetBlockTypeStringById(dataBlock) != "bhkCompressedMeshShapeData") throw new InvalidDataException("compressed mesh data block has an unexpected type");
        var start = c.BlockStart[dataBlock];
        var size = c.Header.GetBlockSize(dataBlock);
        var p = new Reader(c.Bytes, start, start + size);

        p.U32(); p.U32(); p.U32(); p.U32();            // bits per index / w index, masks
        var error = p.F32();
        p.Skip(32);                                     // bounds min / max (Vector4 x2)
        p.Skip(2);                                      // welding type, material type
        p.Skip(4L * p.U32()); p.Skip(4L * p.U32()); p.Skip(4L * p.U32()); // materials32/16/8
        p.Skip(8L * p.U32());                           // materials (havok material + filter)
        var named = p.U32(); if (named != 0) throw new InvalidDataException("named materials present (unsupported)");
        var nTransforms = p.U32();
        var transforms = new (double tx, double ty, double tz, double qx, double qy, double qz, double qw)[nTransforms];
        for (var i = 0; i < nTransforms; i++)
        {
            var tx = p.F32(); var ty = p.F32(); var tz = p.F32(); p.F32();
            var qx = p.F32(); var qy = p.F32(); var qz = p.F32(); var qw = p.F32();
            transforms[i] = (tx, ty, tz, qx, qy, qz, qw);
        }
        var nBigVerts = p.U32();
        var bigVerts = new (double, double, double)[nBigVerts];
        for (var i = 0; i < nBigVerts; i++) { bigVerts[i] = (p.F32(), p.F32(), p.F32()); p.F32(); }
        var nBigTris = p.U32();
        for (var i = 0; i < nBigTris; i++)
        {
            int a = p.U16(), b = p.U16(), cc = p.U16(); p.U32(); p.U16();
            if (a < nBigVerts && b < nBigVerts && cc < nBigVerts) res.Add((bigVerts[a], bigVerts[b], bigVerts[cc]));
        }
        var step = error > 0 && error < 1 ? error : 0.001;
        var nChunks = p.U32();
        for (var ci = 0; ci < nChunks; ci++)
        {
            var ox = p.F32(); var oy = p.F32(); var oz = p.F32(); p.F32();
            p.U32(); p.U16(); int tIndex = p.U16();
            var verts = p.U16s(p.U32()); var idx = p.U16s(p.U32()); var strips = p.U16s(p.U32()); p.Skip(2L * p.U32());
            var nv = verts.Length / 3;
            var pts = new (double, double, double)[nv];
            double[,]? R = null; (double, double, double) T = (0, 0, 0);
            if (tIndex != 0xFFFF && tIndex < nTransforms)
            {
                var t = transforms[tIndex];
                R = QuatToMatrix(t.qx, t.qy, t.qz, t.qw); T = (t.tx, t.ty, t.tz);
            }
            for (var v = 0; v < nv; v++)
            {
                double x = ox + verts[3 * v] * step, y = oy + verts[3 * v + 1] * step, z = oz + verts[3 * v + 2] * step;
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
            for (; o + 2 < idx.Length; o += 3)
            {
                int a = idx[o], b = idx[o + 1], cc = idx[o + 2];
                if (a < nv && b < nv && cc < nv) res.Add((pts[a], pts[b], pts[cc]));
            }
        }
        p.U32(); // numConvexPieceA
        if (p.Position != p.End) throw new InvalidDataException($"compressed mesh block layout mismatch ({p.End - p.Position} bytes unread)");
        return res;
    }

    // ---- small helpers ----

    sealed class Reader
    {
        readonly byte[] _b;
        public long Position;
        public readonly long End;
        public Reader(byte[] b, long start, long end) { _b = b; Position = start; End = end; }
        void Need(long n) { if (Position + n > End) throw new InvalidDataException("compressed mesh block ended early"); }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(_b, (int)Position); Position += 4; return v; }
        public float F32() { Need(4); var v = BitConverter.ToSingle(_b, (int)Position); Position += 4; return v; }
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(_b, (int)Position); Position += 2; return v; }
        public void Skip(long n) { Need(n); Position += n; }
        public ushort[] U16s(uint n) { Need(2L * n); var a = new ushort[n]; for (var i = 0; i < n; i++) { a[i] = BitConverter.ToUInt16(_b, (int)Position); Position += 2; } return a; }
    }

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
