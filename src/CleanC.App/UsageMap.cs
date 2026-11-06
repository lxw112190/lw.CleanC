using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CleanC.Core;

namespace CleanC.App;

public sealed class UsageMap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items),
        typeof(IEnumerable<UsageEntry>), typeof(UsageMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable<UsageEntry>? Items { get => (IEnumerable<UsageEntry>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public event Action<string>? OpenDirectory;
    private List<MapTile> tiles = [];
    private static readonly Brush[] Colors = new[] { "#087F75", "#246A80", "#409995", "#447F99", "#6A8B75", "#546E91" }
        .Select(c => { var brush = (Brush)new BrushConverter().ConvertFromString(c)!; brush.Freeze(); return brush; }).ToArray();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var all = Items?.Where(x => x.Size > 0).OrderByDescending(x => x.Size).ToList() ?? [];
        var visible = all.Take(23).ToList();
        if (all.Count > 23) visible.Add(new("", all.Skip(23).Sum(x => x.Size), 0) { Label = $"其他 {all.Count - 23} 个目录" });
        tiles = TreemapLayout.Create(visible, ActualWidth, ActualHeight);
        if (tiles.Count == 0)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(229, 239, 237)), null, new Rect(RenderSize), 8, 8);
            DrawText(dc, "分析完成后，在这里查看空间分布。", new Rect(18, 15, Math.Max(1, ActualWidth - 36), 26), new SolidColorBrush(Color.FromRgb(71, 109, 105)), 13);
        }
        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i]; var rect = new Rect(tile.X + 1, tile.Y + 1, Math.Max(0, tile.Width - 2), Math.Max(0, tile.Height - 2));
            dc.DrawRoundedRectangle(Colors[i % Colors.Length], null, rect, 5, 5);
            if (rect.Width > 55 && rect.Height > 28)
            {
                DrawText(dc, tile.Entry.Name, new(rect.X + 10, rect.Y + 8, Math.Max(1, rect.Width - 20), 21), Brushes.White, 12);
                if (rect.Height > 52) DrawText(dc, tile.Entry.SizeText, new(rect.X + 10, rect.Y + 29, Math.Max(1, rect.Width - 20), 20), Brushes.White, 11);
            }
        }
    }
    private void DrawText(DrawingContext dc, string text, Rect rect, Brush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = rect.Width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, rect.TopLeft);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); Point point = e.GetPosition(this);
        var tile = tiles.FirstOrDefault(t => new Rect(t.X, t.Y, t.Width, t.Height).Contains(point));
        ToolTip = tile is null ? null : $"{tile.Entry.Name}\n{tile.Entry.SizeText}\n{tile.Entry.Path}";
        Cursor = tile is not null && tile.Entry.Path.Length > 0 ? Cursors.Hand : Cursors.Arrow;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount != 2) return;
        Point point = e.GetPosition(this);
        var tile = tiles.FirstOrDefault(t => new Rect(t.X, t.Y, t.Width, t.Height).Contains(point));
        if (tile is not null && tile.Entry.Path.Length > 0) OpenDirectory?.Invoke(tile.Entry.Path);
    }
}
