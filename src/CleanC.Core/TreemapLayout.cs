namespace CleanC.Core;

public sealed record MapTile(UsageEntry Entry, double X, double Y, double Width, double Height);
public static class TreemapLayout
{
    public static List<MapTile> Create(IEnumerable<UsageEntry> entries, double width, double height)
    {
        var items = entries.Where(e => e.Size > 0).OrderByDescending(e => e.Size).ToList();
        var result = new List<MapTile>();
        if (items.Count == 0 || width <= 0 || height <= 0) return result;
        void Split(int start, int end, double x, double y, double w, double h)
        {
            if (end - start == 1) { result.Add(new(items[start], x, y, w, h)); return; }
            double total = 0; for (int i = start; i < end; i++) total += items[i].Size;
            double first = 0; int middle = start;
            while (middle < end - 1 && first < total / 2) first += items[middle++].Size;
            if (w >= h)
            {
                double cut = w * first / total;
                Split(start, middle, x, y, cut, h); Split(middle, end, x + cut, y, w - cut, h);
            }
            else
            {
                double cut = h * first / total;
                Split(start, middle, x, y, w, cut); Split(middle, end, x, y + cut, w, h - cut);
            }
        }
        Split(0, items.Count, 0, 0, width, height); return result;
    }
}
