using ACadSharp.Entities;
using CSMath;

public readonly record struct CadPlacement(XYZ Origin, XYZ XAxis, XYZ YAxis, XYZ ZAxis)
{
    public XYZ Apply(XYZ p) => Origin + XAxis * p.X + YAxis * p.Y + ZAxis * p.Z;

    // ACadSharp's entity model exposes the insertion point in WCS. Construct
    // a stable orthonormal basis, including normals very close to +/-Z, then
    // apply base point, scale, local rotation, orientation, and translation.
    public static CadPlacement FromInsert(Insert insert)
    {
        var n = Unit(insert.Normal);
        var ax = Unit(Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64
            ? Cross(new XYZ(0, 1, 0), n) : Cross(new XYZ(0, 0, 1), n));
        var ay = Cross(n, ax);
        double c = Math.Cos(insert.Rotation), s = Math.Sin(insert.Rotation);
        var x = (ax * c + ay * s) * insert.XScale;
        var y = (ax * -s + ay * c) * insert.YScale;
        var z = n * insert.ZScale;
        var b = insert.Block?.BlockEntity?.BasePoint ?? XYZ.Zero;
        var origin = insert.InsertPoint - x * b.X - y * b.Y - z * b.Z;
        if (!Finite(origin) || !Finite(x) || !Finite(y) || !Finite(z)) throw new InvalidDataException("Non-finite block transform");
        return new(origin, x, y, z);
    }

    public static bool Finite(XYZ p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static XYZ Unit(XYZ p)
    {
        double length = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        if (!double.IsFinite(length) || length <= 1e-15) throw new InvalidDataException("Invalid block normal");
        return p * (1 / length);
    }
    private static XYZ Cross(XYZ a, XYZ b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
