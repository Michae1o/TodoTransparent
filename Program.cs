using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Diagnostics;
using Microsoft.Win32;

[DataContract]
public class AppSettings {
    [DataMember] public string AccentColor { get; set; }
    [DataMember] public string BackgroundImage { get; set; }
    [DataMember] public string BackgroundStretch { get; set; }
    [DataMember] public string CropRatio { get; set; }
    [DataMember] public bool IsLightTheme { get; set; }
    public AppSettings() { AccentColor = "#FF3CA0FF"; BackgroundStretch = "UniformToFill"; CropRatio = "原始比例"; }
}

[DataContract]
public class SubTask {
    [DataMember] public string Text { get; set; }
    [DataMember] public bool Completed { get; set; }
    [DataMember] public DateTime CreatedAt { get; set; }
}

[DataContract]
public class TodoItem {
    [DataMember] public string Id { get; set; }
    [DataMember] public string Text { get; set; }
    [DataMember] public bool Completed { get; set; }
    [DataMember] public int Priority { get; set; }
    [DataMember] public DateTime CreatedAt { get; set; }
    [DataMember] public DateTime? DueTime { get; set; }
    [DataMember] public DateTime? CompletedAt { get; set; }
    [DataMember] public List<SubTask> SubTasks { get; set; }

    public TodoItem() {
        Id = Guid.NewGuid().ToString("N");
        SubTasks = new List<SubTask>();
    }
}

enum SnapEdge { None, Left, Right, Top, Bottom }

public class MainWindow : Window {
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLong64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_TRANSPARENT = 0x20;

    static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) {
        return IntPtr.Size == 8 ? SetWindowLong64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll")]
    static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    enum WindowCompositionAttribute { WCA_ACCENT_POLICY = 19 }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    enum AccentState {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
    }

    readonly string dataPath;
    readonly string settingsPath;
    readonly List<TodoItem> todos = new List<TodoItem>();
    readonly StackPanel taskPanel;
    readonly TextBox inputBox;
    readonly ScrollViewer scrollViewer;
    readonly Canvas dragOverlay;
    Grid detailOverlay;
    Button btnPass;
    Button btnHistory;
    Button btnGhost;
    Button btnSettings;
    Button btnAdd;
    TextBlock titleLabel;
    Border rootBorder;
    Border inputWrap;
    bool isPassthrough = false;
    bool isHistoryView = false;
    bool isResizing = false;
    bool isDraggingWindow = false;
    bool isDraggingItem = false;
    Point resizeStart;
    Point dragStart;
    Point windowStart;
    Point itemDragOffset;
    Size sizeStart;
    SnapEdge currentSnap = SnapEdge.None;
    double normalLeft, normalTop;
    bool isExpanded = true;
    int dragSourceIndex = -1;
    Border dragGhost = null;
    DispatcherTimer mouseCheckTimer;
    DispatcherTimer hideDelayTimer;
    readonly List<ActiveAnimation> activeAnimations = new List<ActiveAnimation>();
    readonly HashSet<string> notifiedTasks = new HashSet<string>();
    DispatcherTimer reminderTimer;
    Color accentColor = Color.FromArgb(255, 60, 160, 255);
    string backgroundImagePath;
    string backgroundStretch = "UniformToFill";
    string cropRatio = "原始比例";
    bool isGhostMode = false;
    bool isLightTheme = false;
    DispatcherTimer ghostFadeTimer;
    System.Windows.Controls.Image bgImage;
    Border bgOverlay;

    enum EasingType { EaseOutCubic, EaseOutQuint, Spring }

    struct ActiveAnimation {
        public DependencyObject Target;
        public DependencyProperty Property;
        public double From;
        public double To;
        public int DurationMs;
        public Stopwatch Sw;
        public EasingType Easing;
        public Action OnComplete;
    }

    public MainWindow() {
        Title = "Todo透明便签";
        Width = 320;
        Height = 500;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        try {
            if (File.Exists("icon.png"))
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(System.IO.Path.Combine(Environment.CurrentDirectory, "icon.png"), UriKind.Absolute));
        } catch { }

        var area = SystemParameters.WorkArea;
        Left = area.Width - Width - 20;
        Top = 100;

        dataPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TodoTransparent", "todos.json");
        settingsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TodoTransparent", "settings.json");
        Load();
        LoadSettings();

        // Apple-style subtle drop shadow for the entire window
        var shadowEffect = new System.Windows.Media.Effects.DropShadowEffect {
            BlurRadius = 28,
            ShadowDepth = 10,
            Direction = 270,
            Color = Color.FromArgb(140, 0, 0, 0),
            Opacity = 0.4
        };

        // Theme-aware gradient background
        var gradientBrush = new LinearGradientBrush {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        gradientBrush.GradientStops.Add(new GradientStop(WinBgTop(), 0));
        gradientBrush.GradientStops.Add(new GradientStop(WinBgBottom(), 1));

        rootBorder = new Border {
            CornerRadius = new CornerRadius(24),
            Background = gradientBrush,
            BorderBrush = isLightTheme ? new SolidColorBrush(Color.FromArgb(120, 40, 40, 45)) : new SolidColorBrush(Color.FromArgb(140, 200, 200, 210)),
            BorderThickness = new Thickness(1),
            Effect = shadowEffect
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Background image layers
        bgImage = new System.Windows.Controls.Image {
            Stretch = Stretch.UniformToFill,
            Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 10 }
        };
        Grid.SetRow(bgImage, 0);
        Grid.SetRowSpan(bgImage, 3);
        Panel.SetZIndex(bgImage, -2);
        grid.Children.Add(bgImage);

        bgOverlay = new Border {
            Background = new SolidColorBrush(Color.FromArgb(140, 20, 20, 24)),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(bgOverlay, 0);
        Grid.SetRowSpan(bgOverlay, 3);
        Panel.SetZIndex(bgOverlay, -1);
        grid.Children.Add(bgOverlay);

        ApplyBackgroundImage();

        // Title bar - Apple-style minimal
        var titleBar = new Grid {
            Background = Brushes.Transparent,
            Height = 44
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        titleBar.MouseLeftButtonDown += (s, e) => {
            isDraggingWindow = true;
            dragStart = PointToScreen(e.GetPosition(this));
            windowStart = new Point(Left, Top);
            titleBar.CaptureMouse();
            if (currentSnap != SnapEdge.None && !isExpanded) ExpandFromSnap();
            normalLeft = Left;
            normalTop = Top;
        };
        titleBar.MouseMove += (s, e) => {
            if (!isDraggingWindow) return;
            var pos = PointToScreen(e.GetPosition(this));
            Left = windowStart.X + (pos.X - dragStart.X);
            Top = windowStart.Y + (pos.Y - dragStart.Y);
            currentSnap = SnapEdge.None;
            isExpanded = true;
        };
        titleBar.MouseLeftButtonUp += (s, e) => {
            isDraggingWindow = false;
            titleBar.ReleaseMouseCapture();
            CheckAndSnap();
        };

        titleLabel = new TextBlock {
            Text = "待办事项",
            Foreground = TB(TextPri()),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0)
        };
        Grid.SetColumn(titleLabel, 0);
        titleBar.Children.Add(titleLabel);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        Grid.SetColumn(btns, 1);

        // Apple-style circular traffic-light buttons
        btnSettings = CreateTrafficLightBtn("⚙", new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)), new SolidColorBrush(Color.FromArgb(160, 160, 160, 170)));
        btnSettings.ToolTip = "设置";
        btnSettings.Click += (s, e) => OpenSettings();

        btnHistory = CreateTrafficLightBtn("◷", new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)), new SolidColorBrush(Color.FromArgb(160, 120, 120, 128)));
        btnHistory.ToolTip = "历史事项";
        btnHistory.Click += (s, e) => ToggleHistoryView();

        btnGhost = CreateTrafficLightBtn("○", new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)), new SolidColorBrush(Color.FromArgb(160, 180, 180, 190)));
        btnGhost.ToolTip = "幽灵模式";
        btnGhost.Click += (s, e) => ToggleGhostMode();

        btnPass = CreateTrafficLightBtn("↗", new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)), new SolidColorBrush(Color.FromArgb(160, 80, 160, 255)));
        btnPass.ToolTip = "切换鼠标穿透";
        btnPass.Click += (s, e) => TogglePassthrough();

        var btnMin = CreateTrafficLightBtn("—", new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)), new SolidColorBrush(Color.FromArgb(160, 255, 190, 50)));
        btnMin.ToolTip = "最小化";
        btnMin.Click += (s, e) => WindowState = WindowState.Minimized;

        var btnClose = CreateTrafficLightBtn("×", new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)), new SolidColorBrush(Color.FromArgb(160, 255, 95, 85)));
        btnClose.ToolTip = "关闭";
        btnClose.Click += (s, e) => Close();

        btns.Children.Add(btnSettings);
        btns.Children.Add(btnHistory);
        btns.Children.Add(btnGhost);
        btns.Children.Add(btnPass);
        btns.Children.Add(btnMin);
        btns.Children.Add(btnClose);
        titleBar.Children.Add(btns);

        // Input area - theme-aware search bar
        inputWrap = new Border {
            CornerRadius = new CornerRadius(10),
            Background = TB(InputBgC()),
            BorderBrush = isLightTheme ? new SolidColorBrush(Color.FromArgb(120, 40, 40, 45)) : new SolidColorBrush(Color.FromArgb(140, 200, 200, 210)),
            BorderThickness = new Thickness(1),
            Height = 38,
            Margin = new Thickness(16, 12, 16, 12)
        };
        Grid.SetRow(inputWrap, 1);

        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        inputBox = new TextBox {
            Background = Brushes.Transparent,
            Foreground = TB(TextPri()),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 14
        };
        inputBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) AddTodo(); };
        Grid.SetColumn(inputBox, 0);

        btnAdd = CreateBtn("+", 28, 28, 6);
        btnAdd.FontSize = 16;
        btnAdd.Margin = new Thickness(2);
        btnAdd.Click += (s, e) => AddTodo();
        btnAdd.Background = isLightTheme ? new SolidColorBrush(Color.FromArgb(200, 40, 40, 45)) : new SolidColorBrush(Color.FromArgb(200, 200, 200, 210));
        Grid.SetColumn(btnAdd, 1);

        inputGrid.Children.Add(inputBox);
        inputGrid.Children.Add(btnAdd);
        inputWrap.Child = inputGrid;

        // Drag overlay for item reordering ghost
        dragOverlay = new Canvas {
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(dragOverlay, 3);
        Panel.SetZIndex(dragOverlay, 100);

        // Task list
        scrollViewer = new ScrollViewer {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(14, 0, 14, 14),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false
        };
        scrollViewer.SizeChanged += (s, e) => UpdateDynamicHeights();
        taskPanel = new StackPanel { Orientation = Orientation.Vertical };
        scrollViewer.Content = taskPanel;
        Grid.SetRow(scrollViewer, 2);

        scrollViewer.Loaded += (s, e) => {
            foreach (var sb in FindVisualChildren<ScrollBar>(scrollViewer)) {
                sb.Opacity = 0.08;
                sb.Width = 3;
                sb.Background = Brushes.Transparent;
                foreach (var thumb in FindVisualChildren<Thumb>(sb)) {
                    string thumbXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Thumb'>" +
                        "<Border Background='{TemplateBinding Background}' CornerRadius='2' Width='3'/>" +
                        "</ControlTemplate>";
                    thumb.Template = (ControlTemplate)XamlReader.Parse(thumbXaml);
                    thumb.Background = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
                }
            }
        };

        // Resize handle
        var resizeHandle = new Rectangle {
            Width = 18, Height = 18, Fill = Brushes.Transparent,
            Cursor = Cursors.SizeNWSE,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 6)
        };
        resizeHandle.MouseLeftButtonDown += (s, e) => {
            isResizing = true;
            resizeStart = PointToScreen(Mouse.GetPosition(this));
            sizeStart = new Size(Width, Height);
            resizeHandle.CaptureMouse();
            e.Handled = true;
        };
        resizeHandle.MouseMove += (s, e) => {
            if (!isResizing) return;
            var pos = PointToScreen(Mouse.GetPosition(this));
            Width = Math.Max(220, sizeStart.Width + pos.X - resizeStart.X);
            Height = Math.Max(160, sizeStart.Height + pos.Y - resizeStart.Y);
        };
        resizeHandle.MouseLeftButtonUp += (s, e) => {
            isResizing = false;
            resizeHandle.ReleaseMouseCapture();
        };
        Grid.SetRow(resizeHandle, 2);

        // Task detail overlay (double-click popup)
        detailOverlay = new Grid {
            Background = TB(OverlayBgC()),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(detailOverlay, 3);
        Panel.SetZIndex(detailOverlay, 200);

        grid.Children.Add(titleBar);
        grid.Children.Add(inputWrap);
        grid.Children.Add(scrollViewer);
        grid.Children.Add(dragOverlay);
        grid.Children.Add(resizeHandle);
        grid.Children.Add(detailOverlay);

        rootBorder.Child = grid;
        Content = rootBorder;

        // Global mouse for item drag ghost
        MouseMove += OnWindowMouseMove;
        MouseLeftButtonUp += OnWindowMouseUp;

        Loaded += (s, e) => {
            StartMouseCheck();
            StartReminderCheck();
            Render();
        };
        SourceInitialized += (s, e) => EnableAcrylic();

        inputBox.CaretBrush = isLightTheme ? new SolidColorBrush(Color.FromArgb(200, 40, 40, 45)) : new SolidColorBrush(Color.FromArgb(200, 200, 200, 210));
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject {
        if (depObj == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++) {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T) yield return (T)child;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }

    ControlTemplate CreateRoundedTemplate(double radius) {
        string xaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<Border Background='{TemplateBinding Background}' CornerRadius='" + radius + "' BorderThickness='0'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
            "</Border></ControlTemplate>";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    Button CreateBtn(string content, double w, double h, double radius) {
        return new Button {
            Content = content, Width = w, Height = h,
            Background = isLightTheme ? new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0), FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Template = CreateRoundedTemplate(radius)
        };
    }

    Button CreateTrafficLightBtn(string content, Brush defaultBg, Brush hoverBg) {
        var btn = new Button {
            Content = content,
            Width = 20, Height = 20,
            Background = defaultBg,
            Foreground = TB(BtnDefFg()),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(3, 0, 3, 0),
            FontSize = 10,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Template = CreateRoundedTemplate(10),
            Tag = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var scale = new ScaleTransform(1, 1);
        btn.RenderTransform = scale;
        btn.PreviewMouseDown += (s, e) => {
            scale.ScaleX = 0.88;
            scale.ScaleY = 0.88;
        };
        btn.PreviewMouseUp += (s, e) => {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)) {
                                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        };
        btn.MouseEnter += (s, e) => {
            if (!(bool)btn.Tag) {
                btn.Background = hoverBg;
                btn.Foreground = TB(BtnEnterFg());
            }
        };
        btn.MouseLeave += (s, e) => {
            if (!(bool)btn.Tag) {
                btn.Background = defaultBg;
                btn.Foreground = TB(BtnDefFg());
            }
        };
        return btn;
    }

    void EnableAcrylic() {
        try {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var accent = new AccentPolicy {
                AccentState = (int)AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = isLightTheme ? 0xD0FFFFFFU : 0xD0000000U
            };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(ptr);
        } catch { }
    }

    void DisableAcrylic() {
        try {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var accent = new AccentPolicy {
                AccentState = (int)AccentState.ACCENT_DISABLED,
                AccentFlags = 0,
                GradientColor = 0
            };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(ptr);
        } catch { }
    }

    Color ParseColor(string hex) {
        if (string.IsNullOrEmpty(hex)) return Color.FromArgb(255, 60, 160, 255);
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 8) {
            return Color.FromArgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16),
                Convert.ToByte(hex.Substring(6, 2), 16));
        }
        if (hex.Length == 6) {
            return Color.FromRgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        return Color.FromArgb(255, 60, 160, 255);
    }

    Color HsvToRgb(double h, double s, double v) {
        double c = v * s;
        double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb(
            (byte)Math.Min(255, (r + m) * 255),
            (byte)Math.Min(255, (g + m) * 255),
            (byte)Math.Min(255, (b + m) * 255));
    }

    void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v) {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        h = 0; s = 0; v = max;
        double d = max - min;
        if (max != 0) s = d / max;
        if (d != 0) {
            if (max == rd) h = (gd - bd) / d + (gd < bd ? 6 : 0);
            else if (max == gd) h = (bd - rd) / d + 2;
            else h = (rd - gd) / d + 4;
            h *= 60;
        }
    }

    Brush GetAccentBrush(byte alpha) {
        return new SolidColorBrush(Color.FromArgb(alpha, accentColor.R, accentColor.G, accentColor.B));
    }

    string GetAccentHex() {
        return accentColor.ToString();
    }

    Brush GetPriorityBrush(int priority) {
        switch (priority) {
            case 1: return TB(PrioBg1());
            case 2: return TB(PrioBg2());
            default: return TB(PrioBg0());
        }
    }

    Brush GetPriorityBtnBrush(int priority) {
        switch (priority) {
            case 1: return new SolidColorBrush(Color.FromArgb(200, 255, 85, 70));
            case 2: return new SolidColorBrush(Color.FromArgb(200, 255, 190, 45));
            default: return new SolidColorBrush(Color.FromArgb(90, 140, 140, 150));
        }
    }

    Brush GetPriorityBrushHover(int priority) {
        switch (priority) {
            case 1: return TB(PrioHov1());
            case 2: return TB(PrioHov2());
            default: return TB(PrioHov0());
        }
    }

    Brush GetPriorityAccentBrush(int priority) {
        switch (priority) {
            case 1: return new SolidColorBrush(Color.FromArgb(220, 255, 95, 75));
            case 2: return new SolidColorBrush(Color.FromArgb(220, 255, 200, 55));
            default: return new SolidColorBrush(Color.FromArgb(120, 140, 140, 150));
        }
    }

    // Theme color helpers
    Color TC(byte a, byte r, byte g, byte b) { return Color.FromArgb(a, r, g, b); }
    Brush TB(Color c) { return new SolidColorBrush(c); }
    Color WinBgTop()    { return isLightTheme ? TC(230, 255, 255, 255) : TC(240, 42, 42, 50); }
    Color WinBgBottom() { return isLightTheme ? TC(210, 235, 240, 245) : TC(235, 22, 22, 28); }
    Color TextPri()     { return isLightTheme ? TC(240, 40, 40, 45)  : TC(240, 245, 245, 247); }
    Color TextSec()     { return isLightTheme ? TC(220, 80, 80, 90)   : TC(180, 160, 160, 170); }
    Color InputBgC()    { return isLightTheme ? TC(60, 0, 0, 0)       : TC(35, 255, 255, 255); }
    Color CardBgC()     { return isLightTheme ? TC(245, 255, 255, 255) : TC(240, 32, 32, 36); }
    Color OverlayBgC()  { return isLightTheme ? TC(140, 240, 240, 242) : TC(180, 0, 0, 0); }
    Color PopupBgC()    { return isLightTheme ? TC(230, 255, 255, 255) : TC(230, 35, 35, 40); }
    Color PopTextPri()  { return isLightTheme ? TC(240, 40, 40, 45)  : TC(240, 245, 245, 247); }
    Color PopTextSec()  { return isLightTheme ? TC(200, 80, 80, 90)   : TC(160, 180, 180, 190); }
    Color CbBgC()       { return isLightTheme ? TC(255, 250, 250, 252) : TC(255, 45, 48, 52); }
    Color CbFgC()       { return isLightTheme ? TC(240, 40, 40, 45)  : TC(240, 245, 245, 247); }
    Color SepC()        { return isLightTheme ? TC(30, 0, 0, 0)       : TC(18, 255, 255, 255); }
    Color PrioBg1()     { return isLightTheme ? TC(35, 255, 90, 70)   : TC(30, 255, 90, 70); }
    Color PrioBg2()     { return isLightTheme ? TC(35, 255, 195, 50)  : TC(30, 255, 195, 50); }
    Color PrioBg0()     { return isLightTheme ? TC(30, 0, 0, 0)       : TC(22, 255, 255, 255); }
    Color PrioHov1()    { return isLightTheme ? TC(50, 255, 90, 70)   : TC(45, 255, 90, 70); }
    Color PrioHov2()    { return isLightTheme ? TC(50, 255, 195, 50)  : TC(45, 255, 195, 50); }
    Color PrioHov0()    { return isLightTheme ? TC(45, 0, 0, 0)       : TC(35, 255, 255, 255); }
    Color EmptyCirc()   { return isLightTheme ? TC(30, 0, 0, 0)       : TC(18, 255, 255, 255); }
    Color EmptyDot()    { return isLightTheme ? TC(160, 120, 120, 130) : TC(80, 160, 160, 170); }
    Color EmptyTxt()    { return isLightTheme ? TC(200, 80, 80, 90)   : TC(100, 160, 160, 170); }
    Color HistItemBg()  { return isLightTheme ? TC(30, 0, 0, 0)       : TC(18, 255, 255, 255); }
    Color HistItemHov() { return isLightTheme ? TC(50, 0, 0, 0)       : TC(30, 255, 255, 255); }
    Color HistTxt()     { return isLightTheme ? TC(200, 60, 60, 70)   : TC(130, 150, 150, 160); }
    Color HistDate()    { return isLightTheme ? TC(180, 80, 80, 90)   : TC(100, 160, 160, 170); }
    Color HistDelFg()   { return isLightTheme ? TC(180, 80, 80, 90)   : TC(0, 200, 200, 210); }
    Color OverdueC()    { return isLightTheme ? TC(240, 220, 60, 40)  : TC(230, 255, 150, 100); }
    Color EditBgC()     { return isLightTheme ? TC(60, 0, 0, 0)       : TC(70, 0, 0, 0); }
    Color SubTxt()      { return isLightTheme ? TC(240, 50, 50, 55)   : TC(200, 210, 210, 215); }
    Color SubDoneTxt()  { return isLightTheme ? TC(180, 120, 120, 130) : TC(100, 140, 140, 150); }
    Color DelEnter()    { return isLightTheme ? TC(180, 255, 95, 85)   : TC(180, 255, 95, 85); }
    Color BtnDefFg()    { return isLightTheme ? TC(200, 60, 60, 70)   : TC(160, 255, 255, 255); }
    Color BtnEnterFg()  { return isLightTheme ? TC(240, 40, 40, 45)   : TC(240, 245, 245, 247); }
    Color DragDot()     { return isLightTheme ? TC(200, 120, 120, 130) : TC(160, 200, 200, 210); }
    Color ExpandFg()    { return isLightTheme ? TC(160, 80, 80, 90)   : TC(80, 180, 180, 190); }
    Color ExpandFgH()   { return isLightTheme ? TC(220, 60, 60, 70)   : TC(140, 180, 180, 190); }
    Color TipTxt()      { return isLightTheme ? TC(180, 100, 100, 110) : TC(100, 140, 140, 150); }
    Color LabelTxt()    { return isLightTheme ? TC(220, 60, 60, 70)   : TC(140, 180, 180, 190); }

    ComboBox CreateStyledComboBox(double width, double height, Thickness margin) {
        var cb = new ComboBox {
            Width = width, Height = height,
            Margin = margin,
            Background = TB(CbBgC()),
            Foreground = TB(CbFgC()),
            BorderBrush = new SolidColorBrush(Color.FromArgb(100, 160, 160, 170))
        };
        var cbBg = TB(CbBgC());
        var cbFg = TB(CbFgC());
        cb.Resources[SystemColors.WindowBrushKey] = cbBg;
        cb.Resources[SystemColors.WindowTextBrushKey] = cbFg;
        cb.Resources[SystemColors.ControlBrushKey] = cbBg;
        cb.Resources[SystemColors.ControlTextBrushKey] = cbFg;
        cb.Resources[SystemColors.HighlightBrushKey] = GetAccentBrush(200);
        cb.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;

        var style = new Style(typeof(ComboBoxItem));
        string accent = GetAccentHex();
        string templateXaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ComboBoxItem'>" +
            "<Border Name='bd' Background='{TemplateBinding Background}' Padding='6,4'>" +
            "<ContentPresenter TextElement.Foreground='{TemplateBinding Foreground}' VerticalAlignment='Center'/>" +
            "</Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsHighlighted' Value='True'>" +
            "<Setter TargetName='bd' Property='Background' Value='" + accent + "'/>" +
            "<Setter Property='Foreground' Value='White'/>" +
            "</Trigger>" +
            "<Trigger Property='IsSelected' Value='True'>" +
            "<Setter TargetName='bd' Property='Background' Value='" + accent + "'/>" +
            "<Setter Property='Foreground' Value='White'/>" +
            "</Trigger>" +
            "</ControlTemplate.Triggers>" +
            "</ControlTemplate>";
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(templateXaml)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, TB(CbBgC())));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TB(CbFgC())));
        cb.ItemContainerStyle = style;

        cb.Loaded += (s, e) => {
            foreach (var tb in FindVisualChildren<ToggleButton>(cb)) {
                tb.Background = cbBg;
                tb.Foreground = cbFg;
            }
            foreach (var txt in FindVisualChildren<TextBlock>(cb)) {
                txt.Foreground = cbFg;
            }
        };

        return cb;
    }

    void TogglePassthrough() {
        isPassthrough = !isPassthrough;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (isPassthrough) {
            style |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            btnPass.Background = new SolidColorBrush(Color.FromArgb(140, 80, 160, 255));
            btnPass.Foreground = TB(TextPri());
            btnPass.Tag = true;
            Opacity = 0.55;
        } else {
            style &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
            btnPass.Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
            btnPass.Foreground = TB(BtnDefFg());
            btnPass.Tag = false;
            Opacity = 1.0;
        }
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    void ToggleGhostMode() {
        isGhostMode = !isGhostMode;
        if (isGhostMode) {
            btnGhost.Background = new SolidColorBrush(Color.FromArgb(140, 180, 180, 190));
            btnGhost.Foreground = TB(TextPri());
            btnGhost.Tag = true;
        } else {
            btnGhost.Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
            btnGhost.Foreground = TB(BtnDefFg());
            btnGhost.Tag = false;
            EnableAcrylic();
            Animate(this, Window.OpacityProperty, Opacity, 1.0, 200);
        }
    }

    void ToggleHistoryView() {
        isHistoryView = !isHistoryView;
        if (isHistoryView) {
            btnHistory.Background = new SolidColorBrush(Color.FromArgb(140, 120, 120, 128));
            btnHistory.Foreground = TB(TextPri());
            btnHistory.Tag = true;
            titleLabel.Text = "历史事项";
        } else {
            btnHistory.Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
            btnHistory.Foreground = TB(BtnDefFg());
            btnHistory.Tag = false;
            titleLabel.Text = "待办事项";
        }
        Render();
    }

    void CheckAndSnap() {
        if (isDraggingWindow || isResizing || isDraggingItem) return;
        var screen = SystemParameters.WorkArea;
        double threshold = 30;
        normalLeft = Left;
        normalTop = Top;
        if (Left < threshold) {
            currentSnap = SnapEdge.Left;
            HideToSnap();
        } else if (Left + ActualWidth > screen.Width - threshold) {
            currentSnap = SnapEdge.Right;
            HideToSnap();
        } else if (Top < threshold) {
            currentSnap = SnapEdge.Top;
            HideToSnap();
        } else if (Top + ActualHeight > screen.Height - threshold) {
            currentSnap = SnapEdge.Bottom;
            HideToSnap();
        } else {
            currentSnap = SnapEdge.None;
            isExpanded = true;
        }
    }

    void HideToSnap() {
        if (currentSnap == SnapEdge.None) return;
        var screen = SystemParameters.WorkArea;
        isExpanded = false;
        switch (currentSnap) {
            case SnapEdge.Left:
                Animate(this, Window.LeftProperty, Left, -ActualWidth + 5, 250);
                break;
            case SnapEdge.Right:
                Animate(this, Window.LeftProperty, Left, screen.Width - 5, 250);
                break;
            case SnapEdge.Top:
                Animate(this, Window.TopProperty, Top, -ActualHeight + 5, 250);
                break;
            case SnapEdge.Bottom:
                Animate(this, Window.TopProperty, Top, screen.Height - 5, 250);
                break;
        }
    }

    void ExpandFromSnap() {
        if (currentSnap == SnapEdge.None) return;
        isExpanded = true;
        switch (currentSnap) {
            case SnapEdge.Left:
            case SnapEdge.Right:
                Animate(this, Window.LeftProperty, Left, normalLeft, 200);
                break;
            case SnapEdge.Top:
            case SnapEdge.Bottom:
                Animate(this, Window.TopProperty, Top, normalTop, 200);
                break;
        }
    }

    void Animate(DependencyObject target, DependencyProperty prop, double from, double to, int ms, EasingType easing = EasingType.EaseOutQuint, Action onComplete = null) {
        activeAnimations.Add(new ActiveAnimation {
            Target = target, Property = prop, From = from, To = to, DurationMs = ms, Sw = Stopwatch.StartNew(), Easing = easing, OnComplete = onComplete
        });
        if (activeAnimations.Count == 1)
            CompositionTarget.Rendering += OnAnimationFrame;
    }

    double ApplyEasing(double t, EasingType easing) {
        switch (easing) {
            case EasingType.EaseOutCubic:
                return 1 - Math.Pow(1 - t, 3);
            case EasingType.Spring:
                if (t == 0) return 0;
                if (t >= 1) return 1;
                double c4 = (2 * Math.PI) / 3;
                return (double)(Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * c4) + 1);
            case EasingType.EaseOutQuint:
            default:
                return 1 - Math.Pow(1 - t, 5);
        }
    }

    void OnAnimationFrame(object sender, EventArgs e) {
        var completed = new List<Action>();
        for (int i = activeAnimations.Count - 1; i >= 0; i--) {
            var anim = activeAnimations[i];
            double t = Math.Min(1, anim.Sw.Elapsed.TotalMilliseconds / anim.DurationMs);
            double ease = ApplyEasing(t, anim.Easing);
            double value = anim.From + (anim.To - anim.From) * ease;
            if (anim.Easing == EasingType.Spring) {
                if ((anim.Property == Window.LeftProperty || anim.Property == Window.TopProperty) && (value < -5000 || value > 10000))
                    value = anim.To;
            }
            anim.Target.SetValue(anim.Property, value);
            if (t >= 1) {
                anim.Sw.Stop();
                if (anim.OnComplete != null) completed.Add(anim.OnComplete);
                activeAnimations.RemoveAt(i);
            }
        }
        foreach (var cb in completed) cb();
        if (activeAnimations.Count == 0)
            CompositionTarget.Rendering -= OnAnimationFrame;
    }

    void StartMouseCheck() {
        mouseCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        mouseCheckTimer.Tick += (s, e) => CheckMouse();
        mouseCheckTimer.Start();
    }

    void CheckMouse() {
        POINT pt;
        GetCursorPos(out pt);
        var screen = SystemParameters.WorkArea;

        // Passthrough recovery
        if (isPassthrough) {
            bool inTitleBar = pt.X >= Left && pt.X <= Left + ActualWidth
                           && pt.Y >= Top && pt.Y <= Top + 45;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            bool currentlyTransparent = (style & WS_EX_TRANSPARENT) != 0;
            if (inTitleBar && currentlyTransparent) {
                style &= ~WS_EX_TRANSPARENT;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
            } else if (!inTitleBar && !currentlyTransparent) {
                style |= WS_EX_TRANSPARENT;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
            }
        }

        // Ghost mode fade
        if (isGhostMode && currentSnap == SnapEdge.None) {
            bool mouseInWindow = pt.X >= Left && pt.X <= Left + ActualWidth && pt.Y >= Top && pt.Y <= Top + ActualHeight;
            if (mouseInWindow) {
                if (ghostFadeTimer != null) ghostFadeTimer.Stop();
                if (Opacity < 1.0) {
                    EnableAcrylic();
                    Animate(this, Window.OpacityProperty, Opacity, 1.0, 180, EasingType.EaseOutQuint);
                }
            } else {
                if (ghostFadeTimer == null) {
                    ghostFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    ghostFadeTimer.Tick += (s2, e2) => {
                        ghostFadeTimer.Stop();
                        if (isGhostMode && currentSnap == SnapEdge.None && Opacity > 0.12) {
                            DisableAcrylic();
                            Animate(this, Window.OpacityProperty, Opacity, 0.12, 400, EasingType.EaseOutQuint);
                        }
                    };
                }
                if (!ghostFadeTimer.IsEnabled) ghostFadeTimer.Start();
            }
        }

        if (currentSnap == SnapEdge.None) return;

        bool inTrigger = false;
        bool inWindow = false;

        if (isExpanded) {
            inWindow = pt.X >= Left && pt.X <= Left + ActualWidth && pt.Y >= Top && pt.Y <= Top + ActualHeight;
        } else {
            switch (currentSnap) {
                case SnapEdge.Left: inWindow = pt.X <= 8 && pt.Y >= Top && pt.Y <= Top + ActualHeight; break;
                case SnapEdge.Right: inWindow = pt.X >= screen.Width - 8 && pt.Y >= Top && pt.Y <= Top + ActualHeight; break;
                case SnapEdge.Top: inWindow = pt.Y <= 8 && pt.X >= Left && pt.X <= Left + ActualWidth; break;
                case SnapEdge.Bottom: inWindow = pt.Y >= screen.Height - 8 && pt.X >= Left && pt.X <= Left + ActualWidth; break;
            }
        }

        switch (currentSnap) {
            case SnapEdge.Left: inTrigger = pt.X <= 8 && pt.Y >= Top && pt.Y <= Top + ActualHeight; break;
            case SnapEdge.Right: inTrigger = pt.X >= screen.Width - 8 && pt.Y >= Top && pt.Y <= Top + ActualHeight; break;
            case SnapEdge.Top: inTrigger = pt.Y <= 8 && pt.X >= Left && pt.X <= Left + ActualWidth; break;
            case SnapEdge.Bottom: inTrigger = pt.Y >= screen.Height - 8 && pt.X >= Left && pt.X <= Left + ActualWidth; break;
        }

        if (!isExpanded && inTrigger) {
            ExpandFromSnap();
            if (hideDelayTimer != null) hideDelayTimer.Stop();
        } else if (isExpanded && !inWindow) {
            if (hideDelayTimer == null) {
                hideDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                hideDelayTimer.Tick += (s2, e2) => { hideDelayTimer.Stop(); HideToSnap(); };
            }
            if (!hideDelayTimer.IsEnabled) hideDelayTimer.Start();
        } else if (isExpanded && inWindow) {
            if (hideDelayTimer != null) hideDelayTimer.Stop();
        }
    }

    void StartReminderCheck() {
        reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        reminderTimer.Tick += (s, e) => CheckReminders();
        reminderTimer.Start();
        CheckReminders();
    }

    void CheckReminders() {
        var now = DateTime.Now;
        foreach (var todo in todos) {
            if (todo.Completed) continue;
            if (!todo.DueTime.HasValue) continue;
            if (todo.DueTime.Value > now) continue;
            if (notifiedTasks.Contains(todo.Id)) continue;
            notifiedTasks.Add(todo.Id);
            ShowReminder(todo.Text, "任务截止时间已到");
        }
    }

    void ShowReminder(string title, string message) {
        var popup = new Window {
            Width = 300,
            Height = 90,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            Left = SystemParameters.WorkArea.Width - 320,
            Top = SystemParameters.WorkArea.Height - 110,
            Owner = this
        };
        var shadow = new System.Windows.Media.Effects.DropShadowEffect {
            BlurRadius = 20, ShadowDepth = 6, Direction = 270,
            Color = Color.FromArgb(120, 0, 0, 0), Opacity = 0.35
        };
        var root = new Border {
            CornerRadius = new CornerRadius(16),
            Background = TB(PopupBgC()),
            BorderBrush = TB(isLightTheme ? TC(60, 0, 0, 0) : TC(35, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = shadow,
            Margin = new Thickness(8),
            Opacity = 0
        };
        var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        var titleTb = new TextBlock {
            Text = title,
            Foreground = TB(PopTextPri()),
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var msgTb = new TextBlock {
            Text = message,
            Foreground = TB(PopTextSec()),
            FontSize = 12, Margin = new Thickness(0, 4, 0, 0)
        };
        sp.Children.Add(titleTb);
        sp.Children.Add(msgTb);
        root.Child = sp;
        popup.Content = root;
        popup.MouseLeftButtonDown += (s, e) => popup.Close();
        popup.Show();
        var transform = new TranslateTransform(60, 0);
        root.RenderTransform = transform;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(350)) {
            EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        transform.BeginAnimation(TranslateTransform.XProperty, anim);
        var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
        root.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (s, e) => { t.Stop(); try { popup.Close(); } catch { } };
        t.Start();
    }

    void OnWindowMouseMove(object sender, MouseEventArgs e) {
        if (!isDraggingItem || dragGhost == null) return;
        var pos = e.GetPosition(dragOverlay);
        Canvas.SetLeft(dragGhost, pos.X - itemDragOffset.X);
        Canvas.SetTop(dragGhost, pos.Y - itemDragOffset.Y);

        int newIndex = CalculateInsertIndex(pos);
        if (newIndex != dragSourceIndex && newIndex != dragSourceIndex + 1) {
            var item = todos[dragSourceIndex];
            todos.RemoveAt(dragSourceIndex);
            if (newIndex > dragSourceIndex) newIndex--;
            todos.Insert(newIndex, item);
            dragSourceIndex = newIndex;
            Render();
            if (dragSourceIndex >= 0 && dragSourceIndex < taskPanel.Children.Count) {
                var border = taskPanel.Children[dragSourceIndex] as Border;
                if (border != null) border.Opacity = 0.35;
            }
            dragOverlay.Visibility = Visibility.Visible;
            if (dragGhost.Parent == null) dragOverlay.Children.Add(dragGhost);
        }
    }

    void OnWindowMouseUp(object sender, MouseButtonEventArgs e) {
        if (!isDraggingItem || dragGhost == null) return;
        isDraggingItem = false;
        Mouse.Capture(null);
        dragOverlay.Children.Clear();
        dragOverlay.Visibility = Visibility.Collapsed;
        dragGhost = null;
        dragSourceIndex = -1;
        Save();
        Render();
    }

    int CalculateInsertIndex(Point posInOverlay) {
        var posInPanel = dragOverlay.TranslatePoint(posInOverlay, taskPanel);
        for (int i = 0; i < taskPanel.Children.Count; i++) {
            var child = taskPanel.Children[i] as FrameworkElement;
            if (child == null) continue;
            var childTop = child.TranslatePoint(new Point(0, 0), taskPanel).Y;
            var childMid = childTop + child.ActualHeight / 2;
            if (posInPanel.Y < childMid) return i;
        }
        return taskPanel.Children.Count;
    }

    void AddTodo() {
        var t = inputBox.Text.Trim();
        if (string.IsNullOrEmpty(t)) return;
        todos.Insert(0, new TodoItem { Text = t, CreatedAt = DateTime.Now });
        inputBox.Text = "";
        Save(); Render();
    }

    void Save() {
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dataPath));
            using (var fs = File.Create(dataPath))
                new DataContractJsonSerializer(typeof(List<TodoItem>)).WriteObject(fs, todos);
        } catch { }
    }

    void Load() {
        try {
            if (!File.Exists(dataPath)) return;
            using (var fs = File.OpenRead(dataPath)) {
                var loaded = (List<TodoItem>)new DataContractJsonSerializer(typeof(List<TodoItem>)).ReadObject(fs);
                foreach (var t in loaded) {
                    if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N");
                    if (t.SubTasks == null) t.SubTasks = new List<SubTask>();
                    todos.Add(t);
                }
            }
        } catch { }
    }

    void LoadSettings() {
        try {
            if (File.Exists(settingsPath)) {
                using (var fs = File.OpenRead(settingsPath)) {
                    var s = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(fs);
                    if (!string.IsNullOrEmpty(s.AccentColor))
                        accentColor = ParseColor(s.AccentColor);
                    backgroundImagePath = s.BackgroundImage;
                    if (!string.IsNullOrEmpty(s.BackgroundStretch))
                        backgroundStretch = s.BackgroundStretch;
                    if (!string.IsNullOrEmpty(s.CropRatio))
                        cropRatio = s.CropRatio;
                    isLightTheme = s.IsLightTheme;
                }
            }
        } catch { }
    }

    void SaveSettings() {
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath));
            var s = new AppSettings {
                AccentColor = accentColor.ToString(),
                BackgroundImage = backgroundImagePath,
                BackgroundStretch = backgroundStretch,
                CropRatio = cropRatio,
                IsLightTheme = isLightTheme
            };
            using (var fs = File.Create(settingsPath))
                new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(fs, s);
        } catch { }
    }

    void ApplyBackgroundImage() {
        if (bgImage == null) return;
        if (!string.IsNullOrEmpty(backgroundImagePath) && File.Exists(backgroundImagePath)) {
            try {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(backgroundImagePath, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bgImage.Source = bmp;
                bgImage.Stretch = (Stretch)Enum.Parse(typeof(Stretch), backgroundStretch);
                bgImage.Visibility = Visibility.Visible;
                bgOverlay.Visibility = Visibility.Visible;
            } catch {
                bgImage.Visibility = Visibility.Collapsed;
                bgOverlay.Visibility = Visibility.Collapsed;
            }
        } else {
            bgImage.Visibility = Visibility.Collapsed;
            bgOverlay.Visibility = Visibility.Collapsed;
        }
    }

    void Render() {
        taskPanel.Children.Clear();
        if (isHistoryView) {
            RenderHistory();
            return;
        }
        string accentHex = GetAccentHex();
        for (int i = 0; i < todos.Count; i++) {
            if (todos[i].Completed) continue;
            var todo = todos[i];
            var idx = i;

            var itemBorder = new Border {
                CornerRadius = new CornerRadius(12),
                Background = GetPriorityBrush(todo.Priority),
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand
            };

            itemBorder.MouseEnter += (s, e) => {
                itemBorder.Background = GetPriorityBrushHover(todo.Priority);
            };
            itemBorder.MouseLeave += (s, e) => {
                itemBorder.Background = GetPriorityBrush(todo.Priority);
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Margin = new Thickness(12, 9, 12, 9);

            var drag = new StackPanel {
                Orientation = Orientation.Vertical, Width = 12,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0.2,
                Cursor = Cursors.SizeAll
            };
            for (int k = 0; k < 3; k++)
                drag.Children.Add(new Rectangle { Height = 1.5, Fill = TB(DragDot()), Margin = new Thickness(0, 1.2, 0, 1.2), RadiusX = 0.75, RadiusY = 0.75 });
            drag.MouseLeftButtonDown += (s, e) => {
                e.Handled = true;
                isDraggingItem = true;
                dragSourceIndex = idx;
                itemDragOffset = e.GetPosition(itemBorder);

                dragGhost = new Border {
                    CornerRadius = new CornerRadius(8),
                    Background = GetAccentBrush(120),
                    BorderBrush = GetAccentBrush(180),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 8, 12, 8),
                    Width = Math.Max(itemBorder.ActualWidth, 260),
                    Child = new TextBlock {
                        Text = todo.Text,
                        Foreground = Brushes.White,
                        FontSize = 13,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                var pos = e.GetPosition(dragOverlay);
                Canvas.SetLeft(dragGhost, pos.X - itemDragOffset.X);
                Canvas.SetTop(dragGhost, pos.Y - itemDragOffset.Y);
                dragOverlay.Children.Add(dragGhost);
                dragOverlay.Visibility = Visibility.Visible;
                itemBorder.Opacity = 0.35;
                Mouse.Capture(this);
            };
            Grid.SetColumn(drag, 0);
            grid.Children.Add(drag);

            var prioBtn = new Button {
                Width = 8, Height = 8,
                Background = GetPriorityBtnBrush(todo.Priority),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Template = CreateRoundedTemplate(4)
            };
            prioBtn.Click += (s, e) => {
                todo.Priority = (todo.Priority + 1) % 3;
                Save(); Render();
            };
            Grid.SetColumn(prioBtn, 1);
            grid.Children.Add(prioBtn);

            var cb = new CheckBox {
                IsChecked = todo.Completed,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            string cbXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>" +
                "<Border Width='16' Height='16' CornerRadius='4' Background='{TemplateBinding Background}' " +
                "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' x:Name='border'>" +
                "<Path x:Name='checkMark' Width='9' Height='9' Stretch='Uniform' Stroke='White' " +
                "StrokeThickness='1.8' Data='M 1.5 6 L 4.5 9 L 10.5 2' Visibility='Collapsed' " +
                "HorizontalAlignment='Center' VerticalAlignment='Center' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsChecked' Value='True'>" +
                "<Setter TargetName='checkMark' Property='Visibility' Value='Visible'/>" +
                "<Setter TargetName='border' Property='Background' Value='" + accentHex + "'/>" +
                "<Setter TargetName='border' Property='BorderBrush' Value='" + accentHex + "'/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>";
            cb.Template = (ControlTemplate)XamlReader.Parse(cbXaml);
            cb.Background = Brushes.Transparent;
            cb.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(isLightTheme ? 120 : 100), 160, 160, 170));
            cb.Checked += (s, e) => {
                todo.Completed = true;
                todo.CompletedAt = DateTime.Now;
                var transform = new TranslateTransform();
                itemBorder.RenderTransform = transform;
                Animate(itemBorder, UIElement.OpacityProperty, 1, 0, 280, EasingType.EaseOutQuint);
                Animate(transform, TranslateTransform.XProperty, 0, -itemBorder.ActualWidth - 20, 280, EasingType.EaseOutQuint, () => { Save(); Render(); });
            };
            cb.Unchecked += (s, e) => { todo.Completed = false; todo.CompletedAt = null; Save(); Render(); };
            Grid.SetColumn(cb, 2);
            grid.Children.Add(cb);

            bool isOverdue = todo.DueTime.HasValue && todo.DueTime.Value < DateTime.Now;
            var txt = new TextBlock {
                Text = todo.Text + (isOverdue ? "  ·" : ""),
                Foreground = isOverdue ? TB(OverdueC()) : TB(TextPri()),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0), FontSize = 14
            };
            Grid.SetColumn(txt, 3);
            grid.Children.Add(txt);

            itemBorder.MouseLeftButtonDown += (s, e) => {
                if (e.ClickCount == 2) {
                    e.Handled = true;
                    OpenTaskDetail(todo);
                    return;
                }
                if (e.OriginalSource == txt || e.OriginalSource == grid || e.OriginalSource == itemBorder) {
                    var edit = new TextBox {
                        Text = todo.Text,
                        Foreground = TB(TextPri()),
                        Background = TB(EditBgC()),
                        BorderBrush = GetAccentBrush(160),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(6, 3, 6, 3),
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 14,
                        CaretBrush = TB(TextPri())
                    };
                    Grid.SetColumn(edit, 3);
                    grid.Children.Remove(txt);
                    grid.Children.Add(edit);
                    edit.Focus();
                    edit.SelectAll();
                    edit.LostFocus += (s2, e2) => FinishEdit(grid, edit, txt, todo, itemBorder);
                    edit.KeyDown += (s2, e2) => { if (e2.Key == Key.Enter) FinishEdit(grid, edit, txt, todo, itemBorder); };
                }
            };

            var del = new Button {
                Content = "×",
                Width = 18, Height = 18,
                Background = Brushes.Transparent,
                Foreground = TB(HistDelFg()),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 0),
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Template = CreateRoundedTemplate(9),
                Opacity = 0
            };
            del.MouseEnter += (s, e) => del.Foreground = TB(DelEnter());
            del.MouseLeave += (s, e) => del.Foreground = TB(HistDelFg());
            del.Click += (s, e) => { todos.RemoveAt(idx); Save(); Render(); };
            Grid.SetColumn(del, 5);
            grid.Children.Add(del);

            itemBorder.MouseEnter += (s, e) => del.Opacity = 1;
            itemBorder.MouseLeave += (s, e) => del.Opacity = 0;

            Button expandBtn = null;
            var subPanel = new StackPanel {
                Visibility = Visibility.Visible,
                Margin = new Thickness(28, 2, 12, 8)
            };
            if (todo.SubTasks.Count > 0) {
                expandBtn = new Button {
                    Content = "▼",
                    Width = 18, Height = 18,
                    Background = Brushes.Transparent,
                    Foreground = TB(ExpandFg()),
                    BorderThickness = new Thickness(0),
                    FontSize = 9,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Template = CreateRoundedTemplate(9)
                };
                expandBtn.Click += (s, e) => {
                    bool expanding = subPanel.Visibility != Visibility.Visible;
                    subPanel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                    expandBtn.Content = expanding ? "▼" : "▶";
                    expandBtn.Foreground = expanding ? TB(ExpandFgH()) : TB(ExpandFg());
                };
                Grid.SetColumn(expandBtn, 4);
                grid.Children.Add(expandBtn);

                foreach (var sub in todo.SubTasks) {
                    var subRow = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var subCb = new CheckBox {
                        IsChecked = sub.Completed,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    string subCbXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>" +
                        "<Border Width='14' Height='14' CornerRadius='3' Background='{TemplateBinding Background}' " +
                        "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' x:Name='border'>" +
                        "<Path x:Name='checkMark' Width='8' Height='8' Stretch='Uniform' Stroke='White' " +
                        "StrokeThickness='1.5' Data='M 1.5 5 L 4 7.5 L 9.5 2' Visibility='Collapsed' " +
                        "HorizontalAlignment='Center' VerticalAlignment='Center' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>" +
                        "</Border>" +
                        "<ControlTemplate.Triggers>" +
                        "<Trigger Property='IsChecked' Value='True'>" +
                        "<Setter TargetName='checkMark' Property='Visibility' Value='Visible'/>" +
                        "<Setter TargetName='border' Property='Background' Value='" + accentHex + "'/>" +
                        "<Setter TargetName='border' Property='BorderBrush' Value='" + accentHex + "'/>" +
                        "</Trigger>" +
                        "</ControlTemplate.Triggers>" +
                        "</ControlTemplate>";
                    subCb.Template = (ControlTemplate)XamlReader.Parse(subCbXaml);
                    subCb.Background = Brushes.Transparent;
                    subCb.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(isLightTheme ? 120 : 80), 160, 160, 170));
                    subCb.Checked += (s2, e2) => { sub.Completed = true; Save(); };
                    subCb.Unchecked += (s2, e2) => { sub.Completed = false; Save(); };
                    Grid.SetColumn(subCb, 0);
                    subRow.Children.Add(subCb);

                    var subTxt = new TextBlock {
                        Text = sub.Text,
                        Foreground = sub.Completed ? TB(SubDoneTxt()) : TB(SubTxt()),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextDecorations = sub.Completed ? TextDecorations.Strikethrough : null,
                        FontSize = 12
                    };
                    Grid.SetColumn(subTxt, 1);
                    subRow.Children.Add(subTxt);

                    subPanel.Children.Add(subRow);
                }
            }

            var itemStack = new StackPanel();
            itemStack.Children.Add(grid);
            if (todo.SubTasks.Count > 0) {
                var sep = new Rectangle {
                    Height = 1,
                    Fill = TB(SepC()),
                    Margin = new Thickness(28, 2, 12, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Visibility = Visibility.Visible
                };
                if (expandBtn != null) {
                    expandBtn.Click += (s, e) => {
                        bool expanding = subPanel.Visibility != Visibility.Visible;
                        sep.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
                    };
                }
                itemStack.Children.Add(sep);
                itemStack.Children.Add(subPanel);
            }

            var itemRoot = new Grid();
            itemRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            itemRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var accentBar = new Border {
                Background = GetPriorityAccentBrush(todo.Priority),
                CornerRadius = new CornerRadius(6, 0, 0, 6),
                Margin = new Thickness(0, 6, 0, 6)
            };
            Grid.SetColumn(accentBar, 0);
            itemRoot.Children.Add(accentBar);
            Grid.SetColumn(itemStack, 1);
            itemRoot.Children.Add(itemStack);
            itemBorder.Child = itemRoot;
            taskPanel.Children.Add(itemBorder);
        }
        if (taskPanel.Children.Count == 0) {
            var emptyPanel = new StackPanel {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0)
            };
            var emptyCircle = new Border {
                Width = 56, Height = 56,
                CornerRadius = new CornerRadius(28),
                Background = TB(EmptyCirc()),
                BorderBrush = TB(EmptyCirc()),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var emptyDot = new Border {
                Width = 8, Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = TB(EmptyDot()),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            emptyCircle.Child = emptyDot;
            emptyPanel.Children.Add(emptyCircle);
            emptyPanel.Children.Add(new TextBlock {
                Text = "暂无待办事项",
                Foreground = TB(EmptyTxt()),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0)
            });
            taskPanel.Children.Add(emptyPanel);
        }
        if (isLightTheme) {
            var darkBrush = new SolidColorBrush(Color.FromArgb(200, 40, 40, 45));
            var darkBorder = new SolidColorBrush(Color.FromArgb(120, 40, 40, 45));
            btnAdd.Background = darkBrush;
            rootBorder.BorderBrush = darkBorder;
            inputWrap.BorderBrush = darkBorder;
            inputBox.CaretBrush = darkBrush;
        } else {
            var lightBrush = new SolidColorBrush(Color.FromArgb(200, 200, 200, 210));
            var lightBorder = new SolidColorBrush(Color.FromArgb(140, 200, 200, 210));
            btnAdd.Background = lightBrush;
            rootBorder.BorderBrush = lightBorder;
            inputWrap.BorderBrush = lightBorder;
            inputBox.CaretBrush = lightBrush;
        }
        UpdateDynamicHeights();
    }

    void RenderHistory() {
        bool hasAny = false;
        for (int i = 0; i < todos.Count; i++) {
            if (!todos[i].Completed) continue;
            hasAny = true;
            var todo = todos[i];
            var idx = i;

            var itemBorder = new Border {
                CornerRadius = new CornerRadius(12),
                Background = TB(HistItemBg()),
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand
            };
            itemBorder.MouseEnter += (s, e) => itemBorder.Background = TB(HistItemHov());
            itemBorder.MouseLeave += (s, e) => itemBorder.Background = TB(HistItemBg());

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Margin = new Thickness(12, 10, 12, 10);

            var doneDot = new Border {
                Width = 6, Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(120, 100, 210, 100)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(doneDot, 0);
            grid.Children.Add(doneDot);

            var txt = new TextBlock {
                Text = todo.Text,
                Foreground = TB(HistTxt()),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextDecorations = TextDecorations.Strikethrough,
                FontSize = 14
            };
            Grid.SetColumn(txt, 1);
            grid.Children.Add(txt);

            string doneStr = todo.CompletedAt.HasValue ? todo.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm") : "";
            var dateTxt = new TextBlock {
                Text = doneStr,
                Foreground = TB(HistDate()),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(dateTxt, 2);
            grid.Children.Add(dateTxt);

            var del = new Button {
                Content = "×",
                Width = 18, Height = 18,
                Background = Brushes.Transparent,
                Foreground = TB(HistDelFg()),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Template = CreateRoundedTemplate(9),
                Opacity = 0
            };
            del.MouseEnter += (s, e) => del.Foreground = TB(DelEnter());
            del.MouseLeave += (s, e) => del.Foreground = TB(HistDelFg());
            del.Click += (s, e) => { todos.RemoveAt(idx); Save(); Render(); };
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);

            itemBorder.MouseEnter += (s, e) => del.Opacity = 1;
            itemBorder.MouseLeave += (s, e) => del.Opacity = 0;

            itemBorder.Child = grid;
            taskPanel.Children.Add(itemBorder);
        }

        if (!hasAny) {
            var empty = new TextBlock {
                Text = "暂无已完成事项",
                Foreground = TB(EmptyTxt()),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 0),
                FontSize = 14
            };
            taskPanel.Children.Add(empty);
        }
    }

    void UpdateDynamicHeights() {
        int count = taskPanel.Children.Count;
        if (count == 0) return;
        double marginTotal = count * 8;
        double available = scrollViewer.ActualHeight - marginTotal;
        double h = available / count;
        const double MIN_H = 52;
        const double MAX_H = 120;

        if (h < MIN_H) {
            foreach (var child in taskPanel.Children) {
                var border = child as Border;
                if (border != null && !double.IsNaN(border.Height)) {
                    border.ClearValue(FrameworkElement.HeightProperty);
                }
            }
            return;
        }

        double targetH = Math.Min(MAX_H, h);
        foreach (var child in taskPanel.Children) {
            var border = child as Border;
            if (border != null) {
                double current = border.Height;
                if (double.IsNaN(current)) current = border.ActualHeight;
                if (Math.Abs(current - targetH) > 1) {
                    Animate(border, FrameworkElement.HeightProperty, current, targetH, 300, EasingType.Spring);
                }
            }
        }
    }

    void OpenTaskDetail(TodoItem todo) {
        detailOverlay.Children.Clear();
        detailOverlay.Visibility = Visibility.Visible;
        string accentHex = GetAccentHex();

        var card = new Border {
            CornerRadius = new CornerRadius(20),
            Background = TB(CardBgC()),
            BorderBrush = isLightTheme ? new SolidColorBrush(Color.FromArgb(120, 40, 40, 45)) : new SolidColorBrush(Color.FromArgb(140, 200, 200, 210)),
            BorderThickness = new Thickness(1),
            Width = 280,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16)
        };
        var cardShadow = new System.Windows.Media.Effects.DropShadowEffect {
            BlurRadius = 32, ShadowDepth = 12, Direction = 270,
            Color = isLightTheme ? Color.FromArgb(120, 0, 0, 0) : Color.FromArgb(160, 0, 0, 0), Opacity = isLightTheme ? 0.25 : 0.4
        };
        card.Effect = cardShadow;

        var sp = new StackPanel { Margin = new Thickness(20) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerTitle = new TextBlock {
            Text = "任务详情",
            Foreground = TB(TextPri()),
            FontSize = 16, FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(headerTitle, 0);
        header.Children.Add(headerTitle);
        var btnCloseDetail = CreateTrafficLightBtn("×", Brushes.Transparent, new SolidColorBrush(Color.FromArgb(140, 255, 95, 85)));
        btnCloseDetail.Click += (s, e) => { detailOverlay.Visibility = Visibility.Collapsed; detailOverlay.Children.Clear(); };
        Grid.SetColumn(btnCloseDetail, 1);
        header.Children.Add(btnCloseDetail);
        sp.Children.Add(header);

        sp.Children.Add(new TextBlock {
            Text = "任务名称",
            Foreground = TB(LabelTxt()),
            FontSize = 11, Margin = new Thickness(0, 16, 0, 6)
        });
        var nameEdit = new TextBox {
            Text = todo.Text,
            Background = TB(EditBgC()),
            Foreground = TB(TextPri()),
            BorderBrush = TB(isLightTheme ? TC(60, 0, 0, 0) : TC(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            FontSize = 14,
            CaretBrush = TB(TextPri())
        };
        sp.Children.Add(nameEdit);

        sp.Children.Add(new TextBlock {
            Text = "创建时间",
            Foreground = TB(LabelTxt()),
            FontSize = 11, Margin = new Thickness(0, 12, 0, 6)
        });
        sp.Children.Add(new TextBlock {
            Text = todo.CreatedAt == DateTime.MinValue ? "未知" : todo.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            Foreground = TB(TextSec()),
            FontSize = 13
        });

        sp.Children.Add(new TextBlock {
            Text = "截止时间",
            Foreground = TB(LabelTxt()),
            FontSize = 11, Margin = new Thickness(0, 12, 0, 6)
        });
        var dueGrid = new Grid();
        dueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var yearBox = CreateStyledComboBox(50, 26, new Thickness(0));
        for (int y = 2024; y <= 2034; y++) yearBox.Items.Add(y.ToString());
        yearBox.SelectedItem = todo.DueTime.HasValue ? todo.DueTime.Value.Year.ToString() : DateTime.Now.Year.ToString();

        var monthBox = CreateStyledComboBox(40, 26, new Thickness(3, 0, 0, 0));
        for (int m = 1; m <= 12; m++) monthBox.Items.Add(m.ToString("D2"));
        monthBox.SelectedItem = todo.DueTime.HasValue ? todo.DueTime.Value.Month.ToString("D2") : DateTime.Now.Month.ToString("D2");

        var dayBox = CreateStyledComboBox(40, 26, new Thickness(3, 0, 0, 0));
        for (int d = 1; d <= 31; d++) dayBox.Items.Add(d.ToString("D2"));
        dayBox.SelectedItem = todo.DueTime.HasValue ? todo.DueTime.Value.Day.ToString("D2") : DateTime.Now.Day.ToString("D2");

        var hourBox = CreateStyledComboBox(40, 26, new Thickness(3, 0, 0, 0));
        for (int h = 0; h < 24; h++) hourBox.Items.Add(h.ToString("D2"));
        hourBox.SelectedItem = todo.DueTime.HasValue ? todo.DueTime.Value.Hour.ToString("D2") : "23";

        var minuteBox = CreateStyledComboBox(40, 26, new Thickness(3, 0, 0, 0));
        for (int m = 0; m < 60; m++) minuteBox.Items.Add(m.ToString("D2"));
        minuteBox.SelectedItem = todo.DueTime.HasValue ? todo.DueTime.Value.Minute.ToString("D2") : "59";

        var btnClearDue = CreateBtn("清除", 40, 26, 6);
        btnClearDue.FontSize = 11;
        btnClearDue.Margin = new Thickness(6, 0, 0, 0);
        btnClearDue.Click += (s, e) => {
            yearBox.SelectedItem = null;
            monthBox.SelectedItem = null;
            dayBox.SelectedItem = null;
            hourBox.SelectedItem = "23";
            minuteBox.SelectedItem = "59";
        };

        Grid.SetColumn(yearBox, 0);
        Grid.SetColumn(monthBox, 1);
        Grid.SetColumn(dayBox, 2);
        Grid.SetColumn(hourBox, 3);
        Grid.SetColumn(minuteBox, 4);
        Grid.SetColumn(btnClearDue, 5);
        dueGrid.Children.Add(yearBox);
        dueGrid.Children.Add(monthBox);
        dueGrid.Children.Add(dayBox);
        dueGrid.Children.Add(hourBox);
        dueGrid.Children.Add(minuteBox);
        dueGrid.Children.Add(btnClearDue);
        sp.Children.Add(dueGrid);

        sp.Children.Add(new TextBlock {
            Text = "子任务",
            Foreground = TB(LabelTxt()),
            FontSize = 11, Margin = new Thickness(0, 16, 0, 6)
        });

        var subList = new StackPanel();
        System.Action refreshSubList = null;
        refreshSubList = () => {
            subList.Children.Clear();
            for (int si = 0; si < todo.SubTasks.Count; si++) {
                var sub = todo.SubTasks[si];
                var sidx = si;
                var subGrid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var subCb = new CheckBox {
                    IsChecked = sub.Completed,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                string subCbXaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>" +
                    "<Border Width='14' Height='14' CornerRadius='3' Background='{TemplateBinding Background}' " +
                    "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' x:Name='border'>" +
                    "<Path x:Name='checkMark' Width='8' Height='8' Stretch='Uniform' Stroke='White' " +
                    "StrokeThickness='1.5' Data='M 1.5 5 L 4 7.5 L 9.5 2' Visibility='Collapsed' " +
                    "HorizontalAlignment='Center' VerticalAlignment='Center' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>" +
                    "</Border>" +
                    "<ControlTemplate.Triggers>" +
                    "<Trigger Property='IsChecked' Value='True'>" +
                    "<Setter TargetName='checkMark' Property='Visibility' Value='Visible'/>" +
                    "<Setter TargetName='border' Property='Background' Value='" + accentHex + "'/>" +
                    "<Setter TargetName='border' Property='BorderBrush' Value='" + accentHex + "'/>" +
                    "</Trigger>" +
                    "</ControlTemplate.Triggers>" +
                    "</ControlTemplate>";
                subCb.Template = (ControlTemplate)XamlReader.Parse(subCbXaml);
                subCb.Background = Brushes.Transparent;
                subCb.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(isLightTheme ? 120 : 100), 160, 160, 170));
                subCb.Checked += (s, e) => { sub.Completed = true; };
                subCb.Unchecked += (s, e) => { sub.Completed = false; };
                Grid.SetColumn(subCb, 0);
                subGrid.Children.Add(subCb);

                var subTxt = new TextBlock {
                    Text = sub.Text,
                    Foreground = sub.Completed ? TB(SubDoneTxt()) : TB(TextPri()),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextDecorations = sub.Completed ? TextDecorations.Strikethrough : null,
                    FontSize = 13
                };
                Grid.SetColumn(subTxt, 1);
                subGrid.Children.Add(subTxt);

                var subDel = new Button {
                    Content = "×", Width = 16, Height = 16,
                    Background = Brushes.Transparent,
                    Foreground = TB(isLightTheme ? TC(100, 140, 140, 150) : TC(100, 180, 180, 190)),
                    BorderThickness = new Thickness(0), FontSize = 12,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Template = CreateRoundedTemplate(8)
                };
                subDel.Click += (s, e) => { todo.SubTasks.RemoveAt(sidx); refreshSubList(); };
                Grid.SetColumn(subDel, 2);
                subGrid.Children.Add(subDel);

                subList.Children.Add(subGrid);
            }
        };
        refreshSubList();
        sp.Children.Add(subList);

        var addSubGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        addSubGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addSubGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var subInput = new TextBox {
            Background = TB(EditBgC()),
            Foreground = TB(TextPri()),
            BorderBrush = TB(isLightTheme ? TC(60, 0, 0, 0) : TC(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
            CaretBrush = TB(TextPri())
        };
        Grid.SetColumn(subInput, 0);
        addSubGrid.Children.Add(subInput);
        var btnAddSub = CreateBtn("+", 24, 24, 6);
        btnAddSub.FontSize = 14;
        btnAddSub.Margin = new Thickness(6, 0, 0, 0);
        btnAddSub.Click += (s, e) => {
            var st = subInput.Text.Trim();
            if (!string.IsNullOrEmpty(st)) {
                todo.SubTasks.Add(new SubTask { Text = st, CreatedAt = DateTime.Now });
                subInput.Text = "";
                refreshSubList();
            }
        };
        Grid.SetColumn(btnAddSub, 1);
        addSubGrid.Children.Add(btnAddSub);
        sp.Children.Add(addSubGrid);

        var btnSave = new Button {
            Content = "保存",
            Background = GetAccentBrush(200),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 16, 0, 0),
            Template = CreateRoundedTemplate(10)
        };
        btnSave.Click += (s, e) => {
            todo.Text = nameEdit.Text.Trim();
            if (yearBox.SelectedItem != null && monthBox.SelectedItem != null && dayBox.SelectedItem != null) {
                int y = int.Parse((string)yearBox.SelectedItem);
                int m = int.Parse((string)monthBox.SelectedItem);
                int d = int.Parse((string)dayBox.SelectedItem);
                d = Math.Min(d, DateTime.DaysInMonth(y, m));
                int h = int.Parse((string)hourBox.SelectedItem);
                int min = int.Parse((string)minuteBox.SelectedItem);
                todo.DueTime = new DateTime(y, m, d, h, min, 0);
            } else {
                todo.DueTime = null;
            }
            Save();
            detailOverlay.Visibility = Visibility.Collapsed;
            detailOverlay.Children.Clear();
            Render();
        };
        sp.Children.Add(btnSave);

        card.Child = sp;
        detailOverlay.Children.Add(card);

        var scale = new ScaleTransform(0.88, 0.88);
        card.RenderTransform = scale;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.Opacity = 0;
        Animate(card, UIElement.OpacityProperty, 0, 1, 250, EasingType.EaseOutQuint);
        Animate(scale, ScaleTransform.ScaleXProperty, 0.88, 1.0, 450, EasingType.Spring);
        Animate(scale, ScaleTransform.ScaleYProperty, 0.88, 1.0, 450, EasingType.Spring);
    }

    void OpenSettings() {
        detailOverlay.Children.Clear();
        detailOverlay.Visibility = Visibility.Visible;

        var card = new Border {
            CornerRadius = new CornerRadius(20),
            Background = TB(CardBgC()),
            BorderBrush = isLightTheme ? new SolidColorBrush(Color.FromArgb(120, 40, 40, 45)) : new SolidColorBrush(Color.FromArgb(140, 200, 200, 210)),
            BorderThickness = new Thickness(1),
            Width = 280,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16)
        };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect {
            BlurRadius = 32, ShadowDepth = 12, Direction = 270,
            Color = isLightTheme ? Color.FromArgb(120, 0, 0, 0) : Color.FromArgb(160, 0, 0, 0), Opacity = isLightTheme ? 0.25 : 0.4
        };

        var sp = new StackPanel { Margin = new Thickness(20) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerTitle = new TextBlock {
            Text = "设置",
            Foreground = TB(TextPri()),
            FontSize = 16, FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(headerTitle, 0);
        header.Children.Add(headerTitle);
        var btnCloseDetail = CreateTrafficLightBtn("×", Brushes.Transparent, new SolidColorBrush(Color.FromArgb(140, 255, 95, 85)));
        btnCloseDetail.Click += (s, e) => {
            SaveSettings();
            if (isLightTheme)
                inputBox.CaretBrush = new SolidColorBrush(Color.FromArgb(200, 40, 40, 45));
            else
                inputBox.CaretBrush = new SolidColorBrush(Color.FromArgb(200, 200, 200, 210));
            Render();
            detailOverlay.Visibility = Visibility.Collapsed;
            detailOverlay.Children.Clear();
        };
        Grid.SetColumn(btnCloseDetail, 1);
        header.Children.Add(btnCloseDetail);
        sp.Children.Add(header);

        var btnToggleTheme = CreateBtn(isLightTheme ? "切换到深色" : "切换到浅色", 140, 26, 8);
        btnToggleTheme.FontSize = 11;
        btnToggleTheme.Margin = new Thickness(0, 8, 0, 0);
        btnToggleTheme.Click += (s, e) => {
            isLightTheme = !isLightTheme;
            SaveSettings();
            // Rebuild root gradient
            var newGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            newGradient.GradientStops.Add(new GradientStop(WinBgTop(), 0));
            newGradient.GradientStops.Add(new GradientStop(WinBgBottom(), 1));
            rootBorder.Background = newGradient;
            if (isLightTheme) {
                var darkBorder = new SolidColorBrush(Color.FromArgb(120, 40, 40, 45));
                rootBorder.BorderBrush = darkBorder;
                inputWrap.BorderBrush = darkBorder;
                inputBox.CaretBrush = darkBorder;
            } else {
                var lightBorder = new SolidColorBrush(Color.FromArgb(140, 200, 200, 210));
                rootBorder.BorderBrush = lightBorder;
                inputWrap.BorderBrush = lightBorder;
                inputBox.CaretBrush = lightBorder;
            }
            inputWrap.Background = TB(InputBgC());
            titleLabel.Foreground = TB(TextPri());
            inputBox.Foreground = TB(TextPri());
            detailOverlay.Background = TB(OverlayBgC());
            EnableAcrylic();
            Render();
            btnToggleTheme.Content = isLightTheme ? "切换到深色" : "切换到浅色";

            // Refresh title bar button colors for new theme
            Action<Button> refreshBtn = btn => {
                if (btn != null) btn.Foreground = (bool)btn.Tag ? TB(TextPri()) : TB(BtnDefFg());
            };
            refreshBtn(btnSettings);
            refreshBtn(btnHistory);
            refreshBtn(btnGhost);
            refreshBtn(btnPass);
        };
        sp.Children.Add(btnToggleTheme);

        sp.Children.Add(new TextBlock {
            Text = "背景图片",
            Foreground = TB(LabelTxt()),
            FontSize = 11, Margin = new Thickness(0, 12, 0, 8)
        });

        // Fill mode
        var fillWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        string[] fillModes = new[] { "裁切填充", "完整显示", "拉伸填充" };
        string[] fillValues = new[] { "UniformToFill", "Uniform", "Fill" };
        Button[] fillBtns = new Button[3];
        for (int fi = 0; fi < 3; fi++) {
            int idx = fi;
            fillBtns[fi] = CreateBtn(fillModes[fi], 56, 24, 6);
            fillBtns[fi].FontSize = 10;
            fillBtns[fi].Margin = new Thickness(0, 0, 6, 0);
            if (backgroundStretch == fillValues[fi]) {
                fillBtns[fi].Background = GetAccentBrush(160);
            }
            fillBtns[fi].Click += (s, e) => {
                backgroundStretch = fillValues[idx];
                SaveSettings();
                ApplyBackgroundImage();
                foreach (var fb in fillBtns) fb.Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
                fillBtns[idx].Background = GetAccentBrush(160);
            };
            fillWrap.Children.Add(fillBtns[fi]);
        }
        sp.Children.Add(fillWrap);

        // Crop ratio
        sp.Children.Add(new TextBlock {
            Text = "裁剪比例",
            Foreground = TB(LabelTxt()),
            FontSize = 11, Margin = new Thickness(0, 0, 0, 6)
        });
        var ratioBox = CreateStyledComboBox(140, 26, new Thickness(0, 0, 0, 8));
        string[] ratios = new[] { "原始比例", "16:9", "4:3", "1:1", "窗口比例" };
        foreach (var r in ratios) ratioBox.Items.Add(r);
        ratioBox.SelectedItem = cropRatio;
        ratioBox.SelectionChanged += (s, e) => {
            if (ratioBox.SelectedItem != null) {
                cropRatio = (string)ratioBox.SelectedItem;
                SaveSettings();
            }
        };
        sp.Children.Add(ratioBox);

        var imgGrid = new Grid();
        imgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        imgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        imgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var imgName = new TextBlock {
            Text = string.IsNullOrEmpty(backgroundImagePath) ? "未选择" : System.IO.Path.GetFileName(backgroundImagePath),
            Foreground = TB(TextSec()),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(imgName, 0);
        imgGrid.Children.Add(imgName);

        var btnPickImg = CreateBtn("选择", 44, 26, 6);
        btnPickImg.FontSize = 11;
        btnPickImg.Margin = new Thickness(4, 0, 0, 0);
        btnPickImg.Click += (s, e) => {
            var dlg = new OpenFileDialog {
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif"
            };
            if (dlg.ShowDialog() == true) {
                try {
                    var destDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "TodoTransparent");
                    Directory.CreateDirectory(destDir);
                    var dest = System.IO.Path.Combine(destDir, "bg_" + Guid.NewGuid().ToString("N") + ".png");

                    var srcBmp = new System.Windows.Media.Imaging.BitmapImage();
                    srcBmp.BeginInit();
                    srcBmp.UriSource = new Uri(dlg.FileName, UriKind.Absolute);
                    srcBmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    srcBmp.EndInit();

                    int srcW = srcBmp.PixelWidth;
                    int srcH = srcBmp.PixelHeight;
                    int cropW = srcW, cropH = srcH;
                    double winRatio = ActualWidth / ActualHeight;

                    switch (cropRatio) {
                        case "16:9": cropH = (int)(cropW / (16.0 / 9.0)); break;
                        case "4:3": cropH = (int)(cropW / (4.0 / 3.0)); break;
                        case "1:1": cropH = cropW; break;
                        case "窗口比例": cropW = (int)(cropH * winRatio); break;
                    }

                    if (cropH > srcH) { cropH = srcH; cropW = (int)(cropH * ((double)srcW / srcH)); }
                    if (cropW > srcW) { cropW = srcW; cropH = (int)(cropW / ((double)srcW / srcH)); }
                    if (cropW > srcW) cropW = srcW;
                    if (cropH > srcH) cropH = srcH;

                    int offsetX = (srcW - cropW) / 2;
                    int offsetY = (srcH - cropH) / 2;

                    if (cropRatio == "原始比例" || (cropW == srcW && cropH == srcH)) {
                        File.Copy(dlg.FileName, dest, true);
                    } else {
                        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(srcBmp, new Int32Rect(offsetX, offsetY, cropW, cropH));
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cropped));
                        using (var fs = File.Create(dest))
                            encoder.Save(fs);
                    }

                    backgroundImagePath = dest;
                    SaveSettings();
                    ApplyBackgroundImage();
                    imgName.Text = System.IO.Path.GetFileName(dest);
                } catch { }
            }
        };
        Grid.SetColumn(btnPickImg, 1);
        imgGrid.Children.Add(btnPickImg);

        var btnClearImg = CreateBtn("清除", 44, 26, 6);
        btnClearImg.FontSize = 11;
        btnClearImg.Margin = new Thickness(4, 0, 0, 0);
        btnClearImg.Click += (s, e) => {
            backgroundImagePath = null;
            SaveSettings();
            ApplyBackgroundImage();
            imgName.Text = "未选择";
        };
        Grid.SetColumn(btnClearImg, 2);
        imgGrid.Children.Add(btnClearImg);

        sp.Children.Add(imgGrid);
        sp.Children.Add(new TextBlock {
            Text = "提示：选择图片后会根据设定的裁剪比例自动居中裁剪。",
            Foreground = TB(TipTxt()),
            FontSize = 10,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        card.Child = sp;
        detailOverlay.Children.Add(card);

        var scale = new ScaleTransform(0.88, 0.88);
        card.RenderTransform = scale;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.Opacity = 0;
        Animate(card, UIElement.OpacityProperty, 0, 1, 250, EasingType.EaseOutQuint);
        Animate(scale, ScaleTransform.ScaleXProperty, 0.88, 1.0, 450, EasingType.Spring);
        Animate(scale, ScaleTransform.ScaleYProperty, 0.88, 1.0, 450, EasingType.Spring);
    }

    void FinishEdit(Grid grid, TextBox edit, TextBlock txt, TodoItem todo, Border parent) {
        var newText = edit.Text.Trim();
        if (!string.IsNullOrEmpty(newText)) todo.Text = newText;
        else { todos.Remove(todo); Save(); Render(); return; }
        grid.Children.Remove(edit);
        txt.Text = todo.Text;
        grid.Children.Add(txt);
        Save(); Render();
    }
}

public class Program {
    [STAThread]
    public static void Main() {
        try {
            var app = new Application();
            app.DispatcherUnhandledException += (s, e) => {
                System.IO.File.WriteAllText("error.log", e.Exception.ToString());
                e.Handled = true;
            };
            app.Run(new MainWindow());
        } catch (Exception ex) {
            System.IO.File.WriteAllText("error.log", ex.ToString());
        }
    }
}
