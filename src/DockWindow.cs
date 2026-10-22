// Rack (WPF 版) —— 抽屉窗口
//
// 透明原理：
//   1) DwmExtendFrameIntoClientArea(-1) 把整个客户区变成"玻璃区域"
//   2) CompositionTarget.BackgroundColor = Transparent 让 WPF 不去填底色
//   3) 根部 Border 只盖一层半透明纱（alpha 可调），其余地方透出背后的桌面
//   因为是系统实时合成的，背后内容一动它就跟着动 —— 不是截图快照。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace RackWpf
{
    internal sealed class DockWindow : Window
    {
        private const double TileW = 110;
        private const double TileH = 108;
        private const int PerRow = 4;

        private readonly Border _root;
        private readonly Grid _grid;
        private readonly Border _dragBar;
        private double _tabWidth = 125;
        private readonly ScrollViewer _tabScroll;
        private readonly StackPanel _tabBar;
        private readonly ScrollViewer _scroll;
        private readonly WrapPanel _wrap;
        private readonly Border _footer;
        private readonly TextBlock _hint;
        private readonly Border _pinBtn;
        private readonly TextBlock _pinText;
        private readonly Border _importBtn;
        private readonly Border _settingsBtn;

        private string _currentGroup;
        private bool _pinned;
        private bool _moving;
        private System.Windows.Point _downScreen;
        private System.Windows.Point _winStart;
        private bool _dropHover;
        private bool _suppressClose;
        private bool _shown;

        /// <summary>设置被保存后触发，让外面的托盘层重新注册热键等。</summary>
        internal event EventHandler SettingsSaved;

        internal DockWindow(bool startPinned)
        {
            _pinned = startPinned || Cfg.PinByDefault;   // 默认值可在设置里改

            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;          // 关键：不能是分层窗口
            Background = System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            AllowDrop = true;
            FontFamily = Theme.Font;
            Width = 500;
            Height = 520;

            _root = new Border();
            _root.CornerRadius = new CornerRadius(14);
            _root.Background = Theme.Veil(Cfg.Alpha);
            _root.BorderBrush = Theme.Veil(110);
            _root.BorderThickness = new Thickness(Cfg.Alpha > 0 ? 1 : 0);
            _root.ClipToBounds = true;
            Content = _root;

            _grid = new Grid();
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            _root.Child = _grid;

            // ── 顶部拖动条 ──
            _dragBar = new Border();
            _dragBar.Background = System.Windows.Media.Brushes.Transparent;
            _dragBar.Cursor = Cursors.SizeAll;
            _dragBar.MouseLeftButtonDown += DragDown;
            _dragBar.MouseMove += DragMove2;
            _dragBar.MouseLeftButtonUp += DragUp;
            Grid.SetRow(_dragBar, 0);
            _grid.Children.Add(_dragBar);

            System.Windows.Shapes.Rectangle grip = new System.Windows.Shapes.Rectangle();
            grip.Width = 34;
            grip.Height = 3;
            grip.RadiusX = 1.5;
            grip.RadiusY = 1.5;
            grip.Fill = Theme.Solid(System.Windows.Media.Color.FromArgb(110, 120, 130, 142));
            grip.HorizontalAlignment = HorizontalAlignment.Center;
            grip.VerticalAlignment = VerticalAlignment.Center;
            _dragBar.Child = grip;

            // ── 标签栏：最多并排显示 4 个，超出的横向滚动 ──
            Grid tabHost = new Grid();
            tabHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            tabHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });

            _tabScroll = new ScrollViewer();
            _tabScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _tabScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _tabScroll.PreviewMouseWheel += TabWheel;
            Grid.SetRow(_tabScroll, 0);
            tabHost.Children.Add(_tabScroll);

            Border tabLine = new Border();
            tabLine.Height = 1;
            tabLine.Background = Theme.Solid(Theme.Line);
            Grid.SetRow(tabLine, 1);
            tabHost.Children.Add(tabLine);

            _tabBar = new StackPanel();
            _tabBar.Orientation = Orientation.Horizontal;
            _tabBar.Background = System.Windows.Media.Brushes.Transparent;
            _tabScroll.Content = _tabBar;

            Grid.SetRow(tabHost, 1);
            _grid.Children.Add(tabHost);

            // ── 图标区 ──
            _wrap = new WrapPanel();
            _wrap.Orientation = Orientation.Horizontal;
            _wrap.Margin = new Thickness(10, 10, 4, 10);
            _wrap.ItemWidth = TileW;
            _wrap.ItemHeight = TileH;

            _scroll = new ScrollViewer();
            // 藏起来：滚轮照样能滚，但不会在透明面板上插一条突兀的灰白条
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.Content = _wrap;
            _scroll.AllowDrop = true;
            _scroll.Background = System.Windows.Media.Brushes.Transparent;
            Grid.SetRow(_scroll, 2);
            _grid.Children.Add(_scroll);

            // ── 底部栏 ──
            _footer = new Border();
            _footer.Background = System.Windows.Media.Brushes.Transparent;
            _footer.BorderThickness = new Thickness(0);
            Grid.SetRow(_footer, 3);
            _grid.Children.Add(_footer);

            Grid footGrid = new Grid();
            footGrid.Margin = new Thickness(14, 0, 12, 0);
            _footer.Child = footGrid;

            _hint = new TextBlock();
            _hint.FontSize = 11.5;
            _hint.Foreground = Theme.Solid(Theme.PanelTextDim);
            _hint.VerticalAlignment = VerticalAlignment.Center;
            _hint.HorizontalAlignment = HorizontalAlignment.Left;
            _hint.TextTrimming = TextTrimming.CharacterEllipsis;
            Shadow(_hint);
            footGrid.Children.Add(_hint);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.VerticalAlignment = VerticalAlignment.Center;
            footGrid.Children.Add(btns);

            _pinBtn = MakeMiniButton(_pinned ? "已钉住" : "钉住", 56, _pinned);
            _pinText = _pinBtn.Child as TextBlock;
            _pinBtn.MouseLeftButtonUp += delegate { TogglePin(); };
            btns.Children.Add(_pinBtn);

            _importBtn = MakeMiniButton("导入…", 62, false);
            _importBtn.MouseLeftButtonUp += delegate { ShowImport(); };
            btns.Children.Add(_importBtn);

            _settingsBtn = MakeMiniButton("设置", 52, false);
            _settingsBtn.MouseLeftButtonUp += delegate { ShowSettings(); };
            btns.Children.Add(_settingsBtn);

            // 拖放接收
            DragOver += OnDragOver;
            Drop += OnDrop;
            DragLeave += delegate { _dropHover = false; };
            DragEnter += delegate { _dropHover = true; };

            Deactivated += OnDeactivated;
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape && !_pinned) Close();
            };

            Loaded += delegate
            {
                _shown = true;
                ReloadTiles();
                UpdateClip();
                AddHandler();
            };
            SizeChanged += delegate { UpdateClip(); };

            string[] groups = Store.Groups();
            _currentGroup = groups.Length > 0 ? groups[0] : null;
            RebuildTabs(groups, 0);
        }

        private void AddHandler()
        {
            // 让整个窗口都能接住拖动进来的文件
            _wrap.AllowDrop = true;
            _wrap.DragOver += OnDragOver;
            _wrap.Drop += OnDrop;
        }

        /// <summary>
        /// 纯透明模式下文字直接压在桌面上，背景一花就认不出。
        /// 给它一层很淡的投影来提高对比度 —— 只改字，不改透明度。
        /// </summary>
        private static void Shadow(TextBlock t)
        {
            if (!Cfg.TextShadow) return;
            System.Windows.Media.Effects.DropShadowEffect d =
                new System.Windows.Media.Effects.DropShadowEffect();
            d.Color = System.Windows.Media.Colors.Black;
            d.BlurRadius = 4;
            d.ShadowDepth = 1;
            d.Direction = 270;
            d.Opacity = 0.7;
            t.Effect = d;
        }
        // ── 圆角裁剪 ──
        private void UpdateClip()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            RectangleGeometry g = new RectangleGeometry(
                new Rect(0, 0, ActualWidth, ActualHeight), 14, 14);
            _root.Clip = g;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            HwndSource src = HwndSource.FromHwnd(hwnd);
            if (src != null && src.CompositionTarget != null)
                src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;

            Nat.ExtendGlass(hwnd);
        }

        // ── 小按钮 ──
        private Border MakeMiniButton(string text, double width, bool active)
        {
            Border b = new Border();
            b.Width = width;
            b.Height = 28;
            b.CornerRadius = new CornerRadius(6);
            b.Margin = new Thickness(6, 0, 0, 0);
            b.Cursor = Cursors.Hand;
            b.Background = active ? Theme.Solid(Theme.AccentSoft) : System.Windows.Media.Brushes.Transparent;
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = active ? Theme.Solid(Theme.Accent) : System.Windows.Media.Brushes.Transparent;

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11.5;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            Shadow(t);

            // 透明玻璃上纯描边按钮几乎看不见，给它一层淡淡的底
            StyleMini(b, active);

            b.MouseEnter += delegate
            {
                b.BorderBrush = Theme.Solid(Theme.Accent);
            };
            b.MouseLeave += delegate
            {
                StyleMini(b, (b == _pinBtn && _pinned));
            };
            return b;
        }

        private void StyleMini(Border b, bool active)
        {
            // 纯透明模式下绝不能有实心块，只留一圈细边
            b.Background = System.Windows.Media.Brushes.Transparent;
            b.BorderBrush = active ? Theme.Solid(Theme.Accent) : Theme.Veil(110);
            b.BorderThickness = new Thickness(1);
            TextBlock t = b.Child as TextBlock;
            if (t != null) t.Foreground = Theme.Solid(active ? Theme.Accent : Theme.PanelText);
        }
        // ── 钉住 ──
        private void TogglePin()
        {
            _pinned = !_pinned;
            _pinText.Text = _pinned ? "已钉住" : "钉住";
            _pinBtn.Background = _pinned ? Theme.Solid(Theme.AccentSoft) : System.Windows.Media.Brushes.Transparent;
            _pinBtn.BorderBrush = _pinned ? Theme.Solid(Theme.Accent) : System.Windows.Media.Brushes.Transparent;
            StyleMini(_pinBtn, _pinned);
            _dragBar.Cursor = _pinned ? Cursors.Arrow : Cursors.SizeAll;
        }

        // ── 拖动窗口 ──
        private void DragDown(object sender, MouseButtonEventArgs e)
        {
            // 钉住只管"别自动关"，位置随便拖
            _moving = true;
            _downScreen = PointToScreen(e.GetPosition(this));
            _winStart = new System.Windows.Point(Left, Top);
            _dragBar.CaptureMouse();
        }

        private void DragMove2(object sender, MouseEventArgs e)
        {
            if (!_moving) return;
            System.Windows.Point now = PointToScreen(e.GetPosition(this));
            // PointToScreen 给的是物理像素，而 Left/Top 是逻辑单位；
            // 不除以 DPI 缩放的话，窗口会以 1.5 倍的速度追鼠标
            double s = DpiScale();
            Left = _winStart.X + (now.X - _downScreen.X) / s;
            Top = _winStart.Y + (now.Y - _downScreen.Y) / s;
        }

        private void DragUp(object sender, MouseButtonEventArgs e)
        {
            if (!_moving) return;
            _moving = false;
            _dragBar.ReleaseMouseCapture();
            Cfg.SetInt("ui.posX", (int)Math.Round(Left));
            Cfg.SetInt("ui.posY", (int)Math.Round(Top));
            Cfg.Save();
        }

        // ── 标签 ──
        private void RebuildTabs(string[] groups, int selected)
        {
            _tabBar.Children.Clear();
            if (groups == null || groups.Length == 0) return;

            // 新出现的分组补进 order.ini 的记录，删掉的自动剔除
            Order.Sync(Order.GroupKey, new List<string>(groups));

            const int visible = 4;
            double w = groups.Length <= visible ? 500.0 / groups.Length : 500.0 / visible;
            _tabWidth = w;

            for (int i = 0; i < groups.Length; i++)
            {
                string g = groups[i];
                int idx = i;

                Border cell = new Border();
                cell.Width = w;
                cell.Background = System.Windows.Media.Brushes.Transparent;
                cell.Cursor = Cursors.Hand;
                cell.Tag = g;
                cell.AllowDrop = true;

                Grid inner = new Grid();
                TextBlock t = new TextBlock();
                t.Text = g;
                t.FontSize = 12;
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.Margin = new Thickness(4, 0, 4, 0);
                t.Foreground = Theme.Solid(i == selected ? Theme.PanelText : Theme.PanelTextDim);
                Shadow(t);
                inner.Children.Add(t);

                if (i == selected)
                {
                    Border underline = new Border();
                    underline.Height = 3;
                    underline.CornerRadius = new CornerRadius(1.5);
                    underline.Background = Theme.Solid(Theme.Accent);
                    underline.VerticalAlignment = VerticalAlignment.Bottom;
                    underline.Margin = new Thickness(14, 0, 14, 0);
                    inner.Children.Add(underline);
                }

                cell.Child = inner;

                // 点一下 = 切分类；按住拖动 = 调整分类顺序
                bool dragging = false;
                System.Windows.Point downAt = new System.Windows.Point();

                cell.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    downAt = e.GetPosition(cell);
                    dragging = true;
                };

                cell.MouseMove += delegate(object s, MouseEventArgs e)
                {
                    if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
                    System.Windows.Point p = e.GetPosition(cell);
                    if (Math.Abs(p.X - downAt.X) <= 6 && Math.Abs(p.Y - downAt.Y) <= 6) return;
                    dragging = false;
                    DataObject d = new DataObject("RackGroup", g);
                    DragDrop.DoDragDrop(cell, d, DragDropEffects.Move);
                };

                cell.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    if (!dragging) return;
                    dragging = false;
                    _currentGroup = g;
                    RebuildTabs(Store.Groups(), idx);
                    ReloadTiles();
                };

                cell.DragOver += delegate(object s, DragEventArgs e)
                {
                    if (!e.Data.GetDataPresent("RackGroup")) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
                    e.Effects = DragDropEffects.Move;
                    System.Windows.Point p = e.GetPosition(cell);
                    cell.BorderBrush = Theme.Solid(Theme.Accent);
                    cell.BorderThickness = (p.X < cell.ActualWidth / 2)
                        ? new Thickness(3, 0, 0, 0) : new Thickness(0, 0, 3, 0);
                    e.Handled = true;
                };
                cell.DragLeave += delegate { cell.BorderThickness = new Thickness(0); };
                cell.Drop += delegate(object s, DragEventArgs e)
                {
                    cell.BorderThickness = new Thickness(0);
                    string src = e.Data.GetData("RackGroup") as string;
                    if (string.IsNullOrEmpty(src) || src == g) return;
                    System.Windows.Point p = e.GetPosition(cell);
                    bool before = p.X < cell.ActualWidth / 2;
                    Order.MoveRelative(Order.GroupKey, src, g, before);

                    string keep = _currentGroup;
                    string[] gs = Store.Groups();
                    int sel = 0;
                    for (int k = 0; k < gs.Length; k++) if (gs[k] == keep) { sel = k; break; }
                    RebuildTabs(gs, sel);
                    e.Handled = true;
                };

                _tabBar.Children.Add(cell);
            }

            // 让当前选中的标签滚进可见范围
            if (groups.Length > visible)
            {
                double off = (selected - 1) * w;
                if (off < 0) off = 0;
                _tabScroll.ScrollToHorizontalOffset(off);
            }
        }

        /// <summary>鼠标在分类栏上滚滚轮时，改成横向滚（切分类比上下滚更直觉）。</summary>
        private void TabWheel(object sender, MouseWheelEventArgs e)
        {
            // 一次滚一格，并且吸附到整格，免得把某个分类切一半
            double step = _tabWidth > 1 ? _tabWidth : 125;
            double dir = e.Delta > 0 ? -1 : 1;
            double target = _tabScroll.HorizontalOffset + dir * step;

            target = Math.Round(target / step) * step;
            if (target < 0) target = 0;
            double max = _tabScroll.ScrollableWidth;
            if (target > max) target = max;

            _tabScroll.ScrollToHorizontalOffset(target);
            e.Handled = true;
        }
        // ── 列表 ──
        internal void ReloadTiles()
        {
            if (_wrap == null) return;
            _wrap.Children.Clear();

            List<string> items = new List<string>();
            string dir = _currentGroup == null ? null : Store.DirFor(_currentGroup);
            try
            {
                if (dir != null && Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir)) items.Add(f);
                    foreach (string d in Directory.GetDirectories(dir)) items.Add(d);
                }
            }
            catch { }

            // 顺序：order.ini 里记过的按记录排，没记过的按名称追加在后面
            List<string> names = new List<string>();
            foreach (string p in items) names.Add(Path.GetFileName(p));
            Order.Sync(_currentGroup, names);
            List<string> ord = Order.Get(_currentGroup);

            items.Sort(delegate(string a, string b)
            {
                int ia = ord.IndexOf(Path.GetFileName(a));
                int ib = ord.IndexOf(Path.GetFileName(b));
                if (ia < 0) ia = 99999;
                if (ib < 0) ib = 99999;
                if (ia != ib) return ia - ib;
                return string.Compare(Path.GetFileName(a), Path.GetFileName(b),
                                      StringComparison.CurrentCultureIgnoreCase);
            });

            foreach (string path in items) _wrap.Children.Add(MakeTile(path));

            _hint.Text = items.Count == 0
                ? "把图标拖进来，或点「导入…」"
                : items.Count.ToString() + " 项 · 右键可移动分组";

            FitHeight(items.Count);
        }

        private void FitHeight(int count)
        {
            // 固定 4 列 × 3 行，多出来的靠滚动条。
            // 以前是随内容伸缩的，但那样切换分类时高度一变就得重算位置，
            // 会把用户拖好的位置冲回右下角 —— 得不偿失。
            double fixedH = 22 + 52 + 46 + 20 + 3 * TileH + 12;   // 末尾留余量，否则第三行文字会贴到底部栏
            if (Math.Abs(Height - fixedH) > 0.5) Height = fixedH;
        }

        private static string DisplayName(string path)
        {
            string n;
            if (Directory.Exists(path)) n = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            else n = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(n)) n = Path.GetFileName(path);
            if (n.Length > 14) n = n.Substring(0, 13) + "…";
            return n;
        }

        // ── 单个图标 ──
        private FrameworkElement MakeTile(string path)
        {
            double scale = 1.0;
            try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            if (scale <= 0) scale = 1.0;

            Border tile = new Border();
            tile.Width = TileW;
            tile.Height = TileH;
            tile.CornerRadius = new CornerRadius(10);
            tile.Background = System.Windows.Media.Brushes.Transparent;
            tile.Cursor = Cursors.Hand;

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Top;

            System.Windows.Controls.Image im = new System.Windows.Controls.Image();
            im.Source = Icons.Get(path, (int)(48 * scale));
            im.Width = 46;
            im.Height = 46;
            im.Margin = new Thickness(0, 12, 0, 8);
            im.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
            sp.Children.Add(im);

            TextBlock tb = new TextBlock();
            tb.Text = DisplayName(path);
            tb.FontSize = 11.5;
            tb.Foreground = Theme.Solid(Theme.PanelText);
            tb.TextAlignment = TextAlignment.Center;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.MaxHeight = 32;
            tb.Margin = new Thickness(4, 0, 4, 0);
            Shadow(tb);
            sp.Children.Add(tb);

            tile.Child = sp;

            // 点击判定：自己算双击，不依赖系统的 DoubleClick
            DateTime lastDown = DateTime.MinValue;
            System.Windows.Point lastPos = new System.Windows.Point();
            bool dragging = false;
            System.Windows.Point downAt = new System.Windows.Point();

            // 悬停不铺底：用户要的是纯透明，悬停块会和背后的桌面打架

            tile.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                downAt = e.GetPosition(tile);
                dragging = true;
                DateTime now = DateTime.Now;
                bool isDouble = (now - lastDown).TotalMilliseconds <= 500
                                && Math.Abs(downAt.X - lastPos.X) <= 4
                                && Math.Abs(downAt.Y - lastPos.Y) <= 4;
                lastDown = now;
                lastPos = downAt;

                if (isDouble)
                {
                    dragging = false;
                    e.Handled = true;
                    Launch(path);
                }
                else if (Cfg.SingleClickLaunch)
                {
                    dragging = false;
                    e.Handled = true;
                    Launch(path);
                }
            };

            tile.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
                System.Windows.Point p = e.GetPosition(tile);
                if (Math.Abs(p.X - downAt.X) <= 6 && Math.Abs(p.Y - downAt.Y) <= 6) return;
                dragging = false;
                _suppressClose = true;
                try
                {
                    // 同时挂两种格式：
                    //   FileDrop → 拖到资源管理器，那边把文件移走（移出抽屉）
                    //   RackTile → 拖到抽屉内部，我们自己认出来当成"排序"
                    DataObject data = new DataObject();
                    data.SetData(DataFormats.FileDrop, new string[] { path });
                    data.SetData("RackTile", path);
                    DragDrop.DoDragDrop(tile, data, DragDropEffects.Move);
                    ReloadTiles();
                }
                catch { }
                _suppressClose = false;
            };

            // ── 抽屉内部的拖拽排序 ──
            tile.AllowDrop = true;
            tile.Tag = path;
            tile.DragOver += delegate(object s, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent("RackTile")) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
                e.Effects = DragDropEffects.Move;
                System.Windows.Point dp = e.GetPosition(tile);
                tile.BorderBrush = Theme.Solid(Theme.Accent);
                tile.BorderThickness = (dp.X < tile.ActualWidth / 2)
                    ? new Thickness(3, 0, 0, 0)
                    : new Thickness(0, 0, 3, 0);
                e.Handled = true;
            };
            tile.DragLeave += delegate { tile.BorderThickness = new Thickness(0); };
            tile.Drop += delegate(object s, DragEventArgs e)
            {
                tile.BorderThickness = new Thickness(0);
                string src = e.Data.GetData("RackTile") as string;
                if (string.IsNullOrEmpty(src)) return;
                System.Windows.Point dp = e.GetPosition(tile);
                bool before = dp.X < tile.ActualWidth / 2;
                Order.MoveRelative(_currentGroup, Path.GetFileName(src), Path.GetFileName(path), before);
                ReloadTiles();
                e.Handled = true;
            };
            tile.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                ShowTileMenu(tile, path);
                e.Handled = true;
            };

            return tile;
        }

        private void Launch(string path)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(path);
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
                try
                {
                    File.AppendAllText(Path.Combine(Store.Root, "launch.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  OK  " + path + Environment.NewLine);
                }
                catch { }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Store.Root, "launch.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  ERR " + ex.Message + Environment.NewLine);
                }
                catch { }
                MessageBox.Show("打不开这一项：\r\n" + path + "\r\n\r\n" + ex.Message, "Rack");
            }
            if (!_pinned) Close();
        }

        /// <summary>
        /// 统一样式的右键菜单：圆角、跟随主题、跟托盘菜单观感一致。
        /// WPF 的 ContextMenu 默认是方角 + 系统灰，在透明抽屉上很出戏。
        /// </summary>
        private static ContextMenu MakeStyledMenu()
        {
            // 2026-10-20 排查记录：自定义 ControlTemplate 会让 ContextMenu 测量塌成 1x1
            // （右键后出现 3 个 1x1@(0,0) 的隐藏窗口），菜单完全不可见。
            // 现已回退到最简形态，先确认基线可用，再做美化。
            ContextMenu menu = new ContextMenu();
            menu.FontFamily = Theme.Font;
            menu.FontSize = 12.5;
            return menu;
        }
        // ── 右键菜单 ──
        private void ShowTileMenu(FrameworkElement owner, string path)
        {
            _suppressClose = true;

            ContextMenu menu = MakeStyledMenu();

            MenuItem open = new MenuItem();
            open.Header = "打开";
            open.Click += delegate { Launch(path); };
            menu.Items.Add(open);

            MenuItem move = new MenuItem();
            move.Header = "移动到";
            string[] groups = Store.Groups();
            foreach (string g in groups)
            {
                string target = g;
                MenuItem mi = new MenuItem();
                mi.Header = g;
                mi.IsChecked = (g == _currentGroup);
                mi.Click += delegate
                {
                    Store.Import(path, target, false);
                    ReloadTiles();
                };
                move.Items.Add(mi);
            }
            if (groups.Length > 0) menu.Items.Add(move);

            MenuItem reveal = new MenuItem();
            reveal.Header = "在文件夹中显示";
            reveal.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch { }
            };
            menu.Items.Add(reveal);

            MenuItem toTop = new MenuItem();
            toTop.Header = "移到最前";
            toTop.Click += delegate
            {
                List<string> ord2 = Order.Get(_currentGroup);
                if (ord2.Count > 0) Order.MoveRelative(_currentGroup, Path.GetFileName(path), ord2[0], true);
                ReloadTiles();
            };
            menu.Items.Add(toTop);

            MenuItem resetSort = new MenuItem();
            resetSort.Header = "这一组恢复按名称排序";
            resetSort.Click += delegate
            {
                Order.Clear(_currentGroup);
                ReloadTiles();
            };
            menu.Items.Add(resetSort);

            menu.Items.Add(new Separator());

            MenuItem pull = new MenuItem();
            pull.Header = "从抽屉移出（放回桌面）";
            pull.Click += delegate { MoveBackToDesktop(path); };
            menu.Items.Add(pull);

            menu.Closed += delegate { _suppressClose = false; };
            menu.PlacementTarget = owner;
            menu.IsOpen = true;
        }

        private void MoveBackToDesktop(string path)
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                string dest = Path.Combine(desktop, name);
                int i = 2;
                while (File.Exists(dest) || Directory.Exists(dest))
                {
                    dest = Path.Combine(desktop,
                        Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
                    i++;
                }
                if (Directory.Exists(path)) Directory.Move(path, dest);
                else File.Move(path, dest);
                ReloadTiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show("移出失败：" + ex.Message, "Rack");
            }
        }

        // ── 导入 / 设置 ──
        private void ShowImport()
        {
            if (_currentGroup == null)
            {
                MessageBox.Show("还没有任何分类。\r\n先在设置里新建一个分类。", "Rack");
                return;
            }
            _suppressClose = true;
            try
            {
                ImportWindow dlg = new ImportWindow(_currentGroup);
                dlg.Owner = this;
                dlg.ShowDialog();
                if (dlg.Imported > 0) ReloadTiles();
            }
            finally { _suppressClose = false; }
        }

        private void ShowSettings()
        {
            _suppressClose = true;
            try
            {
                SettingsWindow sw = new SettingsWindow();
                sw.Owner = this;
                bool ok = sw.ShowDialog() == true;
                // 等设置窗口彻底关掉再刷新；以前在窗口还开着时刷，两个窗口抢重绘，
                // 会出现底部栏突然多出一块白底、关掉再开又正常
                if (ok)
                {
                    // 热键/开机自启这类东西归托盘层管，得通知它一下
                    if (SettingsSaved != null) SettingsSaved(this, EventArgs.Empty);
                    Dispatcher.BeginInvoke(new Action(delegate { RefreshAll(); }),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            finally { _suppressClose = false; }
        }

        internal void RefreshAll()
        {
            _root.Background = Theme.Veil(Cfg.Alpha);
            _root.BorderThickness = new Thickness(Cfg.Alpha > 0 ? 1 : 0);
            _tabBar.Background = System.Windows.Media.Brushes.Transparent;
            _footer.Background = System.Windows.Media.Brushes.Transparent;
            _dragBar.Background = System.Windows.Media.Brushes.Transparent;
            RebuildTabs(Store.Groups(), 0);
            string[] gs = Store.Groups();
            // 文字颜色/阴影可能变了，整棵树重建一次最省事
            _currentGroup = gs.Length > 0 ? gs[0] : null;
            ReloadTiles();
        }

        // ── 拖放接收 ──
        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                _dropHover = true;
                e.Effects = Cfg.KeepDesktop ? DragDropEffects.Copy : DragDropEffects.Move;
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            _dropHover = false;
            e.Handled = true;
            if (_currentGroup == null) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            bool keep = Cfg.KeepDesktop;
            int ok = 0;
            foreach (string f in files) if (Store.Import(f, _currentGroup, keep)) ok++;

            ReloadTiles();
            if (ok > 0)
                _hint.Text = "已收进「" + _currentGroup + "」" + ok.ToString() + " 项"
                             + (keep ? "（桌面原件保留）" : "");
        }

        // ── 自动关闭 ──
        private bool CanAutoClose
        {
            get { return _suppressClose == false && !_dropHover && !_pinned && !_moving; }
        }

        private void OnDeactivated(object sender, EventArgs e)
        {
            if (!_shown || !CanAutoClose) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (CanAutoClose && IsVisible) Close();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Icons.Clear();
            Nat.TrimMemory();
        }

        // ── 定位 ──
        internal void ShowAtTray()
        {
            ReloadTiles();
            ApplySavedOrDefaultPosition();
            Show();
            Activate();
        }

        private void ApplySavedOrDefaultPosition()
        {
            int px = Cfg.PosX, py = Cfg.PosY;
            bool restored = false;

            if (px != -999999 && py != -999999)
            {
                System.Windows.Rect probe = new System.Windows.Rect(px, py, Math.Max(60, Width), Math.Max(60, Height));
                foreach (WinForms.Screen sc in WinForms.Screen.AllScreens)
                {
                    double sc2 = DpiScale();
                    System.Windows.Rect sb = new System.Windows.Rect(
                        sc.Bounds.X / sc2, sc.Bounds.Y / sc2,
                        sc.Bounds.Width / sc2, sc.Bounds.Height / sc2);
                    System.Windows.Rect inter = System.Windows.Rect.Intersect(probe, sb);
                    if (inter.Width > 80 && inter.Height > 80) { restored = true; break; }
                }
            }

            if (restored) { Left = px; Top = py; return; }
            RepositionToTray();
        }

        private double DpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    double s = g.DpiX / 96.0;
                    return s <= 0 ? 1.0 : s;
                }
            }
            catch { return 1.0; }
        }

        private void RepositionToTray()
        {
            // 必须用鼠标的"物理屏幕坐标"来定屏幕；WPF 的 Mouse.GetPosition 是窗口内的 DIP，混用会把窗口丢到屏幕外
            WinForms.Screen scr = WinForms.Screen.FromPoint(WinForms.Cursor.Position);

            double s = DpiScale();
            System.Windows.Rect wa = new System.Windows.Rect(
                scr.WorkingArea.X / s, scr.WorkingArea.Y / s,
                scr.WorkingArea.Width / s, scr.WorkingArea.Height / s);

            double floor;
            int tb = Nat.TaskbarHeight();
            if (tb > 0) floor = (scr.Bounds.Bottom - tb) / s;
            else floor = wa.Bottom;

            Left = wa.Right - Width - 14;
            Top = floor - Height - 8;

            if (Left < wa.Left) Left = wa.Left;
            if (Top < wa.Top) Top = wa.Top;
        }

        internal void ResetPosition()
        {
            Cfg.SetInt("ui.posX", -999999);
            Cfg.SetInt("ui.posY", -999999);
            Cfg.Save();
            RepositionToTray();
        }
    }
}
