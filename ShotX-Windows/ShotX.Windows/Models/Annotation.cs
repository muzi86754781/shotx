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
    Arrow,
    Line,
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
    public double Thickness { get; set; } = 3.0;
    public double Opacity { get; set; } = 1.0;
    public bool IsFilled { get; set; } = false;

    // Text specific
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 16.0;
    public TextStyle TextStyle { get; set; } = TextStyle.Border;

    // Step number specific
    public int StepNumber { get; set; } = 1;

    // Mosaic specific
    public MosaicStyle MosaicStyle { get; set; } = MosaicStyle.Pixelate;
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

