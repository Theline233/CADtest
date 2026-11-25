using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

internal static class TreatmentFixture
{
    internal static void Write(string path, string[] notes)
    {
        if (File.Exists(path) || notes.Length != 4)
            throw new ArgumentException("Use a new synthetic DWG and exactly four note texts.");

        var doc = new CadDocument(ACadVersion.AC1032);
        XYZ P(double x, double y) => new(x, y, 0);
        TextEntity Text(string value, double x, double y) =>
            new() { Value = value, InsertPoint = P(x, y), Height = 2 };

        void Frame(int index, double origin, IEnumerable<string> processes)
        {
            var frame = new BlockRecord($"SYNTHETIC-FRAME-{index}");
            var corners = new[] { P(0, 0), P(400, 0), P(400, 180), P(0, 180) };
            for (int i = 0; i < corners.Length; i++) frame.Entities.Add(new Line(corners[i], corners[(i+1)%4]));
            frame.Entities.Add(Text("PART No.", 300, 16));
            frame.Entities.Add(Text($"TA20000101-TEST-{index:000}", 300, 12));
            frame.Entities.Add(Text("PART NAME", 230, 16));
            frame.Entities.Add(Text("SYNTHETIC PART", 230, 12));
            frame.Entities.Add(Text("MATERIAL", 181, 16));
            frame.Entities.Add(Text("SYNTHETIC STEEL", 181, 12));
            frame.Entities.Add(Text("Q'TY", 152, 16));
            frame.Entities.Add(Text("2", 152, 12));
            frame.Entities.Add(Text("EA", 162, 12));
            int j = 0;
            foreach (var note in processes) frame.Entities.Add(Text(note, 10, 35 + 8 * j++));
            doc.BlockRecords.Add(frame);
            doc.ModelSpace.Entities.Add(new Insert(frame) { InsertPoint = P(origin,0) });
        }

        // Completely synthetic geometry and title fields, with unmodified
        // process text supplied by the test. Situations 1–8 go through all stages.
        Frame(1, 0, notes);
        Frame(2, 450, ["HARD CHROMIUM PLATING"]);
        Frame(3, 900, ["ZINC WHITE COATING"]);
        Frame(4, 1350, [..notes[..^1], "4. CHROMIUM PLATING ON BORE ONLY"]);
        Frame(5, 1800, [..notes[..^1], "4. NO CHROMIUM PLATING"]);
        Frame(6, 2250, [..notes[..^2], "3. NITRIDING HEAT TREATMENT, HRC 45~55", notes[3]]);
        Frame(7, 2700, ["3. BLACK OXIDE", "4. ZINC WHITE COATING"]);
        Frame(8, 3150, [notes[0], notes[2]]);
        // Adjacent but unrelated insert lies within frame 8. Its process must
        // stay visible as evidence, not become the part's finish.
        var foreign = new BlockRecord("SYNTHETIC-UNRELATED-NOTE");
        foreign.Entities.Add(Text(notes[3], 0, 0));
        doc.BlockRecords.Add(foreign);
        doc.ModelSpace.Entities.Add(new Insert(foreign) { InsertPoint = P(3170,45) });

        DwgWriter.Write(path, doc);
    }
}
