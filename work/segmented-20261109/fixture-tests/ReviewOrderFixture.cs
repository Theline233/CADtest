using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

internal static class ReviewOrderFixture
{
    internal static void WriteDwg(string output)
    {
        if (File.Exists(output)) throw new IOException("Use a new fixture path.");
        var drawing = new CadDocument(ACadVersion.AC1032);
        // Deliberately scrambled insertion order; two rows / three columns
        // with varying frame spans and offsets. Two other copies of A exercise
        // quantity aggregation and the ignored crossed duplicate.
        Frame("D", 4, 25, 106, 94, 4, false);
        Frame("B", 142, 154, 98, 102, 2);
        Frame("F", 287, 23, 96, 99, 6, unit:"SET");
        Frame("A", 0, 150, 100, 100, 1);
        Frame("E", 138, 17, 104, 103, 5, false);
        Frame("C", 281, 147, 108, 100, 0);
        Frame("X", 430, 20, 90, 90, 8, crossed:true);
        Frame("A", 590, 15, 90, 90, 12, crossed:true);
        Frame("A", 460, 160, 96, 98, 3);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        DwgWriter.Write(output, drawing);

        void Frame(string label, double x, double y, double width, double height, int qty,
            bool treatment = true, bool crossed = false, string unit = "EA")
        {
            var block = new BlockRecord($"SYNTHETIC_{label}_{x}");
            drawing.BlockRecords.Add(block);
            drawing.Entities.Add(new Insert(block) { InsertPoint = new XYZ(x, y, 0) });
            void Edge(double x1, double y1, double x2, double y2) =>
                block.Entities.Add(new Line(new XYZ(x1, y1, 0), new XYZ(x2, y2, 0)));
            void Text(string value, double tx, double ty) =>
                block.Entities.Add(new TextEntity { Value = value, InsertPoint = new XYZ(tx, ty, 0), Height = 1 });
            Edge(0, 0, width, 0); Edge(width, 0, width, height); Edge(width, height, 0, height); Edge(0, height, 0, 0);
            if (crossed) { Edge(0, 0, width, height); Edge(0, height, width, 0); }
            Text("PART No.", 10, 14); Text($"TA20990101-CFB-{label}", 10, 12);
            Text("PART NAME", 50, 14); Text($"SYNTHETIC PART {label}", 50, 12);
            Text("MATERIAL", 10, 7); Text("SS41", 10, 5);
            Text("Q'TY", 50, 7); Text(qty.ToString(), 50, 5); Text(unit, 55, 5);
            if (treatment) Text("ZINC WHITE COATING", 10, 40);
        }
    }
}
