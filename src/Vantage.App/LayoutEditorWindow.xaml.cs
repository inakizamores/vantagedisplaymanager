using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Vantage.Core.Models;
using Vantage.Core.Services;

namespace Vantage.App;

/// <summary>
/// Drag-to-arrange layout editor (Windows Settings style). Works in desktop pixel space,
/// scaled to fit the canvas; on apply, positions are normalized so the primary display sits
/// at (0,0) and the arrangement is applied through the verified engine.
/// </summary>
public partial class LayoutEditorWindow : Window
{
    private sealed class DisplayNode
    {
        public required DisplayState State { get; init; }
        public required Border Visual { get; init; }
        public double X;   // desktop coordinates (pixels)
        public double Y;
    }

    private const double SnapThresholdDesktop = 250;

    private readonly ApplyEngine _engine;
    private readonly SystemSnapshot _snapshot;
    private readonly List<DisplayNode> _nodes = [];
    private double _scale = 0.1;
    private double _offsetX, _offsetY;

    private DisplayNode? _dragging;
    private Point _dragStartMouse;
    private (double X, double Y) _dragStartPos;

    public bool Applied { get; private set; }

    public LayoutEditorWindow(SystemSnapshot snapshot, ApplyEngine engine)
    {
        InitializeComponent();
        _snapshot = snapshot;
        _engine = engine;
        Loaded += (_, _) => BuildNodes();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeChrome.Apply(this);
    }

    private void BuildNodes()
    {
        EditorCanvas.Children.Clear();
        _nodes.Clear();

        var minX = _snapshot.Displays.Min(d => (double)d.PositionX);
        var minY = _snapshot.Displays.Min(d => (double)d.PositionY);
        var maxX = _snapshot.Displays.Max(d => (double)d.PositionX + d.Width);
        var maxY = _snapshot.Displays.Max(d => (double)d.PositionY + d.Height);

        // Fit with generous margin so there's room to drag outward.
        var canvasW = EditorCanvas.ActualWidth;
        var canvasH = EditorCanvas.ActualHeight;
        _scale = Math.Min(canvasW / ((maxX - minX) * 2.2), canvasH / ((maxY - minY) * 2.2));
        _offsetX = (canvasW - (maxX - minX) * _scale) / 2 - minX * _scale;
        _offsetY = (canvasH - (maxY - minY) * _scale) / 2 - minY * _scale;

        var accent = Application.Current?.Resources["SystemAccentColorPrimary"] is Color c
            ? c
            : Color.FromRgb(0xA9, 0x4D, 0xC1);

        foreach (var d in _snapshot.Displays)
        {
            var fill = new SolidColorBrush(d.IsPrimary ? accent : Color.FromRgb(0x55, 0x55, 0x55));
            var visual = new Border
            {
                Width = d.Width * _scale,
                Height = d.Height * _scale,
                Background = fill,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Colors.White) { Opacity = 0.35 },
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeAll,
                Child = new TextBlock
                {
                    Text = $"{d.Identity.FriendlyName}\n{d.Width} × {d.Height}",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var node = new DisplayNode { State = d, Visual = visual, X = d.PositionX, Y = d.PositionY };
            visual.MouseLeftButtonDown += (_, e) => StartDrag(node, e);
            visual.MouseMove += (_, e) => Drag(node, e);
            visual.MouseLeftButtonUp += (_, e) => EndDrag(node, e);

            _nodes.Add(node);
            EditorCanvas.Children.Add(visual);
            PositionVisual(node);
        }
    }

    private void PositionVisual(DisplayNode node)
    {
        Canvas.SetLeft(node.Visual, node.X * _scale + _offsetX);
        Canvas.SetTop(node.Visual, node.Y * _scale + _offsetY);
    }

    private void StartDrag(DisplayNode node, MouseButtonEventArgs e)
    {
        _dragging = node;
        _dragStartMouse = e.GetPosition(EditorCanvas);
        _dragStartPos = (node.X, node.Y);
        node.Visual.CaptureMouse();
        Panel.SetZIndex(node.Visual, 10);
    }

    private void Drag(DisplayNode node, MouseEventArgs e)
    {
        if (_dragging != node || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(EditorCanvas);
        node.X = _dragStartPos.X + (pos.X - _dragStartMouse.X) / _scale;
        node.Y = _dragStartPos.Y + (pos.Y - _dragStartMouse.Y) / _scale;
        PositionVisual(node);
    }

    private void EndDrag(DisplayNode node, MouseButtonEventArgs e)
    {
        if (_dragging != node)
            return;
        node.Visual.ReleaseMouseCapture();
        Panel.SetZIndex(node.Visual, 0);
        _dragging = null;

        SnapToNeighbors(node);
        PositionVisual(node);
        StatusText.Text = "";
    }

    /// <summary>
    /// Snaps the dropped display against the nearest neighbor edge so the desktop stays
    /// contiguous (Windows rejects arrangements with gaps).
    /// </summary>
    private void SnapToNeighbors(DisplayNode node)
    {
        var others = _nodes.Where(n => n != node).ToList();
        if (others.Count == 0)
            return;

        var best = double.MaxValue;
        (double X, double Y) bestPos = (node.X, node.Y);

        foreach (var o in others)
        {
            double ow = o.State.Width, oh = o.State.Height;
            double nw = node.State.Width, nh = node.State.Height;

            // Candidate placements: flush against each side of the neighbor,
            // with the perpendicular axis either aligned to an edge or kept as dropped.
            var candidates = new List<(double X, double Y)>
            {
                (o.X + ow, node.Y), (o.X - nw, node.Y),           // right / left, keep Y
                (node.X, o.Y + oh), (node.X, o.Y - nh),           // below / above, keep X
                (o.X + ow, o.Y), (o.X - nw, o.Y),                 // side + top aligned
                (o.X + ow, o.Y + oh - nh), (o.X - nw, o.Y + oh - nh), // side + bottom aligned
                (o.X, o.Y + oh), (o.X, o.Y - nh),                 // stacked + left aligned
            };

            foreach (var cand in candidates)
            {
                // Require actual edge adjacency with overlap after the snap.
                var touchesH = Math.Abs(cand.X + nw - o.X) < 0.5 || Math.Abs(o.X + ow - cand.X) < 0.5;
                var touchesV = Math.Abs(cand.Y + nh - o.Y) < 0.5 || Math.Abs(o.Y + oh - cand.Y) < 0.5;
                var overlapV = cand.Y < o.Y + oh && cand.Y + nh > o.Y;
                var overlapH = cand.X < o.X + ow && cand.X + nw > o.X;
                if (!((touchesH && overlapV) || (touchesV && overlapH)))
                    continue;

                var dist = Math.Abs(cand.X - node.X) + Math.Abs(cand.Y - node.Y);
                if (dist < best && dist < SnapThresholdDesktop * 2)
                {
                    best = dist;
                    bestPos = cand;
                }
            }
        }

        (node.X, node.Y) = bestPos;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        StatusText.Text = "Applying arrangement…";

        try
        {
            // Normalize: primary display anchors the desktop at (0,0).
            var primary = _nodes.FirstOrDefault(n => n.State.IsPrimary) ?? _nodes[0];
            var dx = primary.X;
            var dy = primary.Y;

            var profile = new VantageProfile
            {
                Id = Guid.NewGuid(),
                Name = "(arrangement)",
                Displays = _nodes.Select(n => new ProfileDisplay
                {
                    Identity = n.State.Identity,
                    Primary = n.State.IsPrimary,
                    PositionX = (int)Math.Round(n.X - dx),
                    PositionY = (int)Math.Round(n.Y - dy),
                    Width = n.State.Width,
                    Height = n.State.Height,
                    RefreshMillihertz = n.State.RefreshMillihertz,
                    Rotation = n.State.Rotation,
                }).ToList(),
                Replay = _snapshot.Replay,
            };

            var report = await Task.Run(() => _engine.ApplyAsync(profile));
            if (report.Succeeded)
            {
                Applied = true;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = report.AutoReverted
                    ? "Windows rejected that arrangement — previous layout restored."
                    : $"Could not apply: {report.FailureReason}";
                ApplyButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not apply: {ex.Message}";
            ApplyButton.IsEnabled = true;
        }
    }
}
