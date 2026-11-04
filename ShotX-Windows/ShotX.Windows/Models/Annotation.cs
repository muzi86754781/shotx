using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Rect = System.Windows.Rect;

namespace ShotX.Windows.Models;

public enum ShapeType
{
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Brush,
    Mosaic,
    Text,
    Step
}

public class Annotation
{
    public ShapeType Type { get; set; }
    public Rect Bounds { get; set; }
    public Point StartPoint { get; set; }
    public Point EndPoint { get; set; }
    public List<Point> Points { get; set; } = new();

    public Color PrimaryColor { get; set; } = Colors.Red;
    public Color SecondaryColor { get; set; } = Colors.White;
    public double Thickness { get; set; } = 4.0;
    public double Opacity { get; set; } = 1.0;
    public bool IsFilled { get; set; } = false;

    // Rectangle specific
    public bool IsRounded { get; set; } = false;

    // Arrow specific
    public bool IsCurved { get; set; } = false;

    // Brush specific
    public bool IsHighlighter { get; set; } = false;

    // Text specific
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 18.0;
    public int TextStyleIndex { get; set; } = 1; // 0 = 无, 1 = 描边, 2 = 背景

    // Step number specific
    public int StepNumber { get; set; } = 1;

    // Mosaic specific
    public bool MosaicIsBlur { get; set; } = false; // false = 马赛克, true = 毛玻璃

    public bool HitTest(Point p)
    {
        if (Type == ShapeType.Text)
        {
            return Bounds.Contains(p);
        }
        if (Type == ShapeType.Rectangle || Type == ShapeType.Ellipse || Type == ShapeType.Mosaic)
        {
            return Bounds.Contains(p);
        }
        if (Type == ShapeType.Step)
        {
            return (p - StartPoint).Length <= 18;
        }
        return false;
    }
}

public class AnnotationManager
{
    private readonly Stack<Annotation> _annotations = new();
    private readonly Stack<Annotation> _redoStack = new();

    public IReadOnlyList<Annotation> Items => _annotations.Reverse().ToList();
    public bool CanUndo => _annotations.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Add(Annotation annotation)
    {
        _annotations.Push(annotation);
        _redoStack.Clear();
    }

    public void Remove(Annotation annotation)
    {
        var list = _annotations.Where(a => a != annotation).Reverse().ToList();
        _annotations.Clear();
        foreach (var item in list)
        {
            _annotations.Push(item);
        }
    }

    public void Undo()
    {
        if (CanUndo)
        {
            _redoStack.Push(_annotations.Pop());
        }
    }

    public void Redo()
    {
        if (CanRedo)
        {
            _annotations.Push(_redoStack.Pop());
        }
    }

    public void Clear()
    {
        _annotations.Clear();
        _redoStack.Clear();
    }
}
