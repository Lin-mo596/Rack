// Rack (WPF 版) —— 设置窗口 + 导入对话框 + 文本输入框
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace RackWpf
{
    internal sealed class SettingsWindow : Window
    {
        internal event EventHandler Applied;

        private readonly StackPanel _body;
        private ListBox _groupList;
        private CheckBox _keepChk;
        private CheckBox _singleChk;
        private CheckBox _pinChk;
        private CheckBox _darkChk;
        private CheckBox _autoChk;
        private TextBox _hotkeyBox;
        private TextBlock _hotkeyNote;
        private string _textColor = "FFB6C1";
        private CheckBox _shadowChk;
        private Slider _alphaBar;
        private TextBlock _alphaVal;
        private Slider _menuAlphaBar;
        private Slider _winAlphaBar;
        private Border _shell;

        private uint _hkMods, _hkVk;

        internal SettingsWindow()
        {
            Title = "Rack 设置";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            // 去掉系统标题栏改为自绘。AllowsTransparency 是真半透明和圆角的前提：
            // 不开它，窗口底色永远不透明，圆角外面会露出方块。
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            Width = 560;
            Height = 740;
            FontFamily = Theme.Font;
            Foreground = Theme.Solid(Theme.Text);
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            System.Windows.Media.Color ln = Theme.Line;

            // 外壳：圆角 + 半透明底。窗口整块透明，所有观感都由这一层提供。
            _shell = new Border();
            _shell.CornerRadius = new CornerRadius(12);
            _shell.BorderThickness = new Thickness(1);
            _shell.BorderBrush = Theme.Solid(System.Windows.Media.Color.FromArgb(
                90, ln.R, ln.G, ln.B));
            ApplyShellAlpha(Cfg.WinAlpha);

            // 无边框窗口没有系统阴影，自己补一层，否则看着像贴在屏幕上的纸片
            System.Windows.Media.Effects.DropShadowEffect sh =
                new System.Windows.Media.Effects.DropShadowEffect();
            sh.BlurRadius = 16;
            sh.ShadowDepth = 2;
            sh.Opacity = 0.20;
            sh.Color = System.Windows.Media.Colors.Black;
            _shell.Effect = sh;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            root.Children.Add(BuildTitleBar(ln));

            _body = new StackPanel();
            _body.Margin = new Thickness(26, 6, 26, 10);

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Content = _body;
            sv.Background = System.Windows.Media.Brushes.Transparent;
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);

            // 底部按钮条不再铺不透明底色，只留一条分隔线 ——
            // 否则圆角处会露出一块直角方块
            Border foot = new Border();
            foot.Background = System.Windows.Media.Brushes.Transparent;
            foot.BorderThickness = new Thickness(0, 1, 0, 0);
            foot.BorderBrush = Theme.Solid(System.Windows.Media.Color.FromArgb(
                60, ln.R, ln.G, ln.B));
            Grid.SetRow(foot, 2);
            root.Children.Add(foot);

            StackPanel footSp = new StackPanel();
            footSp.Orientation = Orientation.Horizontal;
            footSp.HorizontalAlignment = HorizontalAlignment.Right;
            footSp.VerticalAlignment = VerticalAlignment.Center;
            footSp.Margin = new Thickness(0, 0, 22, 0);
            foot.Child = footSp;

            Border cancel = MakeButton("取消", 82, false);
            cancel.Margin = new Thickness(0, 0, 10, 0);
            cancel.MouseLeftButtonUp += delegate { Close(); };
            footSp.Children.Add(cancel);

            Border ok = MakeButton("保存", 96, true);
            ok.MouseLeftButtonUp += delegate { SaveAll(); };
            footSp.Children.Add(ok);

            _shell.Child = root;

            // 外面再套一层留边距，给阴影腾地方（紧贴窗口边缘会被裁掉）
            Grid outer = new Grid();
            outer.Margin = new Thickness(18);
            outer.Children.Add(_shell);
            Content = outer;

            try { BuildAll(); }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Store.Root, "settings_error.log"), ex.ToString()); } catch { }
                throw;
            }
        }

        /// <summary>把外壳底色按给定 alpha 刷新。滑块拖动时实时调用，所以不走 Cfg 缓存。</summary>
        private void ApplyShellAlpha(int alpha)
        {
            System.Windows.Media.Color c = Theme.Light
                ? System.Windows.Media.Color.FromRgb(247, 247, 251)
                : System.Windows.Media.Color.FromRgb(28, 28, 32);
            _shell.Background = Theme.Solid(System.Windows.Media.Color.FromArgb(
                (byte)alpha, c.R, c.G, c.B));
        }

        /// <summary>自绘标题栏：左边标题，右边关闭钮，整条可以拖动。</summary>
        private Grid BuildTitleBar(System.Windows.Media.Color ln)
        {
            Grid bar = new Grid();

            TextBlock ttl = new TextBlock();
            ttl.Text = "Rack 设置";
            ttl.FontSize = 14;
            ttl.FontWeight = FontWeights.SemiBold;
            ttl.VerticalAlignment = VerticalAlignment.Center;
            ttl.Margin = new Thickness(20, 0, 0, 0);
            bar.Children.Add(ttl);

            Border xb = new Border();
            xb.Width = 34;
            xb.Height = 30;
            xb.CornerRadius = new CornerRadius(7);
            xb.HorizontalAlignment = HorizontalAlignment.Right;
            xb.VerticalAlignment = VerticalAlignment.Center;
            xb.Margin = new Thickness(0, 0, 14, 0);
            xb.Background = System.Windows.Media.Brushes.Transparent;

            TextBlock xt = new TextBlock();
            xt.Text = "✕";
            xt.FontSize = 13;
            xt.HorizontalAlignment = HorizontalAlignment.Center;
            xt.VerticalAlignment = VerticalAlignment.Center;
            xt.Foreground = Theme.Solid(Theme.TextDim);
            xb.Child = xt;

            xb.MouseEnter += delegate { xb.Background = Theme.Solid(Theme.Hover); };
            xb.MouseLeave += delegate { xb.Background = System.Windows.Media.Brushes.Transparent; };
            xb.MouseLeftButtonUp += delegate { Close(); };
            bar.Children.Add(xb);

            // 没有系统标题栏，拖动只能自己实现；放在整条上所以点标题也能拖
            bar.MouseLeftButtonDown += delegate
            {
                try { DragMove(); } catch { }
            };
            return bar;
        }

        // ── 基础控件 ──
        private Border MakeButton(string text, double width, bool primary)
        {
            Border b = new Border();
            b.Width = width;
            b.Height = 34;
            b.CornerRadius = new CornerRadius(6);
            b.Cursor = Cursors.Hand;
            b.Background = primary
                ? Theme.Solid(Theme.Accent)
                : Theme.Solid(Theme.Light
                    ? System.Windows.Media.Color.FromRgb(232, 235, 242)
                    : System.Windows.Media.Color.FromRgb(52, 52, 58));

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12.5;
            t.FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal;
            t.Foreground = primary
                ? Theme.Solid(System.Windows.Media.Colors.White)
                : Theme.Solid(Theme.Text);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }

        private void SectionTitle(string text, double topMargin, double size, bool bold)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = size;
            t.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal;
            t.Foreground = Theme.Solid(Theme.Text);
            t.Margin = new Thickness(0, topMargin, 0, 8);
            _body.Children.Add(t);
        }

        private void Hint(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11.5;
            t.Foreground = Theme.Solid(Theme.TextDim);
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 6, 0, 0);
            _body.Children.Add(t);
        }

        /// <summary>
        /// 一行「说明 + 滑块 + 数字」。返回滑块本身，方便调用方读值或挂实时预览。
        /// </summary>
        private Slider AddAlphaSlider(string label, int initial)
        {
            TextBlock cap = new TextBlock();
            cap.Text = label;
            cap.FontSize = 11.5;
            cap.Foreground = Theme.Solid(Theme.TextDim);
            cap.TextWrapping = TextWrapping.Wrap;
            cap.Margin = new Thickness(0, 12, 0, 4);
            _body.Children.Add(cap);

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            Slider bar = new Slider();
            bar.Minimum = 0;
            bar.Maximum = 255;
            bar.Value = Math.Max(0, Math.Min(255, initial));
            bar.SmallChange = 5;
            bar.LargeChange = 20;
            bar.VerticalAlignment = VerticalAlignment.Center;
            // 点轨道直接跳到该点，比默认的"走一格"更符合直觉
            bar.IsMoveToPointEnabled = true;
            Grid.SetColumn(bar, 0);
            row.Children.Add(bar);

            TextBlock val = new TextBlock();
            val.Text = ((int)bar.Value).ToString();
            val.FontSize = 12.5;
            val.Foreground = Theme.Solid(Theme.Accent);
            val.VerticalAlignment = VerticalAlignment.Center;
            val.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(val, 1);
            row.Children.Add(val);

            bar.ValueChanged += delegate { val.Text = ((int)bar.Value).ToString(); };

            _body.Children.Add(row);
            return bar;
        }

        private CheckBox AddCheck(string text, bool value)
        {
            CheckBox c = new CheckBox();
            c.Content = text;
            c.IsChecked = value;
            c.FontSize = 12.5;
            c.Foreground = Theme.Solid(Theme.Text);
            c.Margin = new Thickness(0, 7, 0, 0);
            _body.Children.Add(c);
            return c;
        }

        private static System.Windows.Media.Color HexToColor(string hex)
        {
            try
            {
                string s = hex.Trim();
                if (s.StartsWith("#")) s = s.Substring(1);
                uint v = Convert.ToUInt32(s, 16);
                return System.Windows.Media.Color.FromRgb(
                    (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            }
            catch { return System.Windows.Media.Colors.White; }
        }
        private void HR()
        {
            Border line = new Border();
            line.Height = 1;
            line.Background = Theme.Solid(Theme.Line);
            line.Margin = new Thickness(0, 16, 0, 0);
            _body.Children.Add(line);
        }

        // ── 界面 ──
        private void BuildAll()
        {
            SectionTitle("设置", 0, 20, true);

            // 分类
            SectionTitle("分类", 16, 14, true);

            _groupList = new ListBox();
            _groupList.Height = 106;
            _groupList.FontSize = 12.5;
            _groupList.Background = Theme.Solid(Theme.Light
                ? System.Windows.Media.Colors.White
                : System.Windows.Media.Color.FromRgb(42, 42, 48));
            _groupList.Foreground = Theme.Solid(Theme.Text);
            _groupList.BorderBrush = Theme.Solid(Theme.Line);
            _groupList.BorderThickness = new Thickness(1);
            _body.Children.Add(_groupList);

            StackPanel gbtns = new StackPanel();
            gbtns.Orientation = Orientation.Horizontal;
            gbtns.Margin = new Thickness(0, 8, 0, 0);

            Border add = MakeButton("新建", 72, false);
            add.Margin = new Thickness(0, 0, 8, 0);
            add.MouseLeftButtonUp += delegate { AddGroup(); };
            gbtns.Children.Add(add);

            Border ren = MakeButton("重命名", 84, false);
            ren.Margin = new Thickness(0, 0, 8, 0);
            ren.MouseLeftButtonUp += delegate { RenameGroup(); };
            gbtns.Children.Add(ren);

            Border del = MakeButton("删除", 72, false);
            del.MouseLeftButtonUp += delegate { DeleteGroup(); };
            gbtns.Children.Add(del);

            _body.Children.Add(gbtns);
            Hint("分类就是 Dock 文件夹下的子文件夹，用资源管理器直接建也一样。");

            HR();

            // 拖进抽屉时
            SectionTitle("拖进抽屉时", 16, 14, true);
            _keepChk = AddCheck("保留桌面原件（相当于复制一份进来）", Cfg.KeepDesktop);
            Hint("不勾选 = 从桌面移走（默认，桌面才会变干净）。");

            // 点图标时
            SectionTitle("点图标时", 16, 14, true);
            _singleChk = AddCheck("左键单击就启动（不勾 = 双击启动，更不容易误触）", Cfg.SingleClickLaunch);
            SectionTitle("抽屉行为", 16, 14, true);
            _pinChk = AddCheck("打开时默认钉住（保持常开，点外面不关）", Cfg.PinByDefault);
            Hint("不勾选的话，点一下外面抽屉就自己收起 —— 打开一个软件就会自动关闭。");

            HR();

            // 快捷键
            SectionTitle("全局快捷键", 16, 14, true);
            Hint("点下面这个框，然后按下组合键（必须含 Ctrl / Alt / Shift / Win）。");

            _hotkeyBox = new TextBox();
            _hotkeyBox.Width = 220;
            _hotkeyBox.Height = 32;
            _hotkeyBox.FontSize = 12.5;
            _hotkeyBox.HorizontalAlignment = HorizontalAlignment.Left;
            _hotkeyBox.Margin = new Thickness(0, 8, 0, 0);
            _hotkeyBox.Padding = new Thickness(8, 4, 8, 4);
            _hotkeyBox.Background = Theme.Solid(Theme.Light
                ? System.Windows.Media.Colors.White
                : System.Windows.Media.Color.FromRgb(46, 46, 52));
            _hotkeyBox.Foreground = Theme.Solid(Theme.Text);
            _hotkeyBox.BorderBrush = Theme.Solid(Theme.Line);
            _hotkeyBox.IsReadOnly = true;
            _hotkeyBox.PreviewKeyDown += HotkeyKeyDown;
            _body.Children.Add(_hotkeyBox);

            _hotkeyNote = new TextBlock();
            _hotkeyNote.Text = "";
            _hotkeyNote.FontSize = 11.5;
            _hotkeyNote.Foreground = Theme.Solid(Theme.TextDim);
            _hotkeyNote.Margin = new Thickness(0, 6, 0, 0);
            _hotkeyNote.TextWrapping = TextWrapping.Wrap;
            _body.Children.Add(_hotkeyNote);

            HR();

            // 外观
            SectionTitle("外观", 16, 14, true);
            _darkChk = AddCheck("深色主题（不勾选就是白色）", !Cfg.LightTheme);

            TextBlock ct = new TextBlock();
            ct.Text = "文字颜色（抽屉里的字）";
            ct.FontSize = 11.5;
            ct.Foreground = Theme.Solid(Theme.TextDim);
            ct.Margin = new Thickness(0, 14, 0, 4);
            _body.Children.Add(ct);

            StackPanel colorRow = new StackPanel();
            colorRow.Orientation = Orientation.Horizontal;

            string[] palette = { "FFB6C1", "FFFFFF", "A8D8FF", "FFE9A8", "2A2C32" };
            Border[] swatches = new Border[palette.Length];
            _textColor = Cfg.TextColor;

            for (int i = 0; i < palette.Length; i++)
            {
                string hex = palette[i];
                Border sw = new Border();
                sw.Width = 42;
                sw.Height = 26;
                sw.CornerRadius = new CornerRadius(5);
                sw.Margin = new Thickness(0, 0, 8, 0);
                sw.Cursor = Cursors.Hand;
                sw.Background = Theme.Solid(HexToColor(hex));
                sw.BorderThickness = new Thickness(2);
                sw.BorderBrush = string.Equals(hex, _textColor, StringComparison.OrdinalIgnoreCase)
                    ? Theme.Solid(Theme.Accent) : System.Windows.Media.Brushes.Transparent;
                swatches[i] = sw;

                sw.MouseLeftButtonUp += delegate
                {
                    _textColor = hex;
                    for (int j = 0; j < swatches.Length; j++)
                        swatches[j].BorderBrush = string.Equals(palette[j], hex, StringComparison.OrdinalIgnoreCase)
                            ? Theme.Solid(Theme.Accent) : System.Windows.Media.Brushes.Transparent;
                };
                colorRow.Children.Add(sw);
            }
            _body.Children.Add(colorRow);

            _shadowChk = AddCheck("文字带一层淡投影（纯透明背景上更好认）", Cfg.TextShadow);

            TextBlock at = new TextBlock();
            at.Text = "那层纱的浓淡（0 = 完全透明，只剩图标和文字）";
            at.FontSize = 11.5;
            at.Foreground = Theme.Solid(Theme.TextDim);
            at.Margin = new Thickness(0, 12, 0, 4);
            _body.Children.Add(at);

            Grid alphaRow = new Grid();
            alphaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            alphaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            _alphaBar = new Slider();
            _alphaBar.Minimum = 0;
            _alphaBar.Maximum = 255;
            _alphaBar.Value = Math.Max(0, Math.Min(255, Cfg.Alpha));
            _alphaBar.SmallChange = 5;
            _alphaBar.LargeChange = 20;
            _alphaBar.VerticalAlignment = VerticalAlignment.Center;
            // 默认点击轨道是"走一格"，用户会以为点错了位置就跳回 0；改成点哪到哪
            _alphaBar.IsMoveToPointEnabled = true;
            _alphaBar.ValueChanged += delegate
            {
                if (_alphaVal != null) _alphaVal.Text = ((int)_alphaBar.Value).ToString();
            };
            Grid.SetColumn(_alphaBar, 0);
            alphaRow.Children.Add(_alphaBar);

            _alphaVal = new TextBlock();
            _alphaVal.Text = ((int)_alphaBar.Value).ToString();
            _alphaVal.FontSize = 12.5;
            _alphaVal.Foreground = Theme.Solid(Theme.Accent);
            _alphaVal.VerticalAlignment = VerticalAlignment.Center;
            _alphaVal.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_alphaVal, 1);
            alphaRow.Children.Add(_alphaVal);

            _body.Children.Add(alphaRow);

            StackPanel presets = new StackPanel();
            presets.Orientation = Orientation.Horizontal;
            presets.Margin = new Thickness(0, 8, 0, 0);

            int[] presetVals = { 0, 60, 120, 200 };
            string[] presetNames = { "全透明", "淡", "中", "实" };
            for (int pi = 0; pi < presetVals.Length; pi++)
            {
                int pv = presetVals[pi];
                Border pb = MakeButton(presetNames[pi], 62, false);
                pb.Height = 28;
                pb.Margin = new Thickness(0, 0, 8, 0);
                pb.MouseLeftButtonUp += delegate { _alphaBar.Value = pv; };
                presets.Children.Add(pb);
            }
            _body.Children.Add(presets);

            // ── 菜单 / 设置窗口各自的透明度 ──
            _menuAlphaBar = AddAlphaSlider(
                "右键菜单底色的不透明度（0 = 全透明，255 = 完全不透明）", Cfg.MenuAlpha);
            _winAlphaBar = AddAlphaSlider(
                "这个设置窗口底色的不透明度", Cfg.WinAlpha);
            _winAlphaBar.ValueChanged += delegate
            {
                ApplyShellAlpha((int)_winAlphaBar.Value);
            };
            Hint("设置窗口这一条拖动时就变；菜单那一条保存后，下次右键就生效，不用重启。");

            Border resetPos = MakeButton("重置抽屉位置", 122, false);
            resetPos.HorizontalAlignment = HorizontalAlignment.Left;
            resetPos.Margin = new Thickness(0, 12, 0, 0);
            resetPos.MouseLeftButtonUp += delegate
            {
                Cfg.SetInt("ui.posX", -999999);
                Cfg.SetInt("ui.posY", -999999);
                Cfg.Save();
                MessageBox.Show("已重置。下次打开抽屉会出现在右下角。", "Rack");
            };
            _body.Children.Add(resetPos);

            HR();

            // 启动
            SectionTitle("启动", 16, 14, true);
            _autoChk = AddCheck("开机自动启动（注册表 Run 项，随时可关）", TrayApp.ReadAutoStart());

            LoadValues();
        }

        private void LoadValues()
        {
            _groupList.Items.Clear();
            foreach (string g in Store.Groups()) _groupList.Items.Add(g);

            uint mods, vk;
            if (HotkeyText.Parse(Cfg.HotkeyText, out mods, out vk))
            {
                _hkMods = mods;
                _hkVk = vk;
                _hotkeyBox.Text = HotkeyText.Format(mods, vk);
            }
            _hotkeyNote.Text = "保存后立即生效。若组合被别的软件占用，会注册失败。";
        }

        private void HotkeyKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            if (e.Key == Key.Back || e.Key == Key.Delete)
            {
                _hkMods = 0; _hkVk = 0; _hotkeyBox.Text = "";
                return;
            }
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl
                || e.Key == Key.LeftAlt || e.Key == Key.RightAlt
                || e.Key == Key.LeftShift || e.Key == Key.RightShift
                || e.Key == Key.LWin || e.Key == Key.RWin) return;

            // 状态键/功能键不能当热键，否则会存成 Ctrl+Capital（CapsLock）这种看不懂的东西
            if (e.Key == Key.CapsLock || e.Key == Key.NumLock || e.Key == Key.Scroll
                || e.Key == Key.System || e.Key == Key.None
                || e.Key == Key.PrintScreen || e.Key == Key.Pause
                || e.Key == Key.Apps || e.Key == Key.Sleep) return;

            uint mods = 0;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= Nat.MOD_CTRL;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= Nat.MOD_ALT;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= Nat.MOD_SHIFT;

            if (mods == 0)
            {
                _hotkeyNote.Text = "至少要带一个 Ctrl / Alt / Shift，否则会抢掉正常打字。";
                return;
            }

            WinForms.Keys k = (WinForms.Keys)KeyInterop.VirtualKeyFromKey(e.Key);
            if (k == WinForms.Keys.None) return;

            _hkMods = mods;
            _hkVk = (uint)k;
            _hotkeyBox.Text = HotkeyText.Format(mods, _hkVk);
            _hotkeyNote.Text = "保存后立即生效。";
        }

        private void AddGroup()
        {
            string name = TextPromptWindow.Ask(this, "新建分类", "分类名（会变成 Dock 下的一个文件夹）：", "");
            if (name == null) return;
            if (!Store.CreateGroup(name))
            {
                MessageBox.Show("建不了。可能同名已存在，或名字含 \\ / : * ? \" < > |");
                return;
            }
            LoadValues();
        }

        private void RenameGroup()
        {
            string cur = _groupList.SelectedItem as string;
            if (cur == null) { MessageBox.Show("先在列表里选一个分类。"); return; }
            string name = TextPromptWindow.Ask(this, "重命名分类", "新名字：", cur);
            if (name == null || name == cur) return;
            if (!Store.RenameGroup(cur, name)) { MessageBox.Show("改名失败（重名或有非法字符）。"); return; }
            LoadValues();
        }

        private void DeleteGroup()
        {
            string cur = _groupList.SelectedItem as string;
            if (cur == null) { MessageBox.Show("先在列表里选一个分类。"); return; }
            int count = 0;
            try { count = Directory.GetFileSystemEntries(Store.DirFor(cur)).Length; } catch { }
            if (count > 0)
            {
                MessageBox.Show("「" + cur + "」里还有 " + count + " 个东西，不能直接删。\r\n先把里面的图标移走。");
                return;
            }
            if (MessageBox.Show("确定删除空分类「" + cur + "」？（不可撤销）", "Rack",
                    MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            if (!Store.DeleteGroup(cur)) MessageBox.Show("删除失败，可能被占用。");
            else LoadValues();
        }

        private void SaveAll()
        {
            Cfg.SetBool("beh.keepDesktop", _keepChk.IsChecked == true);
            Cfg.SetBool("beh.singleClick", _singleChk.IsChecked == true);
            Cfg.SetBool("beh.pinByDefault", _pinChk.IsChecked == true);
            Cfg.Set("ui.theme", _darkChk.IsChecked == true ? "dark" : "light");
            Cfg.SetInt("ui.alpha", (int)_alphaBar.Value);
            Cfg.SetInt("ui.menuAlpha", (int)_menuAlphaBar.Value);
            Cfg.SetInt("ui.winAlpha", (int)_winAlphaBar.Value);
            Cfg.Set("ui.textColor", _textColor);
            Cfg.SetBool("ui.textShadow", _shadowChk.IsChecked == true);

            if (_hkMods != 0 && _hkVk != 0) Cfg.Set("beh.hotkey", HotkeyText.Format(_hkMods, _hkVk));
            else Cfg.Set("beh.hotkey", "");

            TrayApp.WriteAutoStart(_autoChk.IsChecked == true);

            if (Store.Groups().Length == 0) Store.EnsureInit();
            Cfg.Save();

            // 用 DialogResult 关闭，让调用方在窗口消失之后再刷新抽屉
            DialogResult = true;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  导入对话框
    // ══════════════════════════════════════════════════════════════
    internal sealed class ImportWindow : Window
    {
        internal int Imported;

        private readonly List<string> _paths = new List<string>();
        private readonly List<CheckBox> _checks = new List<CheckBox>();
        private readonly string _group;

        internal ImportWindow(string group)
        {
            _group = group;
            Title = "把桌面上的东西收进「" + group + "」";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Width = 520;
            Height = 620;
            FontFamily = Theme.Font;
            Foreground = Theme.Solid(Theme.Text);
            Background = Theme.Solid(Theme.Light
                ? System.Windows.Media.Color.FromRgb(247, 247, 251)
                : System.Windows.Media.Color.FromRgb(28, 28, 32));
            SnapsToDevicePixels = true;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });

            TextBlock tip = new TextBlock();
            tip.Text = Cfg.KeepDesktop
                ? "勾选要收起来的（当前设置：保留桌面原件，相当于复制一份）。"
                : "勾选要收起来的，它们会从桌面移到抽屉里（可以随时拖回去）。";
            tip.FontSize = 12;
            tip.Foreground = Theme.Solid(Theme.TextDim);
            tip.VerticalAlignment = VerticalAlignment.Center;
            tip.Margin = new Thickness(20, 0, 20, 0);
            Grid.SetRow(tip, 0);
            root.Children.Add(tip);

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            StackPanel list = new StackPanel();
            list.Margin = new Thickness(20, 0, 20, 10);
            sv.Content = list;
            Grid.SetRow(sv, 1);
            root.Children.Add(sv);

            List<string> all = Store.CollectDesktopItems();
            all.Sort(delegate(string a, string b)
            {
                return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            });
            foreach (string p in all)
            {
                _paths.Add(p);
                string name = Path.GetFileNameWithoutExtension(p);
                if (Directory.Exists(p)) name = "[文件夹] " + Path.GetFileName(p) + "\\";

                CheckBox c = new CheckBox();
                c.Content = name;
                c.FontSize = 12.5;
                c.Foreground = Theme.Solid(Theme.Text);
                c.Margin = new Thickness(0, 4, 0, 4);
                _checks.Add(c);
                list.Children.Add(c);
            }

            Border foot = new Border();
            foot.Background = Theme.Solid(Theme.Light
                ? System.Windows.Media.Color.FromRgb(238, 240, 246)
                : System.Windows.Media.Color.FromRgb(22, 22, 26));
            Grid.SetRow(foot, 2);
            root.Children.Add(foot);

            Grid fg = new Grid();
            fg.Margin = new Thickness(20, 0, 20, 0);
            foot.Child = fg;

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;
            left.HorizontalAlignment = HorizontalAlignment.Left;
            fg.Children.Add(left);

            Border allOn = MakeSmall("全选", 70);
            allOn.Margin = new Thickness(0, 0, 8, 0);
            allOn.MouseLeftButtonUp += delegate { SetAll(true); };
            left.Children.Add(allOn);

            Border allOff = MakeSmall("全不选", 78);
            allOff.MouseLeftButtonUp += delegate { SetAll(false); };
            left.Children.Add(allOff);

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            fg.Children.Add(right);

            Border cancel = MakeSmall("取消", 80);
            cancel.Margin = new Thickness(0, 0, 10, 0);
            cancel.MouseLeftButtonUp += delegate { Close(); };
            right.Children.Add(cancel);

            Border ok = new Border();
            ok.Width = 104;
            ok.Height = 34;
            ok.CornerRadius = new CornerRadius(6);
            ok.Cursor = Cursors.Hand;
            ok.Background = Theme.Solid(Theme.Accent);
            TextBlock okt = new TextBlock();
            okt.Text = "收进抽屉";
            okt.FontSize = 12.5;
            okt.FontWeight = FontWeights.SemiBold;
            okt.Foreground = Theme.Solid(System.Windows.Media.Colors.White);
            okt.HorizontalAlignment = HorizontalAlignment.Center;
            okt.VerticalAlignment = VerticalAlignment.Center;
            ok.Child = okt;
            ok.MouseLeftButtonUp += delegate { DoImport(); };
            right.Children.Add(ok);

            Content = root;
        }

        private Border MakeSmall(string text, double width)
        {
            Border b = new Border();
            b.Width = width;
            b.Height = 34;
            b.CornerRadius = new CornerRadius(6);
            b.Cursor = Cursors.Hand;
            b.Background = Theme.Solid(Theme.Light
                ? System.Windows.Media.Color.FromRgb(232, 235, 242)
                : System.Windows.Media.Color.FromRgb(52, 52, 58));

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12.5;
            t.Foreground = Theme.Solid(Theme.Text);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }

        private void SetAll(bool on)
        {
            foreach (CheckBox c in _checks) c.IsChecked = on;
        }

        private void DoImport()
        {
            bool keep = Cfg.KeepDesktop;
            int ok = 0;
            for (int i = 0; i < _paths.Count && i < _checks.Count; i++)
                if (_checks[i].IsChecked == true && Store.Import(_paths[i], _group, keep)) ok++;
            Imported = ok;
            Close();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  文本输入框
    // ══════════════════════════════════════════════════════════════
    internal sealed class TextPromptWindow : Window
    {
        private readonly TextBox _box;

        private TextPromptWindow(string title, string tip, string initial)
        {
            Title = title;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Width = 400;
            Height = 196;
            FontFamily = Theme.Font;
            Foreground = Theme.Solid(Theme.Text);
            Background = Theme.Solid(Theme.Light
                ? System.Windows.Media.Color.FromRgb(247, 247, 251)
                : System.Windows.Media.Color.FromRgb(28, 28, 32));
            SnapsToDevicePixels = true;

            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(20, 16, 20, 16);

            TextBlock t = new TextBlock();
            t.Text = tip;
            t.FontSize = 12;
            t.Foreground = Theme.Solid(Theme.TextDim);
            t.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(t);

            _box = new TextBox();
            _box.Text = initial ?? "";
            _box.FontSize = 13;
            _box.Height = 32;
            _box.Padding = new Thickness(8, 4, 8, 4);
            _box.Margin = new Thickness(0, 10, 0, 0);
            _box.Background = Theme.Solid(Theme.Light
                ? System.Windows.Media.Colors.White
                : System.Windows.Media.Color.FromRgb(46, 46, 52));
            _box.Foreground = Theme.Solid(Theme.Text);
            _box.BorderBrush = Theme.Solid(Theme.Line);
            sp.Children.Add(_box);

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, 16, 0, 0);

            Border cancel = SmallBorder("取消", 80, false);
            cancel.Margin = new Thickness(0, 0, 10, 0);
            cancel.MouseLeftButtonUp += delegate { DialogResult = false; Close(); };
            row.Children.Add(cancel);

            Border ok = SmallBorder("确定", 80, true);
            ok.MouseLeftButtonUp += delegate { DialogResult = true; Close(); };
            row.Children.Add(ok);

            sp.Children.Add(row);
            Content = sp;

            Loaded += delegate { _box.Focus(); _box.SelectAll(); };
            _box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { DialogResult = true; Close(); }
                else if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            };
        }

        private Border SmallBorder(string text, double width, bool primary)
        {
            Border b = new Border();
            b.Width = width;
            b.Height = 32;
            b.CornerRadius = new CornerRadius(6);
            b.Cursor = Cursors.Hand;
            b.Background = primary
                ? Theme.Solid(Theme.Accent)
                : Theme.Solid(Theme.Light
                    ? System.Windows.Media.Color.FromRgb(232, 235, 242)
                    : System.Windows.Media.Color.FromRgb(52, 52, 58));

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12.5;
            t.FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal;
            t.Foreground = primary
                ? Theme.Solid(System.Windows.Media.Colors.White)
                : Theme.Solid(Theme.Text);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }

        internal static string Ask(Window owner, string title, string tip, string initial)
        {
            TextPromptWindow w = new TextPromptWindow(title, tip, initial);
            w.Owner = owner;
            bool? r = w.ShowDialog();
            if (r != true) return null;
            string v = w._box.Text.Trim();
            return v.Length == 0 ? null : v;
        }
    }
}
