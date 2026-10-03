using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Deed.Core;

namespace Deed.Desktop;

public sealed class DrawingSurface : FrameworkElement
{
    private readonly CanvasTransform _transform = new();
    private RecordSolveResult? _solve;
    private DeedRecord? _record;
    private DraftingBlock? _block;
    private CourseId? _selected;
    private Point? _drag;

    public CourseId? SelectedCourseId => _selected;

    public void SetDrawing(DeedRecord record, DraftingBlock block, RecordSolveResult solve, CourseId? selected)
    {
        _record = record; _block = block; _solve = solve; _selected = selected;
        InvalidateVisual();
    }

    public void FitDrawing()
    {
        _transform.Fit(_solve?.NodeCoordinates.Values ?? Array.Empty<Coordinate2D>(), ActualWidth, ActualHeight);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 31, 39)), null,
            new Rect(0, 0, ActualWidth, ActualHeight));
        if (_record is null || _block is null || _solve is null) return;
        foreach (var id in _block.Courses)
        {
            if (!_solve.SolvedLines.TryGetValue(id, out var line)) continue;
            var a = _transform.ToScreen(line.Start);
            var b = _transform.ToScreen(line.ComputedRecordedEnd);
            var pen = id == _selected ? new Pen(Brushes.Gold, 3) : new Pen(Brushes.LightSkyBlue, 1.5);
            dc.DrawLine(pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
        }
        foreach (var (id, point) in _solve.NodeCoordinates)
        {
            if (!_record.Nodes.TryGetValue(id, out var node)) continue;
            var p = _transform.ToScreen(point);
            var center = new Point(p.X, p.Y);
            switch (node.Definition)
            {
                case FixedNodeDefinition:
                    dc.DrawRectangle(Brushes.Orange, new Pen(Brushes.Black, 1),
                        new Rect(p.X - 5, p.Y - 5, 10, 10));
                    break;
                case AlongCourseNodeDefinition:
                    dc.DrawEllipse(Brushes.LimeGreen, new Pen(Brushes.Black, 1), center, 4, 4);
                    break;
                default:
                    dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1), center, 4, 4);
                    break;
            }
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        _transform.ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, p.X, p.Y);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag is not { } previous) return;
        var current = e.GetPosition(this);
        _transform.Pan(current.X - previous.X, current.Y - previous.Y);
        _drag = current;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = null;
        ReleaseMouseCapture();
    }
}
