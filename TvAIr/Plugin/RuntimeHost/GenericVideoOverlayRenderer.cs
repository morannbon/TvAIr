using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TvAIrPlugin.Overlay;

namespace TvAIr.Plugin.RuntimeHost;

internal sealed record VideoOverlayViewerTarget(string ViewerSessionId, long Generation, int ProcessId);

/// <summary>
/// Host-owned renderer for generic video overlay scenes.
/// ViewerSession remains the source of truth; this class only projects the scene onto
/// the visible video area owned by the session process.
/// </summary>
internal sealed class GenericVideoOverlayRenderer : IDisposable
{
    private readonly BlockingCollection<Action> _commands = new();
    private readonly Dictionary<string, OverlayWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread _thread;
    private int _disposed;

    public GenericVideoOverlayRenderer()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "TvAIr.VideoOverlayRenderer"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Update(VideoOverlayViewerTarget target, VideoOverlaySceneState scene, IReadOnlyList<VideoOverlayLayerState> layers)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Enqueue(() =>
        {
            if (!_windows.TryGetValue(scene.SceneInstanceId, out var window))
            {
                window = new OverlayWindow(target);
                _windows[scene.SceneInstanceId] = window;
                window.Show();
            }
            window.UpdateTarget(target);
            window.UpdateScene(scene, layers);
        });
    }

    public void Close(string sceneInstanceId)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Enqueue(() =>
        {
            if (!_windows.Remove(sceneInstanceId, out var window)) return;
            window.Close();
            window.Dispose();
        });
    }

    private void Enqueue(Action action)
    {
        try { _commands.Add(action); }
        catch (InvalidOperationException) { }
    }

    private void Run()
    {
        using var timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += (_, _) =>
        {
            while (_commands.TryTake(out var action))
            {
                try { action(); }
                catch { }
            }
            foreach (var window in _windows.Values)
                window.RefreshTargetAndContent();
        };
        timer.Start();
        Application.Run();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Enqueue(() =>
        {
            foreach (var window in _windows.Values.ToArray())
            {
                window.Close();
                window.Dispose();
            }
            _windows.Clear();
            Application.ExitThread();
        });
        _commands.CompleteAdding();
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        _commands.Dispose();
    }

    private sealed class OverlayWindow : Form
    {
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private static readonly Color TransparentColor = Color.FromArgb(1, 2, 3);

        private readonly Dictionary<string, DateTime?> _expiresAt = new(StringComparer.Ordinal);
        // Drawing resources are owned by this window and bounded to styles used by the current scene.
        // Never create/dispose Font/Brush/StringFormat per Paint; reconcile them only when scene content changes or expires.
        private readonly Dictionary<float, Font> _fonts = new();
        private readonly Dictionary<int, SolidBrush> _foregroundBrushes = new();
        private readonly Dictionary<TextFormatKey, StringFormat> _formats = new();
        private readonly Dictionary<string, TextRenderStyle> _renderStyles = new(StringComparer.Ordinal);
        private readonly HashSet<float> _neededFontSizes = new();
        private readonly HashSet<int> _neededForegroundColors = new();
        private readonly HashSet<TextFormatKey> _neededFormats = new();
        private readonly SolidBrush _shadowBrush = new(Color.FromArgb(210, Color.Black));
        private VideoOverlayViewerTarget _target;
        private VideoOverlaySceneState? _scene;
        private IReadOnlyList<VideoOverlayLayerState> _layers = Array.Empty<VideoOverlayLayerState>();
        private Rectangle _lastBounds = Rectangle.Empty;
        private DateTime _nextWindowLookup = DateTime.MinValue;
        private DateTime? _nextExpirationUtc;
        private IntPtr _targetWindow;

        public OverlayWindow(VideoOverlayViewerTarget target)
        {
            _target = target;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = TransparentColor;
            TransparencyKey = TransparentColor;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            Enabled = false;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
                return cp;
            }
        }

        public void UpdateTarget(VideoOverlayViewerTarget target)
        {
            if (_target.ProcessId != target.ProcessId || _target.Generation != target.Generation)
            {
                _target = target;
                _targetWindow = IntPtr.Zero;
                _nextWindowLookup = DateTime.MinValue;
            }
        }

        public void UpdateScene(VideoOverlaySceneState scene, IReadOnlyList<VideoOverlayLayerState> layers)
        {
            _scene = scene;
            _layers = layers ?? Array.Empty<VideoOverlayLayerState>();
            var now = DateTime.UtcNow;
            var activeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in _layers.SelectMany(x => x.Elements))
            {
                activeIds.Add(element.ElementId);
                if (!_expiresAt.ContainsKey(element.ElementId))
                    _expiresAt[element.ElementId] = element is VideoOverlayTextElement { Duration: { } duration } && duration > TimeSpan.Zero
                        ? now + duration
                        : null;
            }
            foreach (var stale in _expiresAt.Keys.Where(x => !activeIds.Contains(x)).ToArray())
                _expiresAt.Remove(stale);
            RebuildRenderResources(now);
            UpdateNextExpiration(now);
            Invalidate();
        }

        public void RefreshTargetAndContent()
        {
            if (_scene is null || _scene.IsClosed)
            {
                if (Visible) Hide();
                return;
            }

            if (DateTime.UtcNow >= _nextWindowLookup)
            {
                _targetWindow = FindBestVideoWindow(_target.ProcessId);
                _nextWindowLookup = DateTime.UtcNow.AddMilliseconds(_targetWindow == IntPtr.Zero ? 500 : 200);
            }

            if (_targetWindow == IntPtr.Zero || !TryGetScreenBounds(_targetWindow, out var bounds) || bounds.Width < 2 || bounds.Height < 2)
            {
                if (Visible) Hide();
                return;
            }

            if (_lastBounds != bounds)
            {
                _lastBounds = bounds;
                SetBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height, BoundsSpecified.All);
                Invalidate();
            }
            if (!Visible) Show();

            var now = DateTime.UtcNow;
            if (_nextExpirationUtc is { } nextExpiration && now >= nextExpiration)
            {
                RebuildRenderResources(now);
                UpdateNextExpiration(now);
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var now = DateTime.UtcNow;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            foreach (var element in _layers.SelectMany(x => x.Elements).OfType<VideoOverlayTextElement>())
            {
                if (_expiresAt.TryGetValue(element.ElementId, out var expires) && expires.HasValue && expires.Value <= now)
                    continue;
                if (!_renderStyles.TryGetValue(element.ElementId, out var style))
                    continue;
                DrawText(e.Graphics, ClientRectangle, element, style);
            }
        }

        private void RebuildRenderResources(DateTime now)
        {
            _neededFontSizes.Clear();
            _neededForegroundColors.Clear();
            _neededFormats.Clear();
            _renderStyles.Clear();

            foreach (var element in _layers.SelectMany(x => x.Elements).OfType<VideoOverlayTextElement>())
            {
                if (_expiresAt.TryGetValue(element.ElementId, out var expires) && expires.HasValue && expires.Value <= now)
                    continue;

                var size = (float)Math.Clamp(element.FontSize, 8d, 160d);
                var argb = ParseColor(element.Color).ToArgb();
                var formatKey = ResolveFormatKey(element.Placement);
                _neededFontSizes.Add(size);
                _neededForegroundColors.Add(argb);
                _neededFormats.Add(formatKey);
            }

            DisposeUnused(_fonts, _neededFontSizes);
            DisposeUnused(_foregroundBrushes, _neededForegroundColors);
            DisposeUnused(_formats, _neededFormats);

            foreach (var size in _neededFontSizes)
            {
                if (_fonts.ContainsKey(size)) continue;
                var familyName = SystemFonts.MessageBoxFont?.FontFamily.Name ?? FontFamily.GenericSansSerif.Name;
                _fonts[size] = new Font(familyName, size, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            foreach (var argb in _neededForegroundColors)
            {
                if (!_foregroundBrushes.ContainsKey(argb))
                    _foregroundBrushes[argb] = new SolidBrush(Color.FromArgb(argb));
            }
            foreach (var key in _neededFormats)
            {
                if (_formats.ContainsKey(key)) continue;
                _formats[key] = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = key.Alignment,
                    LineAlignment = key.LineAlignment,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
            }

            foreach (var element in _layers.SelectMany(x => x.Elements).OfType<VideoOverlayTextElement>())
            {
                if (_expiresAt.TryGetValue(element.ElementId, out var expires) && expires.HasValue && expires.Value <= now)
                    continue;

                var size = (float)Math.Clamp(element.FontSize, 8d, 160d);
                var argb = ParseColor(element.Color).ToArgb();
                var formatKey = ResolveFormatKey(element.Placement);
                _renderStyles[element.ElementId] = new TextRenderStyle(
                    _fonts[size],
                    _foregroundBrushes[argb],
                    _formats[formatKey],
                    size);
            }
        }

        private void UpdateNextExpiration(DateTime now)
        {
            _nextExpirationUtc = _expiresAt.Values
                .Where(x => x.HasValue && x.Value > now)
                .Select(x => x!.Value)
                .DefaultIfEmpty()
                .Min();
            if (_nextExpirationUtc == default) _nextExpirationUtc = null;
        }

        private static void DisposeUnused<TKey, TValue>(Dictionary<TKey, TValue> resources, HashSet<TKey> needed)
            where TKey : notnull
            where TValue : IDisposable
        {
            foreach (var key in resources.Keys.Where(x => !needed.Contains(x)).ToArray())
            {
                resources[key].Dispose();
                resources.Remove(key);
            }
        }

        private static TextFormatKey ResolveFormatKey(string? placement)
        {
            var value = placement ?? string.Empty;
            var alignment = value.Contains("right", StringComparison.OrdinalIgnoreCase) ? StringAlignment.Far
                : value.Contains("center", StringComparison.OrdinalIgnoreCase) ? StringAlignment.Center
                : StringAlignment.Near;
            var lineAlignment = value.Contains("bottom", StringComparison.OrdinalIgnoreCase) ? StringAlignment.Far
                : value.Equals("center", StringComparison.OrdinalIgnoreCase) || value.Contains("middle", StringComparison.OrdinalIgnoreCase) ? StringAlignment.Center
                : StringAlignment.Near;
            return new TextFormatKey(alignment, lineAlignment);
        }

        private void DrawText(Graphics graphics, Rectangle bounds, VideoOverlayTextElement element, TextRenderStyle style)
        {
            var margin = Math.Max(8f, style.Size * 0.35f);
            var rect = new RectangleF(bounds.Left + margin, bounds.Top + margin,
                Math.Max(1, bounds.Width - margin * 2), Math.Max(1, bounds.Height - margin * 2));
            var shadowRect = rect;
            shadowRect.Offset(Math.Max(1f, style.Size * 0.08f), Math.Max(1f, style.Size * 0.08f));
            graphics.DrawString(element.Text ?? string.Empty, style.Font, _shadowBrush, shadowRect, style.Format);
            graphics.DrawString(element.Text ?? string.Empty, style.Font, style.Foreground, rect, style.Format);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var font in _fonts.Values) font.Dispose();
                foreach (var brush in _foregroundBrushes.Values) brush.Dispose();
                foreach (var format in _formats.Values) format.Dispose();
                _fonts.Clear();
                _foregroundBrushes.Clear();
                _formats.Clear();
                _renderStyles.Clear();
                _shadowBrush.Dispose();
            }
            base.Dispose(disposing);
        }

        private readonly record struct TextFormatKey(StringAlignment Alignment, StringAlignment LineAlignment);
        private readonly record struct TextRenderStyle(Font Font, SolidBrush Foreground, StringFormat Format, float Size);

        private static Color ParseColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Color.White;
            try
            {
                var text = value.Trim();
                if (text.StartsWith('#'))
                {
                    var hex = text[1..];
                    if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                        return Color.FromArgb(255, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
                    if (hex.Length == 8 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
                        return Color.FromArgb((argb >> 24) & 0xff, (argb >> 16) & 0xff, (argb >> 8) & 0xff, argb & 0xff);
                }
                var named = Color.FromName(text);
                return named.IsKnownColor || named.IsNamedColor ? named : Color.White;
            }
            catch { return Color.White; }
        }

        private static IntPtr FindBestVideoWindow(int processId)
        {
            if (processId <= 0) return IntPtr.Zero;
            IntPtr main = IntPtr.Zero;
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                main = process.MainWindowHandle;
            }
            catch { }
            if (main == IntPtr.Zero || !IsWindowVisible(main)) return IntPtr.Zero;

            var best = main;
            long bestArea = 0;
            EnumChildWindows(main, (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || !TryGetScreenBounds(hwnd, out var rect)) return true;
                var area = (long)rect.Width * rect.Height;
                if (rect.Width >= 160 && rect.Height >= 90 && area > bestArea)
                {
                    best = hwnd;
                    bestArea = area;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static bool TryGetScreenBounds(IntPtr hwnd, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (!GetClientRect(hwnd, out var client)) return false;
            var point = new PointNative { X = client.Left, Y = client.Top };
            if (!ClientToScreen(hwnd, ref point)) return false;
            bounds = new Rectangle(point.X, point.Y, client.Right - client.Left, client.Bottom - client.Top);
            return true;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RectNative { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PointNative { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out RectNative rect);
        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref PointNative point);
    }
}
