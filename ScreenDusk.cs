using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new BrightnessForm());
    }
}

internal sealed class BrightnessForm : Form
{
    // 20~100: hardware brightness 0~100, 0~19: hardware minimum + overlay dimming.
    private const int HardwareBoundary = 20;
    private const double MaximumOverlayOpacity = 0.80;

    private static readonly Color Background = Color.FromArgb(20, 24, 31);
    private static readonly Color Surface = Color.FromArgb(30, 36, 46);
    private static readonly Color Line = Color.FromArgb(43, 52, 64);
    private static readonly Color TextColor = Color.FromArgb(221, 228, 236);
    private static readonly Color MutedText = Color.FromArgb(124, 139, 156);
    private static readonly Color Accent = Color.FromArgb(78, 205, 196);

    private readonly DarkComboBox monitorBox = new DarkComboBox();
    private readonly IconButton refreshButton = new IconButton();
    private readonly ToolTip tips = new ToolTip();
    private readonly SmoothSlider slider = new SmoothSlider();
    private readonly Label valueLabel = new Label();
    private readonly Label statusLabel = new Label();
    private readonly List<DdcMonitor> monitors = new List<DdcMonitor>();
    private readonly OverlayManager overlayManager = new OverlayManager();
    private readonly BrightnessWriter writer = new BrightnessWriter();
    private bool updating;

    public BrightnessForm()
    {
        Text = "ScreenDusk";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(414, 172);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        BackColor = Background;
        ForeColor = TextColor;

        monitorBox.Location = new Point(18, 18);
        monitorBox.Size = new Size(338, 30);
        monitorBox.BackColor = Surface;
        monitorBox.ForeColor = TextColor;
        monitorBox.BorderColor = Line;
        monitorBox.ArrowColor = MutedText;
        monitorBox.HoverColor = Color.FromArgb(38, 46, 58);
        monitorBox.ItemHeight = 22;
        monitorBox.DrawItem += DrawMonitorItem;
        monitorBox.SelectedIndexChanged += MonitorChanged;
        Controls.Add(monitorBox);

        refreshButton.Location = new Point(366, 18);
        refreshButton.Size = new Size(30, 30);
        refreshButton.BackColor = Background;
        refreshButton.FillColor = Surface;
        refreshButton.BorderColor = Line;
        refreshButton.IconColor = TextColor;
        refreshButton.HoverColor = Line;
        refreshButton.PressColor = Color.FromArgb(54, 66, 80);
        refreshButton.Click += delegate { FindMonitors(); };
        Controls.Add(refreshButton);

        tips.SetToolTip(refreshButton, "Rescan monitors");

        slider.Location = new Point(16, 68);
        slider.Size = new Size(326, 40);
        slider.TrackCentre = 13;
        slider.BackColor = Background;
        slider.TrackColor = Line;
        slider.AccentColor = Accent;
        slider.ThumbColor = Accent;
        slider.Notch = HardwareBoundary / 100.0;
        slider.NotchColor = Color.FromArgb(143, 160, 178);
        slider.Enabled = false;
        slider.ValueChanged += SliderChanged;
        Controls.Add(slider);

        valueLabel.Location = new Point(348, 67);
        valueLabel.Size = new Size(50, 28);
        valueLabel.TextAlign = ContentAlignment.MiddleRight;
        valueLabel.ForeColor = TextColor;
        valueLabel.Text = "--%";
        Controls.Add(valueLabel);

        statusLabel.Location = new Point(18, 116);
        statusLabel.Size = new Size(378, 44);
        statusLabel.ForeColor = MutedText;
        statusLabel.Text = "Looking for monitors...";
        Controls.Add(statusLabel);

        writer.Written += WriteCompleted;

        Shown += delegate { FindMonitors(); };
        FormClosed += delegate
        {
            writer.Dispose();
            overlayManager.Dispose();
            RemoveMonitors();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    // A DropDownList combo ignores BackColor for its items unless they are drawn here.
    private void DrawMonitorItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;

        bool highlighted = (e.State & DrawItemState.Selected) != 0;
        using (Brush background = new SolidBrush(highlighted ? Line : Surface))
            e.Graphics.FillRectangle(background, e.Bounds);

        using (Brush text = new SolidBrush(TextColor))
            e.Graphics.DrawString(monitorBox.Items[e.Index].ToString(), monitorBox.Font,
                text, e.Bounds.Left + 3, e.Bounds.Top + 3);
    }

    private void FindMonitors()
    {
        ClearOverlays();
        writer.WithWritesPaused(RemoveMonitors);
        slider.Enabled = false;
        valueLabel.Text = "--%";
        statusLabel.Text = "Looking for monitors...";
        Refresh();

        try
        {
            monitors.AddRange(DdcMonitorFinder.Find());
            foreach (DdcMonitor monitor in monitors)
            {
                monitor.Level = HardwareBoundary +
                    (int)Math.Round(monitor.BrightnessPercent * (100 - HardwareBoundary) / 100.0);
                monitorBox.Items.Add(monitor);
            }

            if (monitorBox.Items.Count > 0)
            {
                monitorBox.SelectedIndex = 0;
                statusLabel.Text = "Drag the slider to change brightness.";
            }
            else
            {
                statusLabel.Text = "No monitor with DDC/CI brightness control was found.";
            }
        }
        catch (Exception ex)
        {
            statusLabel.Text = "Couldn't scan monitors: " + ex.Message;
        }
    }

    private void MonitorChanged(object sender, EventArgs e)
    {
        DdcMonitor monitor = monitorBox.SelectedItem as DdcMonitor;
        if (monitor == null)
            return;

        updating = true;
        slider.Value = monitor.Level;
        valueLabel.Text = slider.Value + "%";
        slider.Enabled = true;
        updating = false;
    }

    private void SliderChanged(object sender, EventArgs e)
    {
        valueLabel.Text = slider.Value + "%";
        if (updating)
            return;

        DdcMonitor monitor = monitorBox.SelectedItem as DdcMonitor;
        if (monitor == null)
            return;

        if (slider.Value >= HardwareBoundary)
        {
            SetOverlay(monitor, 0);
            int hardwarePercent = (int)Math.Round(
                (slider.Value - HardwareBoundary) * 100.0 / (100 - HardwareBoundary));
            writer.Request(monitor, hardwarePercent);
        }
        else
        {
            writer.Request(monitor, 0);
            double opacity = (HardwareBoundary - slider.Value) /
                (double)HardwareBoundary * MaximumOverlayOpacity;
            if (!SetOverlay(monitor, opacity))
            {
                statusLabel.Text = "Can't dim further: this monitor's screen wasn't found.";
                return;
            }
        }

        monitor.Level = slider.Value;
        statusLabel.Text = monitor.Name + " set to " + slider.Value + "%.";
    }

    // The writer runs off the UI thread, so the outcome arrives after the fact.
    private void WriteCompleted(DdcMonitor monitor, Exception error)
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        try
        {
            BeginInvoke((Action)delegate
            {
                if (monitorBox.SelectedItem != monitor)
                    return;

                if (error != null)
                    statusLabel.Text = "Couldn't change brightness. Check DDC/CI and the cable.";
                else if (!monitor.BrightnessAccepted)
                    statusLabel.Text = monitor.Name + " ignores brightness changes. "
                        + "Turn off eco or eye-saver mode on the monitor.";
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ClearOverlays()
    {
        overlayManager.Clear();
        TopMost = false;
    }

    private bool SetOverlay(DdcMonitor monitor, double opacity)
    {
        bool applied = overlayManager.Apply(monitor.DeviceName, opacity);
        TopMost = overlayManager.HasOverlay;
        if (TopMost)
            BringToFront();
        return applied;
    }

    private void RemoveMonitors()
    {
        monitorBox.Items.Clear();
        foreach (DdcMonitor monitor in monitors)
            monitor.Dispose();
        monitors.Clear();
    }
}

internal sealed class OverlayManager : IDisposable
{
    private readonly Dictionary<string, DimOverlay> overlays =
        new Dictionary<string, DimOverlay>(StringComparer.OrdinalIgnoreCase);

    public bool HasOverlay
    {
        get
        {
            foreach (DimOverlay overlay in overlays.Values)
                if (overlay.Visible)
                    return true;
            return false;
        }
    }

    // Dims only the given display. Returns false when that display cannot be located.
    public bool Apply(string deviceName, double opacity)
    {
        Rectangle bounds;
        if (!Displays.TryGetBounds(deviceName, out bounds))
            return false;

        DimOverlay overlay;
        if (!overlays.TryGetValue(deviceName, out overlay))
        {
            if (opacity <= 0)
                return true;

            overlay = new DimOverlay(bounds);
            overlays.Add(deviceName, overlay);
        }

        if (opacity <= 0)
        {
            overlay.Hide();
            return true;
        }

        // The arrangement may have changed since the overlay was created.
        overlay.Bounds = bounds;
        overlay.Opacity = opacity;
        overlay.Show();
        return true;
    }

    public void Clear()
    {
        foreach (DimOverlay overlay in overlays.Values)
            overlay.Hide();
    }

    public void Dispose()
    {
        foreach (DimOverlay overlay in overlays.Values)
        {
            overlay.Close();
            overlay.Dispose();
        }
        overlays.Clear();
    }

}

// The refresh action is a glyph rather than a word, so the control draws its own
// circular arrow instead of relying on a font that may not carry the symbol.
internal sealed class IconButton : Control
{
    private bool hovering;
    private bool pressed;

    public IconButton()
    {
        SetStyle(ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable, true);
        TabStop = true;

        FillColor = Color.FromArgb(30, 36, 46);
        BorderColor = Color.FromArgb(43, 52, 64);
        IconColor = Color.FromArgb(221, 228, 236);
        HoverColor = Color.FromArgb(43, 52, 64);
        PressColor = Color.FromArgb(54, 66, 80);
    }

    public Color FillColor { get; set; }
    public Color BorderColor { get; set; }
    public Color IconColor { get; set; }
    public Color HoverColor { get; set; }
    public Color PressColor { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hovering = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovering = false;
        pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        pressed = true;
        Focus();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = new GraphicsPath())
        {
            const int diameter = 10;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            Color fill = pressed ? PressColor : (hovering ? HoverColor : FillColor);
            using (Brush brush = new SolidBrush(fill))
                graphics.FillPath(brush, path);
            using (Pen pen = new Pen(BorderColor))
                graphics.DrawPath(pen, path);
        }

        float centreX = Width / 2f;
        float centreY = Height / 2f;
        const float radius = 6f;
        const float sweepStart = 40f;
        const float sweepLength = 280f;

        using (Pen pen = new Pen(IconColor, 1.8f))
        {
            // A round tail reads as a stroke that was drawn, not a shape that was cut.
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Flat;
            graphics.DrawArc(pen, centreX - radius, centreY - radius,
                radius * 2, radius * 2, sweepStart, sweepLength);
        }

        // The head continues along the tangent where the arc stops, so the glyph
        // reads as one continuous motion rather than an arc with a triangle parked
        // beside it.
        double angle = (sweepStart + sweepLength) * Math.PI / 180.0;
        PointF onArc = new PointF(
            centreX + (float)(radius * Math.Cos(angle)),
            centreY + (float)(radius * Math.Sin(angle)));
        PointF along = new PointF((float)-Math.Sin(angle), (float)Math.Cos(angle));
        PointF across = new PointF(-along.Y, along.X);

        PointF tip = new PointF(onArc.X + along.X * 4.3f, onArc.Y + along.Y * 4.3f);
        PointF left = new PointF(
            onArc.X + across.X * 3.2f - along.X * 0.6f,
            onArc.Y + across.Y * 3.2f - along.Y * 0.6f);
        PointF right = new PointF(
            onArc.X - across.X * 3.2f - along.X * 0.6f,
            onArc.Y - across.Y * 3.2f - along.Y * 0.6f);

        using (Brush brush = new SolidBrush(IconColor))
            graphics.FillPolygon(brush, new PointF[] { tip, left, right });
    }
}

// Letting the system paint the control and covering it afterwards made the drop
// button flash on hover, so the whole control is drawn into a buffer and blitted
// once instead.
internal sealed class DarkComboBox : ComboBox
{
    private const int WmPaint = 0x000F;
    private const int WmEraseBackground = 0x0014;
    private const int ButtonWidth = 24;

    private bool hovering;

    public DarkComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;

        BorderColor = Color.FromArgb(43, 52, 64);
        ArrowColor = Color.FromArgb(124, 139, 156);
        HoverColor = Color.FromArgb(38, 46, 58);
    }

    public Color BorderColor { get; set; }
    public Color ArrowColor { get; set; }
    public Color HoverColor { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hovering = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovering = false;
        Invalidate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmEraseBackground)
        {
            // The buffer covers every pixel, so erasing first only causes flicker.
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg != WmPaint)
        {
            base.WndProc(ref m);
            return;
        }

        NativeMethods.PaintStruct paint;
        IntPtr hdc = NativeMethods.BeginPaint(Handle, out paint);
        try
        {
            using (Graphics target = Graphics.FromHdc(hdc))
            using (Bitmap buffer = new Bitmap(Math.Max(1, Width), Math.Max(1, Height)))
            {
                using (Graphics graphics = Graphics.FromImage(buffer))
                    Render(graphics);
                target.DrawImageUnscaled(buffer, 0, 0);
            }
        }
        finally
        {
            NativeMethods.EndPaint(Handle, ref paint);
        }
    }

    private void Render(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Parent == null ? BackColor : Parent.BackColor);

        Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = new GraphicsPath())
        {
            const int diameter = 10;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            using (Brush brush = new SolidBrush(hovering ? HoverColor : BackColor))
                graphics.FillPath(brush, path);
            using (Pen pen = new Pen(BorderColor))
                graphics.DrawPath(pen, path);
        }

        string text = SelectedItem == null ? String.Empty : SelectedItem.ToString();
        using (Brush brush = new SolidBrush(ForeColor))
        using (StringFormat format = new StringFormat())
        {
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags = StringFormatFlags.NoWrap;
            format.Trimming = StringTrimming.EllipsisCharacter;
            graphics.DrawString(text, Font, brush,
                new RectangleF(10, 0, Width - ButtonWidth - 14, Height), format);
        }

        float arrowX = Width - ButtonWidth / 2f - 5;
        float arrowY = Height / 2f - 1;
        using (Pen pen = new Pen(ArrowColor, 1.6f))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            graphics.DrawLine(pen, arrowX - 4.5f, arrowY - 2, arrowX, arrowY + 2.5f);
            graphics.DrawLine(pen, arrowX + 4.5f, arrowY - 2, arrowX, arrowY + 2.5f);
        }
    }
}

// A TrackBar redraws its thumb only at whole values. Over a 326px track that is a
// 3px jump per step, which reads as stuttering while dragging, so the position is
// tracked as a fraction of the track and the thumb is drawn where the cursor is.
internal sealed class SmoothSlider : Control
{
    private const int ThumbRadius = 8;
    private const int ThumbActiveRadius = 10;
    private const int TrackThickness = 4;
    private const double Easing = 0.28;

    private readonly System.Windows.Forms.Timer animator = new System.Windows.Forms.Timer();
    private double displayPosition;
    private double targetPosition;
    private int minimum;
    private int maximum = 100;
    private int current;
    private bool dragging;
    private bool hovering;

    public event EventHandler ValueChanged;

    public SmoothSlider()
    {
        SetStyle(ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable, true);
        TabStop = true;

        TrackColor = Color.FromArgb(58, 58, 66);
        AccentColor = Color.FromArgb(245, 185, 66);
        ThumbColor = Color.FromArgb(245, 185, 66);
        DisabledColor = Color.FromArgb(64, 64, 72);
        NotchColor = Color.FromArgb(138, 138, 148);
        Notch = -1;
        SmallChange = 1;
        LargeChange = 5;

        animator.Interval = 15;
        animator.Tick += Animate;
    }

    public Color TrackColor { get; set; }
    public Color AccentColor { get; set; }
    public Color ThumbColor { get; set; }
    public Color DisabledColor { get; set; }
    public Color NotchColor { get; set; }

    // Where the hardware backlight range ends, 0~1. Negative hides the notch.
    public double Notch { get; set; }

    // Vertical centre of the track. Zero centres it, which leaves no room under it
    // for the notch marker.
    public int TrackCentre { get; set; }

    private int Centre
    {
        get { return TrackCentre > 0 ? TrackCentre : Height / 2; }
    }
    public int SmallChange { get; set; }
    public int LargeChange { get; set; }

    public int Minimum
    {
        get { return minimum; }
        set { minimum = value; Value = current; }
    }

    public int Maximum
    {
        get { return maximum; }
        set { maximum = value; Value = current; }
    }

    public int Value
    {
        get { return current; }
        set
        {
            int clamped = Math.Max(minimum, Math.Min(maximum, value));
            double position = maximum > minimum
                ? (clamped - minimum) / (double)(maximum - minimum)
                : 0;
            SetPosition(position, true);
        }
    }

    // Track geometry, so callers can line markers up with it exactly.
    public int TrackLeft
    {
        get { return ThumbActiveRadius; }
    }

    public int TrackWidth
    {
        get { return Math.Max(1, Width - ThumbActiveRadius * 2); }
    }

    // Where the thumb is drawn, 0~1. Equals the cursor position while dragging.
    public double Position
    {
        get { return displayPosition; }
    }

    private void SetPosition(double position, bool animate)
    {
        targetPosition = Math.Max(0, Math.Min(1, position));

        int updated = (int)Math.Round(minimum + targetPosition * (maximum - minimum));
        bool changed = updated != current;
        current = updated;

        if (animate && !dragging)
        {
            animator.Start();
        }
        else
        {
            animator.Stop();
            displayPosition = targetPosition;
        }

        Invalidate();

        if (changed && ValueChanged != null)
            ValueChanged(this, EventArgs.Empty);
    }

    private void Animate(object sender, EventArgs e)
    {
        double gap = targetPosition - displayPosition;
        if (Math.Abs(gap) < 0.0015)
        {
            displayPosition = targetPosition;
            animator.Stop();
        }
        else
        {
            displayPosition += gap * Easing;
        }
        Invalidate();
    }

    private double PositionFromX(int x)
    {
        return (x - TrackLeft) / (double)TrackWidth;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left)
            return;

        Focus();
        Capture = true;

        // Clicking the track glides to that point; the drag that follows is direct.
        SetPosition(PositionFromX(e.X), true);
        dragging = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
            SetPosition(PositionFromX(e.X), false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hovering = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovering = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Enabled)
            Value = current + (e.Delta > 0 ? SmallChange : -SmallChange);
    }

    protected override bool IsInputKey(Keys key)
    {
        switch (key)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.PageUp:
            case Keys.PageDown:
            case Keys.Home:
            case Keys.End:
                return true;
            default:
                return base.IsInputKey(key);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled)
            return;

        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                Value = current - SmallChange;
                break;
            case Keys.Right:
            case Keys.Up:
                Value = current + SmallChange;
                break;
            case Keys.PageDown:
                Value = current - LargeChange;
                break;
            case Keys.PageUp:
                Value = current + LargeChange;
                break;
            case Keys.Home:
                Value = minimum;
                break;
            case Keys.End:
                Value = maximum;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);

        int centre = Centre;
        int top = centre - TrackThickness / 2;
        float thumbX = TrackLeft + (float)(displayPosition * TrackWidth);
        int radius = hovering || dragging ? ThumbActiveRadius : ThumbRadius;

        using (Brush brush = new SolidBrush(Enabled ? TrackColor : DisabledColor))
            FillCapsule(graphics, brush, TrackLeft, top, TrackWidth, TrackThickness);

        if (Enabled)
        {
            float filled = thumbX - TrackLeft;
            if (filled > 0)
                using (Brush brush = new SolidBrush(AccentColor))
                    FillCapsule(graphics, brush, TrackLeft, top, filled, TrackThickness);

            if (Notch >= 0 && Notch <= 1)
            {
                // Snapped to a whole pixel column and drawn without anti-aliasing: at
                // this size sub-pixel edges turn a 2px mark into a grey smear. The
                // track itself is left unbroken.
                int notchX = (int)Math.Round(TrackLeft + Notch * TrackWidth);
                SmoothingMode previous = graphics.SmoothingMode;
                graphics.SmoothingMode = SmoothingMode.None;

                using (Brush brush = new SolidBrush(NotchColor))
                    graphics.FillRectangle(brush, notchX - 1, centre + 4, 2, 6);

                graphics.SmoothingMode = previous;

                // A crescent stands in for the caption: below this point the backlight
                // is already at its floor and the screen is dimmed instead.
                const float moonRadius = 5.4f;
                const float bite = 4.1f;
                const double tilt = -38 * Math.PI / 180.0;

                float biteX = (float)(bite * Math.Cos(tilt));
                float biteY = (float)(bite * Math.Sin(tilt));

                // Cutting the disc leaves the crescent sitting left of the disc centre,
                // so the whole thing is nudged back to sit under the tick.
                float moonX = notchX + biteX / 2f;
                float moonY = centre + 18 + biteY / 2f;

                using (Brush brush = new SolidBrush(NotchColor))
                    graphics.FillEllipse(brush, moonX - moonRadius, moonY - moonRadius,
                        moonRadius * 2, moonRadius * 2);
                using (Brush brush = new SolidBrush(BackColor))
                    graphics.FillEllipse(brush, moonX - moonRadius + biteX,
                        moonY - moonRadius + biteY, moonRadius * 2, moonRadius * 2);
            }

            // A ring of background keeps the thumb from merging into the filled track.
            using (Brush brush = new SolidBrush(BackColor))
                graphics.FillEllipse(brush, thumbX - radius - 2, centre - radius - 2,
                    (radius + 2) * 2, (radius + 2) * 2);

            using (Brush brush = new SolidBrush(ThumbColor))
                graphics.FillEllipse(brush, thumbX - radius, centre - radius, radius * 2, radius * 2);

            if (Focused)
                using (Pen pen = new Pen(Color.FromArgb(110, 255, 255, 255)))
                    graphics.DrawEllipse(pen, thumbX - radius - 3, centre - radius - 3,
                        (radius + 3) * 2, (radius + 3) * 2);
        }
    }

    private static void FillCapsule(Graphics graphics, Brush brush,
        float x, float y, float width, float height)
    {
        if (width <= 0)
            return;

        if (width <= height)
        {
            graphics.FillEllipse(brush, x, y, height, height);
            return;
        }

        using (GraphicsPath path = new GraphicsPath())
        {
            path.AddArc(x, y, height, height, 90, 180);
            path.AddArc(x + width - height, y, height, height, 270, 180);
            path.CloseFigure();
            graphics.FillPath(brush, path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animator.Stop();
            animator.Dispose();
        }
        base.Dispose(disposing);
    }
}

// A single DDC/CI write costs well over 100ms. Doing that per scroll tick on the UI
// thread makes the slider lurch, so writes run here and intermediate values collapse.
internal sealed class BrightnessWriter : IDisposable
{
    private readonly object queueGate = new object();
    private readonly object writeGate = new object();
    private readonly Dictionary<DdcMonitor, int> pending = new Dictionary<DdcMonitor, int>();
    private readonly AutoResetEvent signal = new AutoResetEvent(false);
    private readonly Thread worker;
    private volatile bool running = true;

    public event Action<DdcMonitor, Exception> Written;

    public BrightnessWriter()
    {
        worker = new Thread(Loop);
        worker.IsBackground = true;
        worker.Name = "DDC brightness writer";
        worker.Start();
    }

    // Returns at once. Only the newest request per monitor survives.
    public void Request(DdcMonitor monitor, int percent)
    {
        lock (queueGate)
            pending[monitor] = percent;
        signal.Set();
    }

    // Runs the action with no write in flight, so handles cannot be freed mid-write.
    public void WithWritesPaused(Action action)
    {
        lock (writeGate)
        {
            lock (queueGate)
                pending.Clear();
            action();
        }
    }

    private void Loop()
    {
        while (running)
        {
            signal.WaitOne(200);

            while (running)
            {
                DdcMonitor monitor = null;
                int percent = 0;

                lock (queueGate)
                {
                    foreach (KeyValuePair<DdcMonitor, int> item in pending)
                    {
                        monitor = item.Key;
                        percent = item.Value;
                        break;
                    }

                    if (monitor == null)
                        break;

                    pending.Remove(monitor);
                }

                Exception error = null;
                lock (writeGate)
                {
                    try
                    {
                        monitor.SetBrightness(percent);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                }

                Action<DdcMonitor, Exception> handler = Written;
                if (handler != null)
                    handler(monitor, error);
            }
        }
    }

    public void Dispose()
    {
        running = false;
        signal.Set();
        worker.Join(1500);
        signal.Close();
    }
}

internal static class Displays
{
    private const uint ActivePathsOnly = 2;
    private const uint SourceNameInfo = 1;
    private const uint TargetNameInfo = 2;

    public static bool TryGetBounds(string deviceName, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (String.IsNullOrEmpty(deviceName))
            return false;

        foreach (Screen screen in Screen.AllScreens)
        {
            if (String.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                bounds = screen.Bounds;
                return true;
            }
        }
        return false;
    }

    // Maps a GDI display name to the monitor name Windows shows in display settings.
    // DDC/CI only reports a generic description, which is identical on every monitor.
    public static Dictionary<string, string> FriendlyNames()
    {
        Dictionary<string, string> names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        uint pathCount, modeCount;
        if (NativeMethods.GetDisplayConfigBufferSizes(
                ActivePathsOnly, out pathCount, out modeCount) != 0)
            return names;

        NativeMethods.PathInfo[] paths = new NativeMethods.PathInfo[pathCount];
        NativeMethods.ModeInfo[] modes = new NativeMethods.ModeInfo[modeCount];
        if (NativeMethods.QueryDisplayConfig(
                ActivePathsOnly, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return names;

        for (int i = 0; i < pathCount; i++)
        {
            NativeMethods.SourceDeviceName source = new NativeMethods.SourceDeviceName();
            source.Header.Type = SourceNameInfo;
            source.Header.Size = (uint)Marshal.SizeOf(typeof(NativeMethods.SourceDeviceName));
            source.Header.AdapterId = paths[i].Source.AdapterId;
            source.Header.Id = paths[i].Source.Id;
            if (NativeMethods.DisplayConfigGetDeviceInfo(ref source) != 0)
                continue;

            NativeMethods.TargetDeviceName target = new NativeMethods.TargetDeviceName();
            target.Header.Type = TargetNameInfo;
            target.Header.Size = (uint)Marshal.SizeOf(typeof(NativeMethods.TargetDeviceName));
            target.Header.AdapterId = paths[i].Target.AdapterId;
            target.Header.Id = paths[i].Target.Id;
            if (NativeMethods.DisplayConfigGetDeviceInfo(ref target) != 0)
                continue;

            if (!String.IsNullOrWhiteSpace(target.FriendlyName))
                names[source.GdiDeviceName] = target.FriendlyName.Trim();
        }
        return names;
    }
}

internal sealed class DimOverlay : Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    public DimOverlay(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        TopMost = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }
}

internal sealed class DdcMonitor : IDisposable
{
    public string Name { get; set; }
    public string DeviceName { get; private set; }
    public IntPtr Handle { get; private set; }
    public uint Minimum { get; private set; }
    public uint Maximum { get; private set; }
    public uint Current { get; private set; }

    // Slider value (0~100) for this monitor, including the overlay dimming range.
    public int Level { get; set; }

    // False once the monitor is known to discard brightness writes.
    // Set on the writer thread, read on the UI thread.
    public bool BrightnessAccepted { get { return accepted; } }

    private volatile bool accepted = true;
    private bool verified;

    public DdcMonitor(
        string name, string deviceName, IntPtr handle, uint minimum, uint current, uint maximum)
    {
        Name = String.IsNullOrWhiteSpace(name) ? "Monitor" : name;
        DeviceName = deviceName;
        Handle = handle;
        Minimum = minimum;
        Current = current;
        Maximum = maximum;
    }

    public int BrightnessPercent
    {
        get
        {
            if (Maximum <= Minimum)
                return 0;
            return (int)Math.Round((Current - Minimum) * 100.0 / (Maximum - Minimum));
        }
    }

    public void SetBrightness(int percent)
    {
        percent = Math.Max(0, Math.Min(100, percent));
        uint value = Minimum;
        if (Maximum > Minimum)
            value = Minimum + (uint)Math.Round((Maximum - Minimum) * (percent / 100.0));

        // A monitor that overrides brightness drifts on its own, so the cached value
        // must not be allowed to skip later writes.
        if (value == Current && BrightnessAccepted)
            return;

        if (!NativeMethods.SetMonitorBrightness(Handle, value))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Current = value;

        // A monitor whose backlight is owned by an eco or eye-saver mode reports the write
        // as successful and then discards it, so confirm the first one against the hardware.
        if (verified)
            return;

        verified = true;

        uint actualMinimum, actual, actualMaximum;
        if (!NativeMethods.GetMonitorBrightness(
                Handle, out actualMinimum, out actual, out actualMaximum))
            return;

        accepted = actual == value;
        Current = actual;
    }

    public override string ToString() { return Name; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.DestroyPhysicalMonitor(Handle);
            Handle = IntPtr.Zero;
        }
    }
}

internal static class DdcMonitorFinder
{
    public static List<DdcMonitor> Find()
    {
        List<DdcMonitor> result = new List<DdcMonitor>();
        Dictionary<string, string> friendlyNames = Displays.FriendlyNames();
        NativeMethods.MonitorEnumProc callback = delegate(
            IntPtr logical, IntPtr hdc, ref NativeMethods.Rect rect, IntPtr data)
        {
            uint count;
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logical, out count) || count == 0)
                return true;

            NativeMethods.PhysicalMonitor[] physical = new NativeMethods.PhysicalMonitor[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logical, count, physical))
                return true;

            NativeMethods.MonitorInfoEx info = new NativeMethods.MonitorInfoEx();
            info.Size = Marshal.SizeOf(typeof(NativeMethods.MonitorInfoEx));
            string deviceName = NativeMethods.GetMonitorInfo(logical, ref info) ? info.Device : null;

            foreach (NativeMethods.PhysicalMonitor item in physical)
            {
                uint minimum, current, maximum;
                if (NativeMethods.GetMonitorBrightness(
                    item.Handle, out minimum, out current, out maximum))
                {
                    string friendly;
                    string name = deviceName != null
                        && friendlyNames.TryGetValue(deviceName, out friendly)
                        ? friendly : item.Description;

                    result.Add(new DdcMonitor(
                        name, deviceName, item.Handle, minimum, current, maximum));
                }
                else
                {
                    NativeMethods.DestroyPhysicalMonitor(item.Handle);
                }
            }
            return true;
        };

        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        MakeNamesDistinct(result);
        return result;
    }

    // Identical monitors report identical names, so widen them until the list is unambiguous.
    private static void MakeNamesDistinct(List<DdcMonitor> monitors)
    {
        foreach (DdcMonitor monitor in Duplicates(monitors))
        {
            Rectangle bounds;
            if (Displays.TryGetBounds(monitor.DeviceName, out bounds))
                monitor.Name += " (" + bounds.Width + "x" + bounds.Height + ")";
        }

        foreach (DdcMonitor monitor in Duplicates(monitors))
        {
            if (!String.IsNullOrEmpty(monitor.DeviceName))
                monitor.Name += " " + monitor.DeviceName;
        }

        int index = 1;
        foreach (DdcMonitor monitor in Duplicates(monitors))
            monitor.Name += " #" + index++;
    }

    private static List<DdcMonitor> Duplicates(List<DdcMonitor> monitors)
    {
        List<DdcMonitor> result = new List<DdcMonitor>();
        foreach (DdcMonitor monitor in monitors)
        {
            int count = 0;
            foreach (DdcMonitor other in monitors)
                if (String.Equals(monitor.Name, other.Name, StringComparison.OrdinalIgnoreCase))
                    count++;

            if (count > 1)
                result.Add(monitor);
        }
        return result;
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PhysicalMonitor
    {
        [MarshalAs(UnmanagedType.SysInt)]
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    internal delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo Source;
        public PathTargetInfo Target;
        public uint Flags;
    }

    // The trailing union is opaque here; only its size matters to QueryDisplayConfig.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] Union;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceDeviceName
    {
        public DeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string GdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string FriendlyName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DevicePath;
    }

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr BeginPaint(IntPtr handle, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(IntPtr handle, ref PaintStruct paint);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        IntPtr handle, int attribute, ref int value, int size);

    internal static void UseDarkTitleBar(IntPtr handle)
    {
        int enabled = 1;
        // 20 on current Windows 10/11, 19 on the builds that introduced the attribute.
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorBrightness(
        IntPtr hMonitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetMonitorBrightness(IntPtr hMonitor, uint brightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);
}
