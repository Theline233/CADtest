using System.Text.Json.Serialization;

namespace CadReviewUI;

public sealed record FrameLocation(
    [property: JsonPropertyName("layout")] string Layout,
    [property: JsonPropertyName("bounds")] double[] Bounds)
{
    internal bool IsUsable => Layout != null && Bounds is { Length: 4 } &&
        Bounds.All(double.IsFinite) && Bounds[0] < Bounds[2] && Bounds[1] < Bounds[3] &&
        double.IsFinite(Bounds[2] - Bounds[0]) && double.IsFinite(Bounds[3] - Bounds[1]);
}

public sealed record ReviewOrder(string Mode, string[] Ids)
{
    public const string Original = "original", Horizontal = "horizontal", Vertical = "vertical", Manual = "manual";

    // Separate layouts (their coordinates are unrelated). Unlocated rows retain
    // their original slots. For each layout, cluster on the reading axis relative
    // to frame span, not drawing units. Use the first frame in each band as a
    // fixed anchor so small offsets don't form chains between separate bands.
    internal static string[] ByGeometry(DsItem[] source, bool vertical)
    {
        var result = source.Select(r => r.Id).ToArray();
        var located = source.Select((item, slot) => (Location: item.FrameLocation, slot, item.Id))
            .Where(x => x.Location?.IsUsable == true).GroupBy(x => x.Location!.Layout, StringComparer.Ordinal);
        foreach (var layout in located)
        {
            int axis = vertical ? 0 : 1, other = 1 - axis;
            double Center(double[] b, int i) => b[i] + (b[i + 2] - b[i]) / 2;
            double Span(double[] b) => b[axis + 2] - b[axis];
            double Primary(double[] b) => vertical ? Center(b, axis) : -Center(b, axis);
            double Secondary(double[] b) => vertical ? -Center(b, other) : Center(b, other);
            var bands = new List<(double[] Anchor, List<(string Id, double Axis, int Slot)> Rows)>();
            foreach (var row in layout.OrderBy(x => Primary(x.Location!.Bounds)).ThenBy(x => x.slot))
            {
                var box = row.Location!.Bounds;
                // An anchor must be close on the reading axis even for mixed
                // frame sizes. Bands are traversed left-to-right / top-to-bottom.
                if (bands.Count == 0 || Primary(box) - Primary(bands[^1].Anchor) >
                    0.45 * Math.Min(Span(box), Span(bands[^1].Anchor)))
                    bands.Add((box, []));
                bands[^1].Rows.Add((row.Id, Secondary(box), row.slot));
            }
            var sorted = bands.SelectMany(b => b.Rows.OrderBy(r => r.Axis).ThenBy(r => r.Slot)).Select(r => r.Id);
            foreach (var (slot, id) in layout.Select(x => x.slot).Zip(sorted))
                result[slot] = id;
        }
        return result;
    }
}
