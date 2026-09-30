using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TvAIrEpgRec;

// Single-source worker presentation responsibility.
// Exactly one top-level WinForms window belongs to one TvAIrEpgRec process.
// Capture/recording only publish state; presentation never owns or mutates recording lifetime.
internal static class WorkerPresentation
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, RecordingDisplay> ActiveRecordings = new();

    private static PresentationBase _base = new("TvAIrEpgRec", null, null, false);
    private static RecordingDisplay? _lastRecording;
    private static long _sequence;
    private static Thread? _uiThread;
    private static WorkerWindow? _window;

    internal static void UpdateBase(string? mode, TvAIrEpgRecJob? job, bool ensureWindow)
    {
        try
        {
            PresentationSnapshot snapshot;
            lock (Gate)
            {
                _base = BuildBase(mode, job);
                if (ensureWindow)
                    EnsureWindowStartedLocked();
                snapshot = BuildSnapshotLocked();
            }
            Project(snapshot);
        }
        catch
        {
            // Presentation must never affect capture/record execution.
        }
    }

    internal static void RecordingOpened(RecordingJob recording)
    {
        if (recording is null) return;
        RecordingOpened(recording.ReservationId, recording.Title, recording.ServiceName);
    }

    internal static void RecordingOpened(ChainRecordingSegment segment)
    {
        if (segment is null) return;
        RecordingOpened(segment.ReservationId, segment.Title, segment.ServiceName);
    }

    private static void RecordingOpened(int reservationId, string? title, string? serviceName)
    {
        try
        {
            PresentationSnapshot snapshot;
            lock (Gate)
            {
                var display = new RecordingDisplay(
                    reservationId,
                    BuildRecordingTitle(title, serviceName, reservationId),
                    ++_sequence);
                ActiveRecordings[reservationId] = display;
                _lastRecording = display;
                EnsureWindowStartedLocked();
                snapshot = BuildSnapshotLocked();
            }
            Project(snapshot);
        }
        catch
        {
            // Visual state cannot affect TS recording.
        }
    }

    internal static void RecordingClosed(int reservationId)
    {
        try
        {
            PresentationSnapshot snapshot;
            lock (Gate)
            {
                ActiveRecordings.Remove(reservationId);
                snapshot = BuildSnapshotLocked();
            }
            Project(snapshot);
        }
        catch
        {
            // Visual state cannot affect segment close/finalization.
        }
    }

    private static PresentationBase BuildBase(string? mode, TvAIrEpgRecJob? job)
    {
        var normalizedMode = FirstNonEmpty(mode, job?.Mode, "runtime");
        var preTuneLabel = job?.Metadata != null && job.Metadata.TryGetValue("preTuneDisplayLabel", out var label)
            ? label
            : null;

        var modeLabel = normalizedMode.Equals("record", StringComparison.OrdinalIgnoreCase) ? "録画" :
            normalizedMode.Equals("epg-check", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(preTuneLabel) ? preTuneLabel! :
            normalizedMode.Equals("epg-check", StringComparison.OrdinalIgnoreCase) ? "EPG確認" :
            normalizedMode.Equals("epg", StringComparison.OrdinalIgnoreCase) ? "EPG取得" :
            "TvAIrEpgRec";

        var subject = FirstNonEmpty(
            job?.Metadata != null && job.Metadata.TryGetValue("title", out var title) ? title : null,
            job?.Metadata != null && job.Metadata.TryGetValue("displayTitle", out var displayTitle) ? displayTitle : null,
            job?.Metadata != null && job.Metadata.TryGetValue("worker", out var worker) ? worker : null,
            job?.Channels?.FirstOrDefault()?.ServiceName,
            job?.Group,
            string.Empty);

        var shortSubject = TrimForTitle(subject, 44);
        var caption = string.IsNullOrWhiteSpace(shortSubject) ? modeLabel : $"{modeLabel} {shortSubject}";
        var logos = GetLogoPaths(job);
        return new PresentationBase(caption, logos.TitleBarLogoPath, logos.CenterLogoPath,
            normalizedMode.Equals("record", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRecordingTitle(string? title, string? serviceName, int reservationId)
    {
        var subject = FirstNonEmpty(title, serviceName, $"R{reservationId}");
        var shortSubject = TrimForTitle(subject, 44);
        return string.IsNullOrWhiteSpace(shortSubject) ? "録画" : $"録画 {shortSubject}";
    }

    private static PresentationSnapshot BuildSnapshotLocked()
    {
        RecordingDisplay? current = null;
        if (_base.IsRecordingMode)
        {
            current = ActiveRecordings.Values.OrderByDescending(item => item.Sequence).FirstOrDefault();
            // Do not regress to the process-launch title while the worker is completing the final segment.
            current ??= _lastRecording;
        }
        return new PresentationSnapshot(current?.Title ?? _base.Title, _base.TitleBarLogoPath, _base.CenterLogoPath);
    }

    private static void Project(PresentationSnapshot snapshot)
    {
        try { Console.Title = snapshot.Title; } catch { }

        WorkerWindow? window;
        lock (Gate) window = _window;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return;

        try
        {
            window.BeginInvoke(new Action(() => window.Apply(snapshot)));
        }
        catch
        {
            // Window projection is visual only.
        }
    }

    private static void EnsureWindowStartedLocked()
    {
        if (_window is not null && !_window.IsDisposed) return;
        if (_uiThread is not null && _uiThread.IsAlive) return;

        _uiThread = new Thread(UiThreadMain)
        {
            IsBackground = true,
            Name = "TvAIrEpgRecPresentation"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    private static void UiThreadMain()
    {
        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            PresentationSnapshot initial;
            lock (Gate) initial = BuildSnapshotLocked();

            // There is exactly one top-level worker window in this process.
            // Keep its WinForms/DWM behavior identical to the proven pre-rebuild worker window:
            // no owner/child window, no WS_EX_NOACTIVATE, no secondary preview surface.
            using var form = new WorkerWindow(initial);
            PresentationSnapshot current;
            lock (Gate)
            {
                _window = form;
                current = BuildSnapshotLocked();
            }
            form.Apply(current);
            System.Windows.Forms.Application.Run(form);

            lock (Gate)
            {
                if (ReferenceEquals(_window, form)) _window = null;
            }
        }
        catch
        {
            // UI lifetime cannot affect the worker process lifetime.
        }
    }

    private static LogoPaths GetLogoPaths(TvAIrEpgRecJob? job)
    {
        if (job?.Metadata == null) return new LogoPaths(null, null);
        var titleBar = FirstNonEmpty(
            job.Metadata.TryGetValue("titleBarLogoPath", out var titleBarLogoPath) ? titleBarLogoPath : null,
            job.Metadata.TryGetValue("displayLogoPath", out var displayLogoPath) ? displayLogoPath : null,
            job.Metadata.TryGetValue("serviceLogoPath", out var serviceLogoPath) ? serviceLogoPath : null,
            job.Metadata.TryGetValue("logoPath", out var logoPath) ? logoPath : null);
        var center = FirstNonEmpty(
            job.Metadata.TryGetValue("centerLogoPath", out var centerLogoPath) ? centerLogoPath : null,
            job.Metadata.TryGetValue("displayLogoPath", out var displayLogoPath2) ? displayLogoPath2 : null);
        return new LogoPaths(titleBar, center);
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string TrimForTitle(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Replace('\u3000', ' ').Replace("\r", " ").Replace("\n", " ").Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal)) normalized = normalized.Replace("  ", " ");
        return normalized.Length <= max ? normalized : normalized[..Math.Max(0, max - 1)] + "…";
    }

    private sealed record PresentationBase(string Title, string? TitleBarLogoPath, string? CenterLogoPath, bool IsRecordingMode);
    private sealed record RecordingDisplay(int ReservationId, string Title, long Sequence);
    private sealed record PresentationSnapshot(string Title, string? TitleBarLogoPath, string? CenterLogoPath);
    private sealed record LogoPaths(string? TitleBarLogoPath, string? CenterLogoPath);

    // One and only one top-level user-visible worker window.
    // This intentionally retains the old proven Form semantics; the rebuild is in responsibility/state ownership,
    // not in inventing new native-window behavior.
    private sealed class WorkerWindow : System.Windows.Forms.Form
    {
        private readonly System.Windows.Forms.PictureBox _pictureBox = new();
        private Image? _currentImage;
        private Icon? _currentIcon;
        private string? _titleBarLogoPath;
        private string? _centerLogoPath;
        private bool _visualInitialized;
        private bool _minimizeQueued;

        internal WorkerWindow(PresentationSnapshot initial)
        {
            Text = initial.Title;
            ShowInTaskbar = true;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            Size = new Size(220, 140);
            MinimumSize = new Size(180, 110);
            MaximizeBox = false;
            BackColor = Color.FromArgb(238, 238, 238);

            _pictureBox.Dock = System.Windows.Forms.DockStyle.Fill;
            _pictureBox.BackColor = Color.FromArgb(238, 238, 238);
            _pictureBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.CenterImage;
            _pictureBox.Padding = new System.Windows.Forms.Padding(12);
            Controls.Add(_pictureBox);

            Apply(initial);
        }

        internal void Apply(PresentationSnapshot snapshot)
        {
            if (!string.Equals(Text, snapshot.Title, StringComparison.Ordinal))
                Text = snapshot.Title;

            var visualChanged = !_visualInitialized
                || !string.Equals(_titleBarLogoPath, snapshot.TitleBarLogoPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_centerLogoPath, snapshot.CenterLogoPath, StringComparison.OrdinalIgnoreCase);

            if (visualChanged)
            {
                _titleBarLogoPath = snapshot.TitleBarLogoPath;
                _centerLogoPath = snapshot.CenterLogoPath;
                using var iconSource = LoadLogoOrFallback(snapshot.TitleBarLogoPath);
                using var centerSource = LoadLogoOrFallback(snapshot.CenterLogoPath ?? snapshot.TitleBarLogoPath);
                SetWindowIcon(iconSource);
                SetCenterImage(centerSource);
                _visualInitialized = true;
            }

            QueueMinimizeOnce();
        }

        private void QueueMinimizeOnce()
        {
            if (_minimizeQueued) return;
            _minimizeQueued = true;
            Shown += (_, _) =>
            {
                var timer = new System.Windows.Forms.Timer { Interval = 350 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    if (!IsDisposed) WindowState = System.Windows.Forms.FormWindowState.Minimized;
                };
                timer.Start();
            };
        }

        private void SetWindowIcon(Bitmap source)
        {
            try
            {
                var icon = CreateIconFromBitmap(source, 64);
                if (icon is null) return;
                var previous = _currentIcon;
                _currentIcon = icon;
                Icon = icon;
                previous?.Dispose();
            }
            catch { }
        }

        private void SetCenterImage(Bitmap source)
        {
            var size = Math.Max(48, Math.Min(96, Math.Min(ClientSize.Width, ClientSize.Height) - 24));
            var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var maxW = size - 8f;
                var maxH = size - 8f;
                var scale = Math.Min(maxW / Math.Max(1, source.Width), maxH / Math.Max(1, source.Height));
                var w = Math.Max(1, (int)Math.Round(source.Width * scale));
                var h = Math.Max(1, (int)Math.Round(source.Height * scale));
                g.DrawImage(source, new Rectangle((size - w) / 2, (size - h) / 2, w, h));
            }

            var previous = _currentImage;
            _currentImage = canvas;
            _pictureBox.Image = canvas;
            previous?.Dispose();
        }

        private static Bitmap LoadLogoOrFallback(string? logoPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath)) return new Bitmap(logoPath);
            }
            catch { }

            try
            {
                using var appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
                if (appIcon is not null) return appIcon.ToBitmap();
            }
            catch { }

            var fallback = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(fallback);
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(34, 34, 34));
            using var pen = new Pen(Color.FromArgb(210, 24, 24), 5);
            g.FillRectangle(brush, new Rectangle(8, 8, 48, 48));
            g.DrawLine(pen, 25, 20, 25, 44);
            g.DrawLine(pen, 25, 20, 44, 32);
            g.DrawLine(pen, 44, 32, 25, 44);
            return fallback;
        }

        private static Icon? CreateIconFromBitmap(Bitmap source, int size)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                using var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    var maxW = size - 6f;
                    var maxH = size - 6f;
                    var scale = Math.Min(maxW / Math.Max(1, source.Width), maxH / Math.Max(1, source.Height));
                    var w = Math.Max(1, (int)Math.Round(source.Width * scale));
                    var h = Math.Max(1, (int)Math.Round(source.Height * scale));
                    g.DrawImage(source, new Rectangle((size - w) / 2, (size - h) / 2, w, h));
                }

                handle = canvas.GetHicon();
                using var temp = Icon.FromHandle(handle);
                return (Icon)temp.Clone();
            }
            catch { return null; }
            finally
            {
                if (handle != IntPtr.Zero) DestroyIcon(handle);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pictureBox.Image = null;
                _currentImage?.Dispose();
                Icon = null;
                _currentIcon?.Dispose();
                _pictureBox.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
