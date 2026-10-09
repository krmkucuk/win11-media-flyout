using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MediaFlyout
{
    internal sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly Settings _settings;
        private readonly Action _showPreview;
        private readonly Action _settingsChanged;
        private readonly Action _exit;

        public TrayIcon(Settings settings, Action showPreview, Action settingsChanged, Action exit)
        {
            _settings = settings;
            _showPreview = showPreview;
            _settingsChanged = settingsChanged;
            _exit = exit;

            _menu = new ContextMenuStrip
            {
                Font = new Font("Segoe UI", 9f),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Padding = new Padding(2, 4, 2, 4),
            };
            _menu.Opening += (_, _) => _menu.Renderer = new MenuRenderer(IsDark());
            BuildMenu();

            _icon = new NotifyIcon
            {
                Text = "Media Flyout",
                Icon = LoadIcon(),
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) showPreview();
            };
        }

        private void BuildMenu()
        {
            var settings = _settings;
            var showPreview = _showPreview;
            var settingsChanged = _settingsChanged;

            foreach (ToolStripItem old in _menu.Items) old.Dispose();
            _menu.Items.Clear();

            _menu.Items.Add(Item(Strings.Preview, (_, _) => showPreview()));
            _menu.Items.Add(new ToolStripSeparator());

            var trackChange = Item(Strings.ShowOnTrackChange);
            trackChange.CheckOnClick = true;
            trackChange.Checked = settings.ShowOnTrackChange;
            trackChange.CheckedChanged += (_, _) =>
            {
                settings.ShowOnTrackChange = trackChange.Checked;
                settings.Save();
            };
            _menu.Items.Add(trackChange);

            var position = Item(Strings.Position);
            AddChoices(position, settings.Position,
                new[] { (Strings.TopLeft, FlyoutPosition.TopLeft), (Strings.TopCenter, FlyoutPosition.TopCenter), (Strings.TopRight, FlyoutPosition.TopRight) },
                value =>
                {
                    settings.Position = value;
                    settings.Save();
                    settingsChanged();
                    showPreview();
                });
            _menu.Items.Add(position);

            var duration = Item(Strings.DisplayDuration);
            AddChoices(duration, settings.HideDelayMs,
                new[] { (Strings.Seconds(2), 2000), (Strings.Seconds(3), 3000), (Strings.Seconds(5), 5000), (Strings.Seconds(8), 8000) },
                value =>
                {
                    settings.HideDelayMs = value;
                    settings.Save();
                    settingsChanged();
                });
            _menu.Items.Add(duration);

            var language = Item(Strings.Language);
            AddChoices(language, settings.Language,
                new[] { (Strings.SystemLanguage, AppLanguage.Auto), ("Türkçe", AppLanguage.Turkish), ("English", AppLanguage.English) },
                value =>
                {
                    settings.Language = value;
                    settings.Save();
                    Strings.Apply(value);
                    Services.AppInfo.ClearCache();
                    settingsChanged();
                    // Rebuild after the click has finished being processed.
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(BuildMenu));
                });
            _menu.Items.Add(language);

            var startup = Item(Strings.StartWithWindows);
            startup.Checked = Settings.StartWithWindows;
            startup.Click += (_, _) =>
            {
                try
                {
                    Settings.StartWithWindows = !startup.Checked;
                }
                catch { }
                startup.Checked = Settings.StartWithWindows;
            };
            _menu.Items.Add(startup);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(Item(Strings.Exit, (_, _) => _exit()));
        }

        private static ToolStripMenuItem Item(string text, EventHandler? onClick = null)
        {
            var item = new ToolStripMenuItem(text) { Padding = new Padding(4, 5, 4, 5) };
            if (onClick != null) item.Click += onClick;
            return item;
        }

        private static void AddChoices<T>(ToolStripMenuItem parent, T current, (string Text, T Value)[] choices, Action<T> onSelect)
        {
            parent.DropDown.Padding = new Padding(2, 4, 2, 4);
            if (parent.DropDown is ToolStripDropDownMenu dd)
            {
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
            }
            parent.DropDownOpening += (_, _) => parent.DropDown.Renderer = new MenuRenderer(IsDark());

            foreach (var (text, value) in choices)
            {
                var item = Item(text);
                item.Checked = Equals(value, current);
                item.Click += (_, _) =>
                {
                    foreach (ToolStripItem other in parent.DropDownItems)
                        if (other is ToolStripMenuItem mi) mi.Checked = ReferenceEquals(mi, item);
                    onSelect(value);
                };
                parent.DropDownItems.Add(item);
            }
        }

        private static bool IsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return !(key?.GetValue("SystemUsesLightTheme") is int v && v != 0);
            }
            catch
            {
                return true;
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
                if (info != null)
                {
                    using var stream = info.Stream;
                    return new Icon(stream, SystemInformation.SmallIconSize);
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }

        /// <summary>Windows 11-ish flat menu that follows the system theme.</summary>
        private sealed class MenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly bool _dark;
            private Color Text => _dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26);

            public MenuRenderer(bool dark) : base(new Palette(dark))
            {
                _dark = dark;
                RoundedEdges = false;
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                var rect = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
                using var brush = new SolidBrush(_dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(235, 235, 235));
                using var path = Rounded(rect, 4);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Text;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                using var font = new Font("Segoe Fluent Icons", 9f);
                TextRenderer.DrawText(e.Graphics, "", font, e.ImageRectangle, Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using var pen = new Pen(_dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(225, 225, 225));
                e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
            }

            private static GraphicsPath Rounded(Rectangle r, int radius)
            {
                int d = radius * 2;
                var path = new GraphicsPath();
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        private sealed class Palette : ProfessionalColorTable
        {
            private readonly Color _background;
            private readonly Color _border;

            public Palette(bool dark)
            {
                _background = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
                _border = dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(220, 220, 220);
                UseSystemColors = false;
            }

            public override Color ToolStripDropDownBackground => _background;
            public override Color ImageMarginGradientBegin => _background;
            public override Color ImageMarginGradientMiddle => _background;
            public override Color ImageMarginGradientEnd => _background;
            public override Color MenuBorder => _border;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Color.Transparent;
            public override Color CheckBackground => Color.Transparent;
            public override Color CheckSelectedBackground => Color.Transparent;
            public override Color CheckPressedBackground => Color.Transparent;
        }
    }
}
