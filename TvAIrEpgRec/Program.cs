using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TvAIrEpgRec.CommonTsRoute;

namespace TvAIrEpgRec;

internal static class Program
{
    private const string Version = "0.10.27";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<int> Main(string[] args)
    {
        var startedAt = DateTimeOffset.Now;
        var parsed = CliOptions.Parse(args);
        var mode = NormalizeMode(parsed.Get("mode"));
        var jobPath = parsed.Get("job");
        var progressPath = parsed.Get("progress");
        var resultPath = parsed.Get("result") ?? parsed.Get("result-path");
        var cancelPath = parsed.Get("cancel");
        var keepAliveMs = parsed.GetInt("keep-alive-ms", 0);

        ApplyTaskbarTitle(mode, null, "起動中");

        TvAIrEpgRecJob? job = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(jobPath) && File.Exists(jobPath))
            {
                var json = await File.ReadAllTextAsync(jobPath).ConfigureAwait(false);
                job = JsonSerializer.Deserialize<TvAIrEpgRecJob>(json, JsonOptions);
                mode = NormalizeMode(mode ?? job?.Mode);
                progressPath ??= job?.ProgressPath;
                resultPath ??= string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase)
                    ? job?.ResultPath
                    : job?.ResultPath ?? job?.OutputPath;
                cancelPath ??= job?.CancelSignalPath;

                ApplyTaskbarTitle(mode, job, "準備中");
                EnsureWorkerStatusWindow(mode, job);
                var loadedJobId = FirstNonEmpty(job?.JobId, parsed.Get("job-id"), Guid.NewGuid().ToString("N"));
                await WriteProgressAsync(progressPath, new WorkerProgress
                {
                    Timestamp = DateTimeOffset.Now,
                    Version = Version,
                    JobId = loadedJobId,
                    Mode = NormalizeMode(mode) ?? "runtime",
                    Stage = "job_loaded",
                    Message = "Job JSON loaded and file IPC paths resolved.",
                    ProcessId = Environment.ProcessId
                }).ConfigureAwait(false);
            }

            mode ??= "runtime";
            ApplyTaskbarTitle(mode, job, "実行中");
            if (string.IsNullOrWhiteSpace(resultPath) && !string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase))
            {
                resultPath = parsed.Get("output");
            }
            var jobId = FirstNonEmpty(job?.JobId, parsed.Get("job-id"), Guid.NewGuid().ToString("N"));

            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = startedAt,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = "started",
                Message = IsEpgLikeMode(mode)
                    ? $"TvAIrEpgRec {mode} mode started. single-process tuner runtime is used before EPG/EPG-check; dbWrite remains false in epg-check."
                    : "TvAIrEpgRec integrated execution line started. Existing DirectRecorderBridge executable is not modified or launched as a child; BonDriver/OpenTuner/SetChannel/TS-read/Close/Release/FreeLibrary execution is owned by TvAIrEpgRec before mode-specific EPG behavior.",
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);

            var runtimeMode = IsEpgLikeMode(mode) || mode == "record";
            var epgContract = IsEpgLikeMode(mode)
                ? await BuildEpgContractSummaryAsync(job, parsed, progressPath, jobId, mode).ConfigureAwait(false)
                : null;

            var tsReadProbe = runtimeMode && IsTsReadProbeRequested(job, parsed, mode)
                ? await RunTsReadProbeAsync(job, parsed, progressPath, jobId, mode).ConfigureAwait(false)
                : null;

            var setChannelProbe = tsReadProbe is null && runtimeMode && IsSetChannelProbeRequested(job, parsed)
                ? await RunSetChannelProbeAsync(job, parsed, progressPath, jobId, mode).ConfigureAwait(false)
                : null;

            var bonDriverOpenProbe = tsReadProbe is null && setChannelProbe is null && runtimeMode && IsBonDriverOpenProbeRequested(job, parsed)
                ? await RunBonDriverOpenProbeAsync(job, parsed, progressPath, jobId, mode).ConfigureAwait(false)
                : null;

            var recordMode = string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase);
            // In record mode the cancel path is the normal stop-signal file.  Treating it as a post-run
            // cancellation was the old DirectRecorderBridge migration mistake: successful recordings were
            // written and then downgraded to success=false after the scheduled stop.
            var cancelled = recordMode ? false : await WaitForOptionalCancellationAsync(cancelPath, keepAliveMs, progressPath, jobId, mode).ConfigureAwait(false);
            var endedAt = DateTimeOffset.Now;

            var bonDriverProbeFailed = bonDriverOpenProbe is not null && !bonDriverOpenProbe.OpenTunerOk;
            var setChannelProbeFailed = setChannelProbe is not null && !setChannelProbe.SetChannelOk;
            var tsReadProbeFailed = tsReadProbe is not null && !tsReadProbe.TsReadOk;
            var compatibleRecordResult = recordMode && tsReadProbe is not null
                ? DirectRecorderCompatibleResult.FromTsReadProbe(job, tsReadProbe, startedAt, endedAt)
                : null;
            var resultSuccess = !cancelled && !bonDriverProbeFailed && !setChannelProbeFailed && !tsReadProbeFailed;
            if (compatibleRecordResult is not null)
            {
                resultSuccess = resultSuccess && compatibleRecordResult.Success;
            }

            var result = new WorkerResult
            {
                Success = resultSuccess,
                Cancelled = cancelled,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                StartedAt = startedAt,
                EndedAt = endedAt,
                ProcessId = Environment.ProcessId,
                ExitCode = cancelled ? 2 : (bonDriverProbeFailed || setChannelProbeFailed || tsReadProbeFailed) ? 3 : 0,
                Message = cancelled
                    ? "TvAIrEpgRec single-process execution cancelled by cancel signal."
                    : tsReadProbe is not null
                        ? (tsReadProbeFailed
                            ? "TvAIrEpgRec TS read runtime failed. No EIT parse/DB write was performed."
                            : "TvAIrEpgRec TS read runtime completed. No EIT parse/DB write was performed.")
                        : setChannelProbe is not null
                            ? (setChannelProbeFailed
                                ? "TvAIrEpgRec SetChannel runtime failed. No TS read/DB write was performed."
                                : "TvAIrEpgRec SetChannel runtime completed. No TS read/DB write was performed.")
                        : bonDriverOpenProbe is not null
                            ? (bonDriverProbeFailed
                                ? "TvAIrEpgRec BonDriver open/close runtime failed. No SetChannel/TS read/DB write was performed."
                                : "TvAIrEpgRec BonDriver open/close runtime completed. No SetChannel/TS read/DB write was performed.")
                            : IsEpgLikeMode(mode)
                                ? $"TvAIrEpgRec {mode} mode completed. mode-specific behavior stays behind the DirectRecorderBridge-derived common TS route; epg-check does not write DB."
                                : "TvAIrEpgRec single-process integrated execution completed. DirectRecorderBridge files remain untouched; TvAIrEpgRec executes the migrated tuner open/set/read/close/cooldown boundary for this EPG/EPG-check execution.",
                Job = job,
                EpgContract = epgContract,
                BonDriverOpenProbe = bonDriverOpenProbe,
                SetChannelProbe = setChannelProbe,
                TsReadProbe = tsReadProbe,
                Result = compatibleRecordResult,
                Lineage = BuildLineageSummary(),
                Arguments = parsed.Values
            };

            var finalStage = cancelled ? "キャンセル" : (resultSuccess ? "完了" : "確認要");
            ApplyTaskbarTitle(mode, job, finalStage);
            EnsureWorkerStatusWindow(mode, job);
            await WriteResultAsync(resultPath, result).ConfigureAwait(false);
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = endedAt,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = cancelled ? "cancelled" : "completed",
                Message = result.Message,
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);

            return result.ExitCode;
        }
        catch (Exception ex)
        {
            var endedAt = DateTimeOffset.Now;
            var result = new WorkerResult
            {
                Success = false,
                Cancelled = false,
                Version = Version,
                JobId = job?.JobId ?? parsed.Get("job-id") ?? "unknown",
                Mode = mode ?? "unknown",
                StartedAt = startedAt,
                EndedAt = endedAt,
                ProcessId = Environment.ProcessId,
                ExitCode = 1,
                Message = ex.Message,
                ErrorType = ex.GetType().FullName,
                Error = ex.ToString(),
                Job = job,
                Lineage = BuildLineageSummary(),
                Arguments = parsed.Values
            };

            ApplyTaskbarTitle(mode, job, "異常終了");
            EnsureWorkerStatusWindow(mode, job);

            await WriteResultAsync(resultPath, result).ConfigureAwait(false);
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = endedAt,
                Version = Version,
                JobId = result.JobId,
                Mode = result.Mode,
                Stage = "failed",
                Message = ex.Message,
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);

            return 1;
        }
    }



    private static readonly object WorkerStatusWindowLock = new();
    private static WorkerStatusForm? WorkerStatusWindow;
    private static Thread? WorkerStatusThread;
    private static string? WorkerStatusTitle;
    private static string? WorkerStatusTitleBarLogoPath;
    private static string? WorkerStatusCenterLogoPath;
    private static readonly List<Icon> WorkerStatusRetainedIcons = new();

    private static void EnsureWorkerStatusWindow(string? mode, TvAIrEpgRecJob? job)
    {
        try
        {
            var title = BuildTaskbarTitle(mode, job);
            var logoPaths = GetDisplayLogoPaths(job);
            lock (WorkerStatusWindowLock)
            {
                WorkerStatusTitle = title;
                WorkerStatusTitleBarLogoPath = logoPaths.TitleBarLogoPath;
                WorkerStatusCenterLogoPath = logoPaths.CenterLogoPath;

                if (WorkerStatusWindow is not null && !WorkerStatusWindow.IsDisposed)
                {
                    WorkerStatusWindow.BeginInvoke(new Action(() => WorkerStatusWindow.ApplyState(title, logoPaths.TitleBarLogoPath, logoPaths.CenterLogoPath)));
                    return;
                }

                if (WorkerStatusThread is not null && WorkerStatusThread.IsAlive)
                {
                    return;
                }

                WorkerStatusThread = new Thread(() =>
                {
                    try
                    {
                        System.Windows.Forms.Application.EnableVisualStyles();
                        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                        string? initialTitle;
                        string? initialTitleBarLogo;
                        string? initialCenterLogo;
                        lock (WorkerStatusWindowLock)
                        {
                            initialTitle = WorkerStatusTitle;
                            initialTitleBarLogo = WorkerStatusTitleBarLogoPath;
                            initialCenterLogo = WorkerStatusCenterLogoPath;
                        }

                        using var form = new WorkerStatusForm(initialTitle ?? "TvAIrEpgRec", initialTitleBarLogo, initialCenterLogo);
                        lock (WorkerStatusWindowLock)
                        {
                            WorkerStatusWindow = form;
                        }
                        System.Windows.Forms.Application.Run(form);
                    }
                    catch
                    {
                        // The status window is only a visual aid. It must never affect EPG/record execution.
                    }
                })
                {
                    IsBackground = true,
                    Name = "TvAIrEpgRecStatusWindow",
                };
                WorkerStatusThread.SetApartmentState(ApartmentState.STA);
                WorkerStatusThread.Start();
            }
        }
        catch
        {
            // The status window is only a visual aid. It must never affect EPG/record execution.
        }
    }

    private static void UpdateWorkerStatusWindowTitle(string? mode, TvAIrEpgRecJob? job)
    {
        try
        {
            var title = BuildTaskbarTitle(mode, job);
            lock (WorkerStatusWindowLock)
            {
                WorkerStatusTitle = title;
                var window = WorkerStatusWindow;
                if (window is not null && !window.IsDisposed)
                {
                    window.BeginInvoke(new Action(() => window.ApplyTitle(title)));
                }
            }
        }
        catch
        {
            // Status title is a visual aid only.
        }
    }

    private sealed class WorkerStatusForm : System.Windows.Forms.Form
    {
        private readonly System.Windows.Forms.PictureBox _pictureBox = new();
        private Image? _currentImage;
        private Icon? _currentIcon;
        private bool _minimizeQueued;

        public WorkerStatusForm(string title, string? titleBarLogoPath, string? centerLogoPath)
        {
            Text = title;
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

            ApplyState(title, titleBarLogoPath, centerLogoPath);
        }

        public void ApplyTitle(string title)
        {
            Text = title;
        }

        public void ApplyState(string title, string? titleBarLogoPath, string? centerLogoPath)
        {
            ApplyTitle(title);
            using var iconSource = LoadLogoOrFallback(titleBarLogoPath);
            using var centerSource = LoadLogoOrFallback(centerLogoPath ?? titleBarLogoPath);
            SetWindowIcon(iconSource);
            SetCenterImage(centerSource);
            QueueMinimizeOnce();
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
                lock (WorkerStatusWindowLock)
                {
                    WorkerStatusRetainedIcons.Add(icon);
                }
                previous?.Dispose();
            }
            catch
            {
                // Icon conversion is visual only.
            }
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
                var x = (size - w) / 2;
                var y = (size - h) / 2;
                g.DrawImage(source, new Rectangle(x, y, w, h));
            }

            var previous = _currentImage;
            _currentImage = canvas;
            _pictureBox.Image = canvas;
            previous?.Dispose();
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
                    WindowState = System.Windows.Forms.FormWindowState.Minimized;
                };
                timer.Start();
            };
        }

        private static Bitmap LoadLogoOrFallback(string? logoPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
                {
                    return new Bitmap(logoPath);
                }
            }
            catch
            {
                // Fall back to the application icon below.
            }

            try
            {
                using var appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
                if (appIcon is not null)
                {
                    return appIcon.ToBitmap();
                }
            }
            catch
            {
                // Fall through to generated fallback.
            }

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
                    var x = (size - w) / 2;
                    var y = (size - h) / 2;
                    g.DrawImage(source, new Rectangle(x, y, w, h));
                }

                var handle = canvas.GetHicon();
                using var temp = Icon.FromHandle(handle);
                return (Icon)temp.Clone();
            }
            catch
            {
                return null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pictureBox.Image = null;
                _currentImage?.Dispose();
                _currentIcon?.Dispose();
                _pictureBox.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static WorkerDisplayLogoPaths GetDisplayLogoPaths(TvAIrEpgRecJob? job)
    {
        if (job?.Metadata == null) return WorkerDisplayLogoPaths.Empty;
        var titleBar = FirstNonEmpty(
            job.Metadata.TryGetValue("titleBarLogoPath", out var titleBarLogoPath) ? titleBarLogoPath : null,
            job.Metadata.TryGetValue("displayLogoPath", out var displayLogoPath) ? displayLogoPath : null,
            job.Metadata.TryGetValue("serviceLogoPath", out var serviceLogoPath) ? serviceLogoPath : null,
            job.Metadata.TryGetValue("logoPath", out var logoPath) ? logoPath : null);
        var center = FirstNonEmpty(
            job.Metadata.TryGetValue("centerLogoPath", out var centerLogoPath) ? centerLogoPath : null,
            job.Metadata.TryGetValue("displayLogoPath", out var displayLogoPath2) ? displayLogoPath2 : null);
        return new WorkerDisplayLogoPaths(titleBar, center);
    }

    private sealed record WorkerDisplayLogoPaths(string? TitleBarLogoPath, string? CenterLogoPath)
    {
        public static WorkerDisplayLogoPaths Empty { get; } = new(null, null);
    }

    private static void ApplyTaskbarTitle(string? mode, TvAIrEpgRecJob? job, string stage)
    {
        try
        {
            var title = BuildTaskbarTitle(mode, job);
            Console.Title = title;
            UpdateWorkerStatusWindowTitle(mode, job);
        }
        catch
        {
            // Taskbar title is a user-facing visibility aid. It must never interrupt recording/EPG execution.
        }
    }

    private static string BuildTaskbarTitle(string? mode, TvAIrEpgRecJob? job)
    {
        var normalizedMode = NormalizeMode(mode) ?? NormalizeMode(job?.Mode) ?? "runtime";
        var preTuneLabel = job?.Metadata != null && job.Metadata.TryGetValue("preTuneDisplayLabel", out var label) ? label : null;
        var modeLabel = normalizedMode.Equals("record", StringComparison.OrdinalIgnoreCase) ? "録画" :
            normalizedMode.Equals("epg-check", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(preTuneLabel) ? preTuneLabel! :
            normalizedMode.Equals("epg-check", StringComparison.OrdinalIgnoreCase) ? "EPG確認" :
            normalizedMode.Equals("epg", StringComparison.OrdinalIgnoreCase) ? "EPG取得" :
            "TvAIrEpgRec";

        var title = FirstNonEmpty(
            job?.Metadata != null && job.Metadata.TryGetValue("title", out var programTitle) ? programTitle : null,
            job?.Metadata != null && job.Metadata.TryGetValue("displayTitle", out var displayTitle) ? displayTitle : null,
            job?.Metadata != null && job.Metadata.TryGetValue("worker", out var worker) ? worker : null,
            job?.Channels?.FirstOrDefault()?.ServiceName,
            job?.Group,
            string.Empty);

        var shortTitle = TrimForConsoleTitle(title, 44);
        return string.IsNullOrWhiteSpace(shortTitle) ? modeLabel : $"{modeLabel} {shortTitle}";
    }

    private static string TrimForConsoleTitle(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Replace('\u3000', ' ').Replace("\r", " ").Replace("\n", " ").Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal)) normalized = normalized.Replace("  ", " ");
        return normalized.Length <= max ? normalized : normalized[..Math.Max(0, max - 1)] + "…";
    }


    private static async Task<EpgContractSummary> BuildEpgContractSummaryAsync(TvAIrEpgRecJob? job, CliOptions parsed, string? progressPath, string jobId, string mode)
    {
        var channels = job?.Channels ?? new List<EpgChannelJob>();
        if (channels.Count == 0)
        {
            var cliService = parsed.Get("service");
            if (!string.IsNullOrWhiteSpace(cliService))
            {
                channels.Add(new EpgChannelJob
                {
                    ServiceName = cliService,
                    NetworkId = parsed.GetIntNullable("nid"),
                    TransportStreamId = parsed.GetIntNullable("tsid"),
                    ServiceId = parsed.GetIntNullable("sid"),
                    ChannelSpace = parsed.GetIntNullable("chspace"),
                    ChannelIndex = parsed.GetIntNullable("chi"),
                    ChannelArgument = parsed.Get("channel")
                });
            }
        }

        var summary = new EpgContractSummary
        {
            ContractOk = !string.IsNullOrWhiteSpace(job?.Group ?? parsed.Get("group"))
                         && !string.IsNullOrWhiteSpace(job?.Tuner ?? parsed.Get("tuner"))
                         && !string.IsNullOrWhiteSpace(job?.Did ?? parsed.Get("did"))
                         && !string.IsNullOrWhiteSpace(job?.BonDriver ?? parsed.Get("bonDriver") ?? parsed.Get("bondriver"))
                         && channels.Count > 0,
            Group = FirstNonEmpty(job?.Group, parsed.Get("group"), "unknown"),
            Tuner = FirstNonEmpty(job?.Tuner, parsed.Get("tuner"), "unknown"),
            Did = FirstNonEmpty(job?.Did, parsed.Get("did"), "unknown"),
            BonDriver = FirstNonEmpty(job?.BonDriver, parsed.Get("bonDriver"), parsed.Get("bondriver"), "unknown"),
            ChannelCount = channels.Count,
            Channels = channels,
            BonDriverAccess = false,
            DbWrite = false,
            Purpose = "epg_job_contract_only"
        };

        await WriteProgressAsync(progressPath, new WorkerProgress
        {
            Timestamp = DateTimeOffset.Now,
            Version = Version,
            JobId = jobId,
            Mode = mode,
            Stage = "epg_contract_loaded",
            Message = $"EPG contract parsed: group={summary.Group} tuner={summary.Tuner} did={summary.Did} bonDriver={summary.BonDriver} channels={summary.ChannelCount} contractOk={summary.ContractOk}.",
            ProcessId = Environment.ProcessId
        }).ConfigureAwait(false);

        foreach (var channel in channels.Take(8))
        {
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = "epg_contract_channel_resolved",
                Message = $"service={channel.ServiceName ?? "-"} nid={channel.NetworkId?.ToString() ?? "-"} tsid={channel.TransportStreamId?.ToString() ?? "-"} sid={channel.ServiceId?.ToString() ?? "-"} chspace={channel.ChannelSpace?.ToString() ?? "-"} chi={channel.ChannelIndex?.ToString() ?? "-"} arg={channel.ChannelArgument ?? "-"}",
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
        }

        return summary;
    }


    private static void ApplyTvTestCardReaderReference(TvAIrEpgRecJob? job, CliOptions parsed, TsReadProbeSummary summary)
    {
        var tvTestExe = FirstNonEmptyOrNull(job?.TvTestExecutablePath,
            parsed.Get("tvTestExecutablePath"),
            parsed.Get("tvtest-exe"),
            parsed.Get("tvtest"));
        var tvTestDir = ResolveTvTestDirectoryForCardReader(tvTestExe, summary.ResolvedPath);
        summary.CardReaderTvTestDirectory = tvTestDir;
        summary.CardReaderReferenceRule = "v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback";

        if (string.IsNullOrWhiteSpace(tvTestDir))
        {
            summary.CardReaderReferenceResult = "SKIP_TVTEST_DIR_NOT_RESOLVED";
            return;
        }

        var winscardPath = Path.Combine(tvTestDir, "winscard.dll");
        summary.CardReaderWinscardPath = winscardPath;
        if (!File.Exists(winscardPath))
        {
            summary.CardReaderReferenceResult = "SKIP_TVTEST_WINSCARD_DLL_MISSING";
            return;
        }

        var module = NativeMethods.LoadLibraryExW(winscardPath, IntPtr.Zero, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
        summary.CardReaderWinscardLoaded = module != IntPtr.Zero;
        summary.CardReaderReferenceResult = module != IntPtr.Zero ? "OK_TVTEST_WINSCARD_PRELOADED" : $"NG_LOADLIBRARYEX_LASTERROR_{Marshal.GetLastWin32Error()}";
        summary.CardReaderLoadedModulePath = module != IntPtr.Zero ? NativeMethods.GetModulePath(module) : null;

        // Intentionally keep the module loaded for the worker lifetime.  This pins the TVTest-directory
        // card-reader shim before any B25/libaribb25 path can initialize, without copying winscard.* into TvAIr.
        if (module != IntPtr.Zero) NativeMethods.RegisterPinnedCardReaderModule(module);
    }

    private static string? ResolveTvTestDirectoryForCardReader(string? tvTestExe, string? bonDriverPath)
    {
        if (!string.IsNullOrWhiteSpace(tvTestExe))
        {
            try
            {
                var fullExe = Path.GetFullPath(tvTestExe);
                if (File.Exists(fullExe))
                {
                    var dir = Path.GetDirectoryName(fullExe);
                    if (!string.IsNullOrWhiteSpace(dir)) return dir;
                }
            }
            catch { }
        }

        // v0.11.678: CardReaderRuntimeProbeDirectory は ConfiguredTvTestExecutablePath だけから解決する。
        // BonDriver 配置から TVTest ディレクトリを推測しない。
        return null;
    }

    private static bool IsBonDriverOpenProbeRequested(TvAIrEpgRecJob? job, CliOptions parsed)
    {
        if (parsed.Get("bondriver-open-runtime")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (parsed.Get("bonDriverOpenProbe")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (job?.Metadata is null) return false;
        if (job.Metadata.TryGetValue("bonDriverOpenProbe", out var direct) && direct.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (job.Metadata.TryGetValue("bonDriverAccess", out var access) && access.Equals("open_close_only", StringComparison.OrdinalIgnoreCase)) return true;
        if (job.Metadata.TryGetValue("purpose", out var purpose) && purpose.Contains("bondriver_open_runtime", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsSetChannelProbeRequested(TvAIrEpgRecJob? job, CliOptions parsed)
    {
        if (parsed.Get("setchannel-runtime")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (parsed.Get("setChannelProbe")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (job?.Metadata is null) return false;
        if (job.Metadata.TryGetValue("setChannelProbe", out var direct) && direct.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (job.Metadata.TryGetValue("setChannel", out var setChannel) && setChannel.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (job.Metadata.TryGetValue("purpose", out var purpose) && purpose.Contains("setchannel_runtime", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsTsReadProbeRequested(TvAIrEpgRecJob? job, CliOptions parsed, string? mode)
    {
        if (string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsEpgLikeMode(mode)) return true;
        if (parsed.Get("ts-read-runtime")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (parsed.Get("tsReadProbe")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (job?.Metadata is null) return false;
        if (job.Metadata.TryGetValue("tsReadProbe", out var direct) && direct.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (job.Metadata.TryGetValue("tsRead", out var tsRead) && tsRead.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (job.Metadata.TryGetValue("purpose", out var purpose) && (purpose.Contains("ts_read_runtime", StringComparison.OrdinalIgnoreCase) || purpose.Contains("epg_capture_ts_file", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static string ResolveTsGetStreamVariant(TvAIrEpgRecJob? job, CliOptions parsed)
    {
        var raw = FirstNonEmpty(parsed.Get("getstream-variant"), parsed.Get("variant"),
            job?.Metadata != null && job.Metadata.TryGetValue("getStreamVariant", out var m) ? m : null,
            job?.Metadata != null && job.Metadata.TryGetValue("variant", out var v) ? v : null,
            "ready-only");
        raw = raw.Trim().ToLowerInvariant();
        return raw switch
        {
            "pointer-vtable6-single" => "pointer-vtable6-single",
            "pointer-vtable7-single" => "pointer-vtable7-single",
            "buffer-vtable7-single" => "buffer-vtable7-single",
            "pointer-vtable6-ready-threshold-single" => "pointer-vtable6-ready-threshold-single",
            "buffer-vtable7-ready-threshold-single" => "buffer-vtable7-ready-threshold-single",
            "pointer-vtable6-ready-threshold-continuous" => "pointer-vtable6-ready-threshold-continuous",
            "buffer-vtable7-ready-threshold-continuous" => "buffer-vtable7-ready-threshold-continuous",
            "pointer-vtable6-continuous" => "pointer-vtable6-ready-threshold-continuous",
            "buffer-vtable7-continuous" => "buffer-vtable7-ready-threshold-continuous",
            "continuous-pointer-vtable6" => "pointer-vtable6-ready-threshold-continuous",
            "continuous-buffer-vtable7" => "buffer-vtable7-ready-threshold-continuous",
            "pointer-vtable6-psi-minimal" => "pointer-vtable6-psi-minimal",
            "buffer-vtable7-psi-minimal" => "buffer-vtable7-psi-minimal",
            "psi-pointer-vtable6" => "pointer-vtable6-psi-minimal",
            "psi-buffer-vtable7" => "buffer-vtable7-psi-minimal",
            "pat-pmt-sdt-eit-pointer" => "pointer-vtable6-psi-minimal",
            "pat-pmt-sdt-eit-buffer" => "buffer-vtable7-psi-minimal",
            "pointer-vtable6-eit-minimal-decode" => "pointer-vtable6-eit-minimal-decode",
            "buffer-vtable7-eit-minimal-decode" => "buffer-vtable7-eit-minimal-decode",
            "eit-pointer-vtable6" => "pointer-vtable6-eit-minimal-decode",
            "eit-buffer-vtable7" => "buffer-vtable7-eit-minimal-decode",
            "eit-minimal-pointer" => "pointer-vtable6-eit-minimal-decode",
            "eit-minimal-buffer" => "buffer-vtable7-eit-minimal-decode",
            "pointer-vtable6-eit-target-service" => "pointer-vtable6-eit-target-service",
            "buffer-vtable7-eit-target-service" => "buffer-vtable7-eit-target-service",
            "eit-target-pointer" => "pointer-vtable6-eit-target-service",
            "eit-target-buffer" => "buffer-vtable7-eit-target-service",
            "target-service-eit-pointer" => "pointer-vtable6-eit-target-service",
            "target-service-eit-buffer" => "buffer-vtable7-eit-target-service",
            "pointer-vtable6-eit-arib-decode" => "pointer-vtable6-eit-arib-decode",
            "buffer-vtable7-eit-arib-decode" => "buffer-vtable7-eit-arib-decode",
            "eit-arib-pointer" => "pointer-vtable6-eit-arib-decode",
            "eit-arib-buffer" => "buffer-vtable7-eit-arib-decode",
            "arib-short-event-pointer" => "pointer-vtable6-eit-arib-decode",
            "arib-short-event-buffer" => "buffer-vtable7-eit-arib-decode",
            "pointer-vtable6-epg-normalize" => "pointer-vtable6-epg-normalize",
            "pointer-vtable6-epg-normalize-gr-logo-opportunistic" => "pointer-vtable6-epg-normalize-gr-logo-opportunistic",
            "epg-normalize-gr-logo" => "pointer-vtable6-epg-normalize-gr-logo-opportunistic",
            "pointer-vtable6-epg-normalize-logo-pid29" => "pointer-vtable6-epg-normalize-logo-pid29",
            "epg-normalize-logo-pid29" => "pointer-vtable6-epg-normalize-logo-pid29",
            "logo-pid29-pointer" => "pointer-vtable6-epg-normalize-logo-pid29",
            "buffer-vtable7-epg-normalize" => "buffer-vtable7-epg-normalize",
            "epg-normalize-pointer" => "pointer-vtable6-epg-normalize",
            "epg-normalize-buffer" => "buffer-vtable7-epg-normalize",
            "epg-intermediate-pointer" => "pointer-vtable6-epg-normalize",
            "epg-intermediate-buffer" => "buffer-vtable7-epg-normalize",
            "pointer-vtable6-ready10-single" => "pointer-vtable6-ready-threshold-single",
            "pointer-vtable6-ready50-single" => "pointer-vtable6-ready-threshold-single",
            "pointer-vtable6-ready100-single" => "pointer-vtable6-ready-threshold-single",
            "buffer-vtable7-ready10-single" => "buffer-vtable7-ready-threshold-single",
            "buffer-vtable7-ready50-single" => "buffer-vtable7-ready-threshold-single",
            "buffer-vtable7-ready100-single" => "buffer-vtable7-ready-threshold-single",
            "ready-only" => "ready-only",
            _ => "ready-only"
        };
    }

    private static int ResolveTsGetStreamReadyThreshold(TvAIrEpgRecJob? job, CliOptions parsed)
    {
        var raw = FirstNonEmpty(parsed.Get("ready-threshold"), parsed.Get("readyThreshold"), parsed.Get("getstream-ready-threshold"),
            job?.Metadata != null && job.Metadata.TryGetValue("readyThreshold", out var m) ? m : null,
            job?.Metadata != null && job.Metadata.TryGetValue("getStreamReadyThreshold", out var g) ? g : null,
            null);
        if (!int.TryParse(raw, out var threshold))
        {
            var variantRaw = FirstNonEmpty(parsed.Get("getstream-variant"), parsed.Get("variant"),
                job?.Metadata != null && job.Metadata.TryGetValue("getStreamVariant", out var vm) ? vm : null,
                job?.Metadata != null && job.Metadata.TryGetValue("variant", out var vv) ? vv : null,
                string.Empty).Trim().ToLowerInvariant();
            threshold = variantRaw.Contains("ready100") ? 100 : variantRaw.Contains("ready10") ? 10 : 50;
        }
        return Math.Clamp(threshold, 1, 5000);
    }

    private static int ResolveStopCooldownMs(TvAIrEpgRecJob? job, CliOptions parsed)
    {
        var raw = FirstNonEmpty(parsed.Get("stop-cooldown-ms"), parsed.Get("stopCooldownMs"),
            job?.Metadata != null && job.Metadata.TryGetValue("stopCooldownMs", out var m) ? m : null,
            job?.Metadata != null && job.Metadata.TryGetValue("tunerStopCooldownMs", out var t) ? t : null,
            null);
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 0, 10000) : 1500;
    }

    private static async Task<BonDriverOpenProbeSummary> RunBonDriverOpenProbeAsync(TvAIrEpgRecJob? job, CliOptions parsed, string? progressPath, string jobId, string mode)
    {
        var bonDriver = FirstNonEmpty(job?.BonDriver, parsed.Get("bonDriver"), parsed.Get("bondriver"), "unknown");
        var requestedPath = FirstNonEmpty(job?.BonDriverPath, parsed.Get("bonDriverPath"), parsed.Get("bondriver-path"), bonDriver);
        var resolvedPath = ResolveBonDriverPath(requestedPath, bonDriver);
        var summary = new BonDriverOpenProbeSummary
        {
            RequestedBonDriver = bonDriver,
            RequestedPath = requestedPath,
            ResolvedPath = resolvedPath,
            LoadLibraryOk = false,
            CreateBonDriverOk = false,
            OpenTunerOk = false,
            CloseTunerCalled = false,
            ReleaseCalled = false,
            FreeLibraryCalled = false,
            Purpose = "bondriver_open_close_only_no_setchannel_no_ts_no_db"
        };

        await WriteProgressAsync(progressPath, new WorkerProgress
        {
            Timestamp = DateTimeOffset.Now,
            Version = Version,
            JobId = jobId,
            Mode = mode,
            Stage = "bondriver_path_resolved",
            Message = $"requested={requestedPath} resolved={resolvedPath} exists={File.Exists(resolvedPath)}",
            ProcessId = Environment.ProcessId
        }).ConfigureAwait(false);

        if (!File.Exists(resolvedPath))
        {
            summary.Error = "BonDriver DLL not found.";
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = "bondriver_open_runtime_failed",
                Message = summary.Error,
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
            return summary;
        }

        await BonDriverNativeProbe.OpenCloseAsync(resolvedPath, summary, async (stage, message) =>
        {
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = stage,
                Message = message,
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return summary;
    }

    private static async Task<SetChannelProbeSummary> RunSetChannelProbeAsync(TvAIrEpgRecJob? job, CliOptions parsed, string? progressPath, string jobId, string mode)
    {
        var bonDriver = FirstNonEmpty(job?.BonDriver, parsed.Get("bonDriver"), parsed.Get("bondriver"), "unknown");
        var requestedPath = FirstNonEmpty(job?.BonDriverPath, parsed.Get("bonDriverPath"), parsed.Get("bondriver-path"), bonDriver);
        var resolvedPath = ResolveBonDriverPath(requestedPath, bonDriver);
        var firstChannel = job?.Channels?.FirstOrDefault();
        var chspace = firstChannel?.ChannelSpace ?? parsed.GetIntNullable("chspace") ?? 0;
        var chi = firstChannel?.ChannelIndex ?? parsed.GetIntNullable("chi") ?? 0;
        var service = FirstNonEmpty(firstChannel?.ServiceName, parsed.Get("service"), "unknown");
        var summary = new SetChannelProbeSummary
        {
            RequestedBonDriver = bonDriver,
            RequestedPath = requestedPath,
            ResolvedPath = resolvedPath,
            ServiceName = service,
            ChannelSpace = chspace,
            ChannelIndex = chi,
            Purpose = "bondriver_open_setchannel_close_only_no_ts_no_db"
        };

        await WriteProgressAsync(progressPath, new WorkerProgress
        {
            Timestamp = DateTimeOffset.Now,
            Version = Version,
            JobId = jobId,
            Mode = mode,
            Stage = "setchannel_path_resolved",
            Message = $"service={service} chspace={chspace} chi={chi} requested={requestedPath} resolved={resolvedPath} exists={File.Exists(resolvedPath)}",
            ProcessId = Environment.ProcessId
        }).ConfigureAwait(false);

        if (!File.Exists(resolvedPath))
        {
            summary.Error = "BonDriver DLL not found.";
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = "setchannel_runtime_failed",
                Message = summary.Error,
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
            return summary;
        }

        await BonDriverNativeProbe.SetChannelAsync(resolvedPath, summary, async (stage, message) =>
        {
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = stage,
                Message = message,
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return summary;
    }

    private static string? FirstNonEmptyOrNull(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static async Task<TsReadProbeSummary> RunTsReadProbeAsync(TvAIrEpgRecJob? job, CliOptions parsed, string? progressPath, string jobId, string mode)
    {
        var bonDriver = FirstNonEmpty(job?.BonDriver, parsed.Get("bonDriver"), parsed.Get("bondriver"), "unknown");
        var requestedPath = FirstNonEmpty(job?.BonDriverPath, parsed.Get("bonDriverPath"), parsed.Get("bondriver-path"), bonDriver);
        var resolvedPath = ResolveBonDriverPath(requestedPath, bonDriver);
        var firstChannel = job?.Channels?.FirstOrDefault();
        var chspace = firstChannel?.ChannelSpace ?? parsed.GetIntNullable("chspace") ?? 0;
        var chi = firstChannel?.ChannelIndex ?? parsed.GetIntNullable("chi") ?? 0;
        var service = FirstNonEmpty(firstChannel?.ServiceName, parsed.Get("service"), "unknown");
        var requestedReadSeconds = parsed.GetInt("read-seconds", job?.TsReadSeconds ?? 3);
        var readSeconds = string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(requestedReadSeconds, 1, 12 * 60 * 60)
            : IsEpgLikeMode(mode)
                ? Math.Clamp(requestedReadSeconds, 1, 30 * 60)
                : Math.Clamp(requestedReadSeconds, 1, 180);
        var targetEventsMin = Math.Clamp(parsed.GetInt("target-events-min", parsed.GetInt("targetEventsMin", 1)), 1, 50);
        var summary = new TsReadProbeSummary
        {
            RequestedBonDriver = bonDriver,
            RequestedPath = requestedPath,
            ResolvedPath = resolvedPath,
            ServiceName = service,
            ChannelSpace = chspace,
            ChannelIndex = chi,
            TargetOriginalNetworkId = firstChannel?.NetworkId ?? parsed.GetIntNullable("nid") ?? 0,
            TargetTransportStreamId = firstChannel?.TransportStreamId ?? parsed.GetIntNullable("tsid") ?? 0,
            TargetServiceId = firstChannel?.ServiceId ?? parsed.GetIntNullable("sid") ?? 0,
            ReadSeconds = readSeconds,
            TargetServiceEventMin = targetEventsMin,
            Variant = ResolveTsGetStreamVariant(job, parsed),
            ReadyThreshold = ResolveTsGetStreamReadyThreshold(job, parsed),
            StopCooldownMs = ResolveStopCooldownMs(job, parsed),
            Mode = mode,
            RecordOutputPath = string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase) || IsEpgLikeMode(mode)
                ? FirstNonEmptyOrNull(job?.OutputPath, parsed.Get("record-output"), parsed.Get("recordOutputPath"), parsed.Get("output"))
                : null,
            RecordStopSignalPath = string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase) || IsEpgLikeMode(mode)
                ? FirstNonEmptyOrNull(job?.CancelSignalPath, parsed.Get("stop-signal"), parsed.Get("stopSignalPath"), parsed.Get("cancel"))
                : null,
            RecordWriteEnabled = string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase) || IsEpgLikeMode(mode),
            Purpose = mode == "epg-check"
                ? "mode_epg_check_after_single_process_tuner_runtime_dbwrite_false_ts_output_enabled"
                : mode == "record"
                    ? "mode_record_after_single_process_tuner_runtime_card_reader_tvtest_dir_reference_only"
                    : "mode_epg_after_single_process_tuner_runtime_ts_output_enabled"
        };

        summary.RecordSegments = NormalizeRecordSegments(job?.RecordSegments, summary.RecordOutputPath);
        var loadedSegmentPlanPath = FirstNonEmptyOrNull(job?.SegmentPlanPath, job?.Metadata != null && job.Metadata.TryGetValue("chainSegmentPlanPath", out var segmentPlanFromMetadata) ? segmentPlanFromMetadata : null);
        if ((summary.RecordSegments.Count <= 1 || summary.RecordSegments.All(x => x.ReservationId == 0)) && !string.IsNullOrWhiteSpace(loadedSegmentPlanPath))
        {
            var loadedSegments = TryLoadRecordSegmentsFromPlan(loadedSegmentPlanPath, summary.RecordOutputPath);
            if (loadedSegments.Count > 0)
            {
                summary.RecordSegments = loadedSegments;
            }
        }

        ApplyTvTestCardReaderReference(job, parsed, summary);
        await WriteProgressAsync(progressPath, new WorkerProgress
        {
            Timestamp = DateTimeOffset.Now,
            Version = Version,
            JobId = jobId,
            Mode = mode,
            Stage = "record_segment_plan_loaded",
            Message = $"result=OK count={summary.RecordSegments.Count} segmentReservations={string.Join(',', summary.RecordSegments.Select(x => "R" + x.ReservationId))} outputPaths={string.Join('|', summary.RecordSegments.Select(x => x.OutputPath ?? "-"))} rule=v0.9.79_chain_restart_same_tuner_cooldown_bypass",
            ProcessId = Environment.ProcessId
        }).ConfigureAwait(false);
        await WriteProgressAsync(progressPath, new WorkerProgress
        {
            Timestamp = DateTimeOffset.Now,
            Version = Version,
            JobId = jobId,
            Mode = mode,
            Stage = "card_reader_reference_prepared",
            Message = $"result={summary.CardReaderReferenceResult} tvTestDir={summary.CardReaderTvTestDirectory ?? "-"} winscardPath={summary.CardReaderWinscardPath ?? "-"} loaded={summary.CardReaderWinscardLoaded} loadedPath={summary.CardReaderLoadedModulePath ?? "-"} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback",
            ProcessId = Environment.ProcessId
        }).ConfigureAwait(false);

        if (string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase))
        {
            summary.RecordDescrambler = ExternalB25DecoderRuntime.TryCreate(summary, async (stage, message) =>
            {
                await WriteProgressAsync(progressPath, new WorkerProgress
                {
                    Timestamp = DateTimeOffset.Now,
                    Version = Version,
                    JobId = jobId,
                    Mode = mode,
                    Stage = stage,
                    Message = message,
                    ProcessId = Environment.ProcessId
                }).ConfigureAwait(false);
            });
        }

        if ((string.Equals(mode, "record", StringComparison.OrdinalIgnoreCase) || IsEpgLikeMode(mode)) && summary.Variant.Equals("ready-only", StringComparison.OrdinalIgnoreCase))
        {
            summary.Variant = "pointer-vtable6-ready-threshold-continuous";
        }

        summary.CommonTsRoute = DirectRecorderTsRouteFacade.AttachForMode(mode, new DirectRecorderTsRouteRequest(
            FirstNonEmpty(job?.Group, parsed.Get("group"), "unknown"),
            FirstNonEmpty(job?.Tuner, parsed.Get("tuner"), "unknown"),
            FirstNonEmpty(job?.Did, parsed.Get("did"), "unknown"),
            bonDriver,
            resolvedPath,
            service,
            summary.TargetOriginalNetworkId,
            summary.TargetTransportStreamId,
            summary.TargetServiceId,
            chspace,
            chi));

        await WriteProgressAsync(progressPath, new WorkerProgress
        {
            Timestamp = DateTimeOffset.Now,
            Version = Version,
            JobId = jobId,
            Mode = mode,
            Stage = "common_ts_route_facade_attached",
            Message = $"routeBeforeMode={summary.CommonTsRoute?.RouteBeforeMode} routeReady={summary.CommonTsRoute?.RouteReadyForMode} mode={mode} service={service} chspace={chspace} chi={chi} target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} seconds={readSeconds} targetEventsMin={targetEventsMin} requested={requestedPath} resolved={resolvedPath} exists={File.Exists(resolvedPath)}",
            ProcessId = Environment.ProcessId
        }).ConfigureAwait(false);

        if (summary.CommonTsRoute?.RouteReadyForMode != true)
        {
            summary.Error = "common_ts_route_not_ready";
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = "common_ts_route_ready_gate_blocked",
                Message = $"routeReady={summary.CommonTsRoute?.RouteReadyForMode} issues={string.Join(',', summary.CommonTsRoute?.ValidationIssues ?? new List<string>())} action=bonDriver_open_not_started rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback",
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
            return summary;
        }

        await WriteProgressAsync(progressPath, new WorkerProgress
        {
            Timestamp = DateTimeOffset.Now,
            Version = Version,
            JobId = jobId,
            Mode = mode,
            Stage = "common_ts_route_ready_gate_passed",
            Message = $"routeReady=true action={mode}_ts_read_allowed service={service} target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback",
            ProcessId = Environment.ProcessId
        }).ConfigureAwait(false);

        if (!File.Exists(resolvedPath))
        {
            summary.Error = "BonDriver DLL not found.";
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = "integrated_tsread_failed",
                Message = summary.Error,
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
            return summary;
        }

        await CommonTsRouteModeExecutionGate.RunModeAsync(mode, resolvedPath, summary, async (stage, message) =>
        {
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = stage,
                Message = message,
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);



        if (mode == "epg-check"
            && job?.Metadata != null
            && job.Metadata.TryGetValue("preTuneEnabled", out var preTuneEnabled)
            && string.Equals(preTuneEnabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            var targetLabel = job.Metadata.TryGetValue("preTuneTargetDisplayLabel", out var target) && !string.IsNullOrWhiteSpace(target)
                ? target
                : "録画準備";
            job.Metadata["preTuneDisplayLabel"] = targetLabel;
            ApplyTaskbarTitle(mode, job, "pre_tune_state_transition");
            await WriteProgressAsync(progressPath, new WorkerProgress
            {
                Timestamp = DateTimeOffset.Now,
                Version = Version,
                JobId = jobId,
                Mode = mode,
                Stage = "pre_record_pretune_state_transition",
                Message = $"display={targetLabel} action=epg_check_to_record_pretune_same_worker rule=v0.9.94_chain_head_pretune_state_transition",
                ProcessId = Environment.ProcessId
            }).ConfigureAwait(false);
        }

        if (mode == "epg-check")
        {
            summary.EpgCheck = new EpgCheckProbeSummary
            {
                DbWrite = false,
                TargetServiceReady = summary.CommonTsRouteExecution?.TargetServiceReady == true || summary.TargetServiceEventsDecoded > 0,
                EitSeen = summary.EitSeen,
                TargetServiceEventsDecoded = summary.TargetServiceEventsDecoded,
                TargetServiceIntermediateEventsBuilt = summary.TargetServiceIntermediateEventsBuilt,
                TargetWaitResult = summary.TargetServiceEitWaitResult,
                Warning = summary.PsiMinimalOk ? string.Empty : $"PSI_WARNING_IGNORED_FOR_EPG_CHECK pat={summary.PatSeen} pmt={summary.PmtSeen} sdt={summary.SdtSeen} eit={summary.EitSeen}",
                ObservedEventKeys = summary.TargetServiceIntermediateEvents
                    .Take(8)
                    .Select(e => e.EventKey)
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .ToArray(),
                ObservedTitles = summary.TargetServiceIntermediateEvents
                    .Take(8)
                    .Select(e => e.Title)
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .ToArray()
            };
        }

        return summary;
    }

    private static string ResolveBonDriverPath(string requestedPath, string bonDriverName)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath) && Path.IsPathRooted(requestedPath))
        {
            return Path.GetFullPath(requestedPath);
        }

        var configuredFileName = Path.GetFileName(string.IsNullOrWhiteSpace(requestedPath) ? bonDriverName : requestedPath);
        if (string.IsNullOrWhiteSpace(configuredFileName)) return string.Empty;

        var configuredRelativePath = Path.Combine(AppContext.BaseDirectory, configuredFileName);
        return File.Exists(configuredRelativePath) ? Path.GetFullPath(configuredRelativePath) : configuredRelativePath;
    }

    private static async Task<bool> WaitForOptionalCancellationAsync(string? cancelPath, int keepAliveMs, string? progressPath, string jobId, string mode)
    {
        if (keepAliveMs <= 0)
        {
            return IsCancelRequested(cancelPath);
        }

        var deadline = DateTimeOffset.Now.AddMilliseconds(keepAliveMs);
        var lastProgress = DateTimeOffset.MinValue;
        while (DateTimeOffset.Now < deadline)
        {
            if (IsCancelRequested(cancelPath))
            {
                return true;
            }

            if ((DateTimeOffset.Now - lastProgress).TotalSeconds >= 5)
            {
                lastProgress = DateTimeOffset.Now;
                await WriteProgressAsync(progressPath, new WorkerProgress
                {
                    Timestamp = lastProgress,
                    Version = Version,
                    JobId = jobId,
                    Mode = mode,
                    Stage = "waiting",
                    Message = mode == "epg" ? "TvAIrEpgRec EPG contract keep-alive wait. No BonDriver access unless explicit route/runtime execution is requested; DirectRecorderBridge-derived common route must precede mode-specific behavior." : "TvAIrEpgRec staged execution shell keep-alive wait. Existing DirectRecorderBridge recording route remains untouched; common TS route facade boundary remains staged.",
                    ProcessId = Environment.ProcessId
                }).ConfigureAwait(false);
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return IsCancelRequested(cancelPath);
    }

    private static bool IsCancelRequested(string? cancelPath)
    {
        return !string.IsNullOrWhiteSpace(cancelPath) && File.Exists(cancelPath);
    }

    private static ExecutionLineageSummary BuildLineageSummary()
    {
        return new ExecutionLineageSummary
        {
            ExecutableName = "TvAIrEpgRec.exe",
            LineageSource = "DirectRecorderBridge logic lineage migrated into TvAIrEpgRec boundary",
            ExistingRecordRouteTouched = false,
            DirectRecorderBridgeStillRequired = true,
            DirectRecorderBridgeRemovalAllowed = false,
            RecordDecisionOwner = "TvAIr common allocation route",
            RecordExecutionOwner = "TvAIrEpgRec is allowed as production recording executable after v0.7.68 switch; DirectRecorderBridge remains as fallback/legacy reference",
            EpgExecutionOwner = "TvAIrEpgRec mode=epg",
            EpgCheckExecutionOwner = "TvAIrEpgRec mode=epg-check",
            ServiceIdentityRule = "NID/TSID/SID/chspace/chi; no station-name partial matching; no NEXT string search",
            ChainRecordingRule = "TvAIr decides chain recording; TvAIrEpgRec may later add only an execution module if mechanics differ from normal recording",
            SharedRouteRoot = "TvAIrEpgRec integrated common TS route: BonDriver LoadLibrary/Create/OpenTuner/SetChannel/NID/TSID/SID/PAT/PMT/SDT/TargetServiceReady/service-scoped TS/CloseTuner/Release/FreeLibrary before mode-specific record/epg/epg-check processing",
            ExistingRecordModule = "DirectRecorderBridge executable remains untouched as fallback/legacy reference; production record execution can be handled by TvAIrEpgRec mode=record after TvAIr common allocation decision",
            EpgModule = "TvAIrEpgRec mode=epg keeps EIT/ARIB/intermediate-model work only after the shared DirectRecorderBridge service route has selected/scoped the TS",
            EpgCheckModule = "Planned short timing-confirmation mode; DB write stays false",
            MigrationSafetyRule = "Do not modify DirectRecorderBridge files, do not spawn DirectRecorderBridge.exe from TvAIrEpgRec, do not switch production recording until integrated TvAIrEpgRec route is verified",
            Rule = "v0.8.04_configured_sid_only_import_policy"
        };
    }

    private static async Task WriteProgressAsync(string? path, WorkerProgress progress)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        EnsureParentDirectory(path);
        var line = JsonSerializer.Serialize(progress, JsonOptions).ReplaceLineEndings(string.Empty);
        await File.AppendAllTextAsync(path, line + Environment.NewLine).ConfigureAwait(false);
    }

    private static async Task WriteResultAsync(string? path, WorkerResult result)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        EnsureParentDirectory(path);
        var json = JsonSerializer.Serialize(result, ResultJsonOptions);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
    }

    private static void EnsureParentDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static bool IsEpgLikeMode(string? mode)
    {
        var normalized = NormalizeMode(mode);
        return normalized is "epg" or "epg-check";
    }


    private static List<RecordSegmentJob> NormalizeRecordSegments(List<RecordSegmentJob>? segments, string? fallbackOutputPath)
    {
        var normalized = (segments ?? new List<RecordSegmentJob>())
            .Where(x => !string.IsNullOrWhiteSpace(x.OutputPath))
            .Select(x =>
            {
                x.OutputPath = RecordingFileNameNormalizer.NormalizeOutputPathFileName(x.OutputPath);
                return x;
            })
            .OrderBy(x => x.SwitchAt == default ? x.StartTime : x.SwitchAt)
            .ToList();
        if (normalized.Count == 0 && !string.IsNullOrWhiteSpace(fallbackOutputPath))
        {
            normalized.Add(new RecordSegmentJob
            {
                ReservationId = 0,
                ServiceName = string.Empty,
                Title = string.Empty,
                StartTime = DateTime.MinValue,
                EndTime = DateTime.MaxValue,
                SwitchAt = DateTime.MinValue,
                OutputPath = fallbackOutputPath
            });
        }
        return normalized;
    }


    private sealed class RecordSegmentPlanFile
    {
        public List<RecordSegmentJob>? Segments { get; set; }
    }

    private static List<RecordSegmentJob> TryLoadRecordSegmentsFromPlan(string? planPath, string? fallbackOutputPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath)) return NormalizeRecordSegments(null, fallbackOutputPath);
            var json = File.ReadAllText(planPath);
            var plan = JsonSerializer.Deserialize<RecordSegmentPlanFile>(json, JsonOptions);
            return NormalizeRecordSegments(plan?.Segments, fallbackOutputPath);
        }
        catch
        {
            return NormalizeRecordSegments(null, fallbackOutputPath);
        }
    }

    private static string? NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;
        var m = mode.Trim().ToLowerInvariant();
        return m switch
        {
            "record" or "rec" or "recording" => "record",
            "epg" or "epg-run" or "epg_capture" => "epg",
            "epg-check" or "epgcheck" or "precheck" or "pre-record-check" => "epg-check",
            "runtime" or "shell" => "runtime",
            _ => m
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return Guid.NewGuid().ToString("N");
    }
}

internal sealed class CliOptions
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var result = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var keyValue = arg[2..];
            var separator = keyValue.IndexOf('=');
            if (separator >= 0)
            {
                result.Values[keyValue[..separator]] = keyValue[(separator + 1)..];
                continue;
            }

            var key = keyValue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result.Values[key] = args[++i];
            }
            else
            {
                result.Values[key] = "true";
            }
        }
        return result;
    }

    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public int GetInt(string key, int fallback)
    {
        return int.TryParse(Get(key), out var value) ? value : fallback;
    }

    public int? GetIntNullable(string key)
    {
        return int.TryParse(Get(key), out var value) ? value : null;
    }
}

internal static class NativeMethods
{
    internal const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;
    private static readonly List<IntPtr> PinnedCardReaderModules = new();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, char[] lpFilename, uint nSize);

    internal static string? GetModulePath(IntPtr module)
    {
        if (module == IntPtr.Zero) return null;
        var buffer = new char[32768];
        var copied = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
        return copied == 0 ? null : new string(buffer, 0, (int)copied);
    }

    internal static void RegisterPinnedCardReaderModule(IntPtr module)
    {
        if (module != IntPtr.Zero) PinnedCardReaderModules.Add(module);
    }
}


internal sealed class ExternalB25DecoderRuntime : IDisposable
{
    private readonly IntPtr _module;
    private readonly IntPtr _decoder;
    private readonly B25ReleaseDelegate? _release;
    private readonly B25DecodeDelegate? _decode;
    private readonly B25FlushDelegate? _flush;
    private bool _disposed;

    private ExternalB25DecoderRuntime(IntPtr module, IntPtr decoder, B25ReleaseDelegate release, B25DecodeDelegate decode, B25FlushDelegate flush, string loadedPath)
    {
        _module = module;
        _decoder = decoder;
        _release = release;
        _decode = decode;
        _flush = flush;
        LoadedPath = loadedPath;
        Available = true;
    }

    public bool Available { get; }
    public string LoadedPath { get; }

    public static ExternalB25DecoderRuntime? TryCreate(TsReadProbeSummary summary, Func<string, string, Task> progress)
    {
        var candidates = BuildCandidates(summary).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        summary.ExternalB25CandidatePath = candidates.FirstOrDefault();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            var module = NativeMethods.LoadLibraryExW(candidate, IntPtr.Zero, NativeMethods.LOAD_WITH_ALTERED_SEARCH_PATH);
            if (module == IntPtr.Zero)
            {
                progress("record_b25_decoder_probe", $"result=LOAD_NG path={candidate} lastError={Marshal.GetLastWin32Error()} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback").GetAwaiter().GetResult();
                continue;
            }

            try
            {
                var createPtr = NativeMethods.GetProcAddress(module, "CreateB25Decoder");
                if (createPtr == IntPtr.Zero)
                {
                    progress("record_b25_decoder_probe", $"result=EXPORT_MISSING path={candidate} export=CreateB25Decoder rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback").GetAwaiter().GetResult();
                    NativeMethods.FreeLibrary(module);
                    continue;
                }

                var create = Marshal.GetDelegateForFunctionPointer<CreateB25DecoderDelegate>(createPtr);
                var decoder = create();
                if (decoder == IntPtr.Zero)
                {
                    progress("record_b25_decoder_probe", $"result=CREATE_NULL path={candidate} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback").GetAwaiter().GetResult();
                    NativeMethods.FreeLibrary(module);
                    continue;
                }

                var vtbl = Marshal.ReadIntPtr(decoder);
                var initialize = Marshal.GetDelegateForFunctionPointer<B25InitializeDelegate>(Marshal.ReadIntPtr(vtbl, IntPtr.Size * 0));
                var release = Marshal.GetDelegateForFunctionPointer<B25ReleaseDelegate>(Marshal.ReadIntPtr(vtbl, IntPtr.Size * 1));
                var decode = Marshal.GetDelegateForFunctionPointer<B25DecodeDelegate>(Marshal.ReadIntPtr(vtbl, IntPtr.Size * 2));
                var flush = Marshal.GetDelegateForFunctionPointer<B25FlushDelegate>(Marshal.ReadIntPtr(vtbl, IntPtr.Size * 3));
                var initOk = initialize(decoder, 4) != 0;
                if (!initOk)
                {
                    try { release(decoder); } catch { }
                    progress("record_b25_decoder_probe", $"result=INITIALIZE_NG path={candidate} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback").GetAwaiter().GetResult();
                    NativeMethods.FreeLibrary(module);
                    continue;
                }

                var loadedPath = NativeMethods.GetModulePath(module) ?? candidate;
                summary.ExternalB25Available = true;
                summary.ExternalB25ProbeResult = "OK_APP_LOCAL_B25DECODER_LOADED";
                summary.ExternalB25LoadedPath = loadedPath;
                progress("record_b25_decoder_prepared", $"result=OK_APP_LOCAL_B25DECODER_LOADED path={loadedPath} cardReader={summary.CardReaderLoadedModulePath ?? "-"} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback").GetAwaiter().GetResult();
                return new ExternalB25DecoderRuntime(module, decoder, release, decode, flush, loadedPath);
            }
            catch (Exception ex)
            {
                progress("record_b25_decoder_probe", $"result=EXCEPTION path={candidate} error={ex.GetType().Name}:{ex.Message} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback").GetAwaiter().GetResult();
                try { NativeMethods.FreeLibrary(module); } catch { }
            }
        }

        summary.ExternalB25Available = false;
        summary.ExternalB25ProbeResult = "NG_APP_LOCAL_B25DECODER_NOT_AVAILABLE";
        progress("record_b25_decoder_prepared", $"result=NG_APP_LOCAL_B25DECODER_NOT_AVAILABLE candidates={string.Join("|", candidates)} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback").GetAwaiter().GetResult();
        return null;
    }

    private static IEnumerable<string> BuildCandidates(TsReadProbeSummary summary)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "B25Decoder.dll");
        if (!string.IsNullOrWhiteSpace(summary.CardReaderTvTestDirectory))
        {
            yield return Path.Combine(summary.CardReaderTvTestDirectory, "B25Decoder.dll");
        }
        if (!string.IsNullOrWhiteSpace(summary.ResolvedPath))
        {
            var bonDir = Path.GetDirectoryName(summary.ResolvedPath);
            if (!string.IsNullOrWhiteSpace(bonDir)) yield return Path.Combine(bonDir, "B25Decoder.dll");
        }
    }

    public DecodedBuffer Decode(byte[] source, int length)
    {
        if (_disposed || _decode is null || _decoder == IntPtr.Zero || length <= 0) return DecodedBuffer.Ng;
        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            var srcPtr = handle.AddrOfPinnedObject();
            var ok = _decode(_decoder, srcPtr, (uint)length, out var dst, out var dstSize) != 0;
            if (!ok || dst == IntPtr.Zero || dstSize == 0) return DecodedBuffer.Ng;
            var data = new byte[dstSize];
            Marshal.Copy(dst, data, 0, checked((int)dstSize));
            var passthrough = dst == srcPtr && dstSize == length;
            return new DecodedBuffer(true, data, passthrough);
        }
        catch
        {
            return DecodedBuffer.Ng;
        }
        finally
        {
            handle.Free();
        }
    }

    public DecodedBuffer Flush()
    {
        if (_disposed || _flush is null || _decoder == IntPtr.Zero) return DecodedBuffer.Ng;
        try
        {
            var ok = _flush(_decoder, out var dst, out var dstSize) != 0;
            if (!ok || dst == IntPtr.Zero || dstSize == 0) return new DecodedBuffer(ok, Array.Empty<byte>(), false);
            var data = new byte[dstSize];
            Marshal.Copy(dst, data, 0, checked((int)dstSize));
            return new DecodedBuffer(true, data, false);
        }
        catch
        {
            return DecodedBuffer.Ng;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_decoder != IntPtr.Zero) _release?.Invoke(_decoder); } catch { }
        try { if (_module != IntPtr.Zero) NativeMethods.FreeLibrary(_module); } catch { }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateB25DecoderDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int B25InitializeDelegate(IntPtr self, uint round);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void B25ReleaseDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int B25DecodeDelegate(IntPtr self, IntPtr src, uint srcSize, out IntPtr dst, out uint dstSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int B25FlushDelegate(IntPtr self, out IntPtr dst, out uint dstSize);
}


internal readonly record struct DecodedBuffer(bool Ok, byte[] Data, bool Passthrough)
{
    public static readonly DecodedBuffer Ng = new(false, Array.Empty<byte>(), false);
}

internal sealed class TvAIrEpgRecJob
{
    public string? JobId { get; set; }
    public string? Mode { get; set; }
    public string? Group { get; set; }
    public string? Tuner { get; set; }
    public string? Did { get; set; }
    public string? BonDriver { get; set; }
    public string? BonDriverPath { get; set; }
    public string? TvTestExecutablePath { get; set; }
    public string? ChannelsPath { get; set; }
    public string? OutputPath { get; set; }
    public string? ResultPath { get; set; }
    public string? ProgressPath { get; set; }
    public string? RuntimeStatsPath { get; set; }
    public string? CancelSignalPath { get; set; }
    public int? TsReadSeconds { get; set; }
    public List<EpgChannelJob>? Channels { get; set; }
    public List<RecordSegmentJob>? RecordSegments { get; set; }
    public string? SegmentPlanPath { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

internal sealed class EpgChannelJob
{
    public string? ServiceName { get; set; }
    public int? NetworkId { get; set; }
    public int? TransportStreamId { get; set; }
    public int? ServiceId { get; set; }
    public int? ChannelSpace { get; set; }
    public int? ChannelIndex { get; set; }
    public string? ChannelArgument { get; set; }
}

internal sealed class RecordSegmentJob
{
    public int ReservationId { get; set; }
    public string? ServiceName { get; set; }
    public string? Title { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public DateTime SwitchAt { get; set; }
    public string? OutputPath { get; set; }
}

internal sealed class EpgContractSummary
{
    public bool ContractOk { get; set; }
    public string Group { get; set; } = string.Empty;
    public string Tuner { get; set; } = string.Empty;
    public string Did { get; set; } = string.Empty;
    public string BonDriver { get; set; } = string.Empty;
    public int ChannelCount { get; set; }
    public List<EpgChannelJob> Channels { get; set; } = new();
    public bool BonDriverAccess { get; set; }
    public bool DbWrite { get; set; }
    public string Purpose { get; set; } = string.Empty;
}


internal sealed class BonDriverOpenProbeSummary
{
    public string RequestedBonDriver { get; set; } = string.Empty;
    public string RequestedPath { get; set; } = string.Empty;
    public string ResolvedPath { get; set; } = string.Empty;
    public bool LoadLibraryOk { get; set; }
    public bool CreateBonDriverOk { get; set; }
    public bool OpenTunerOk { get; set; }
    public bool CloseTunerCalled { get; set; }
    public bool ReleaseCalled { get; set; }
    public bool FreeLibraryCalled { get; set; }
    public int LastWin32Error { get; set; }
    public string? Error { get; set; }
    public string Purpose { get; set; } = string.Empty;
}

internal sealed class SetChannelProbeSummary
{
    public string RequestedBonDriver { get; set; } = string.Empty;
    public string RequestedPath { get; set; } = string.Empty;
    public string ResolvedPath { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public int ChannelSpace { get; set; }
    public int ChannelIndex { get; set; }
    public bool LoadLibraryOk { get; set; }
    public bool CreateBonDriverOk { get; set; }
    public bool OpenTunerOk { get; set; }
    public bool SetChannel2Called { get; set; }
    public bool SetChannel2Ok { get; set; }
    public bool SetChannel1Called { get; set; }
    public bool SetChannel1Ok { get; set; }
    public bool SetChannelOk { get; set; }
    public bool CloseTunerCalled { get; set; }
    public bool ReleaseCalled { get; set; }
    public bool FreeLibraryCalled { get; set; }
    public int LastWin32Error { get; set; }
    public string? Error { get; set; }
    public string Purpose { get; set; } = string.Empty;
}

internal sealed class TsReadProbeSummary
{
    public string RequestedBonDriver { get; set; } = string.Empty;
    public string RequestedPath { get; set; } = string.Empty;
    public string ResolvedPath { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public int ChannelSpace { get; set; }
    public int ChannelIndex { get; set; }
    public int ReadSeconds { get; set; }
    public string Variant { get; set; } = "ready-only";
    public int ReadyThreshold { get; set; } = 50;
    public int StopCooldownMs { get; set; } = 1500;
    public string Mode { get; set; } = string.Empty;
    public bool RecordWriteEnabled { get; set; }
    public string? RecordOutputPath { get; set; }
    public string? RecordStopSignalPath { get; set; }
    public List<RecordSegmentJob> RecordSegments { get; set; } = new();
    public int RecordCurrentSegmentReservationId { get; set; }
    public string RecordCurrentSegmentTitle { get; set; } = string.Empty;
    public string? CardReaderTvTestDirectory { get; set; }
    public string? CardReaderWinscardPath { get; set; }
    public bool CardReaderWinscardLoaded { get; set; }
    public string? CardReaderLoadedModulePath { get; set; }
    public string CardReaderReferenceResult { get; set; } = "NOT_PREPARED";
    public string CardReaderReferenceRule { get; set; } = string.Empty;
    public bool ExternalB25Available { get; set; }
    public string ExternalB25ProbeResult { get; set; } = "NOT_PREPARED";
    public string? ExternalB25CandidatePath { get; set; }
    public string? ExternalB25LoadedPath { get; set; }
    public long ExternalB25DecodeCalls { get; set; }
    public long ExternalB25DecodeOk { get; set; }
    public long ExternalB25DecodeNg { get; set; }
    public long ExternalB25DecodedBytes { get; set; }
    public long ExternalB25Passthrough { get; set; }
    public long ExternalB25FlushCalls { get; set; }
    public long ExternalB25FlushBytes { get; set; }
    public long ExternalB25BufferedEmpty { get; set; }
    public long ExternalB25RawFallbackSuppressed { get; set; }
    public long OutputPacketsAnalyzed { get; set; }
    public long OutputSyncErrors { get; set; }
    public long OutputTransportErrorPackets { get; set; }
    public long OutputScrambledLikePackets { get; set; }
    [JsonIgnore]
    public ExternalB25DecoderRuntime? RecordDescrambler { get; set; }
    public bool RecordOutputOpened { get; set; }
    public long RecordBytesWritten { get; set; }
    public long RecordChunksWritten { get; set; }
    public DateTimeOffset? RecordReadStartedAt { get; set; }
    public int RecordStartupRecoveryCount { get; set; }
    public string RecordStartupRecoveryAction { get; set; } = "none";
    public string RecordStartupRecoveryResult { get; set; } = "not_required";
    public bool RecordStartupFallbackFullTsActive { get; set; }
    public long RecordStartupFallbackFullTsBytes { get; set; }
    public bool RecordServiceScopeEnabled { get; set; }
    public bool RecordServiceScopeReady { get; set; }
    public int RecordTargetPmtPid { get; set; } = -1;
    public int RecordTargetPcrPid { get; set; } = -1;
    public List<int> RecordTargetStreamPids { get; set; } = new();
    public List<int> RecordWrittenServiceIds { get; set; } = new();
    public List<int> RecordExcludedServiceIds { get; set; } = new();
    public long RecordServiceScopeInputPackets { get; set; }
    public long RecordServiceScopeWrittenPackets { get; set; }
    public long RecordServiceScopeDroppedPackets { get; set; }
    public long RecordServiceScopeMediaPackets { get; set; }
    public long RecordServiceScopePatPackets { get; set; }
    public long RecordServiceScopeTargetPmtPackets { get; set; }
    public long RecordServiceScopeOtherPmtPacketsDropped { get; set; }
    public string RecordServiceScopeRule { get; set; } = string.Empty;
    public bool RecordStopRequested { get; set; }
    public string RecordStopReason { get; set; } = string.Empty;
    public string RecordShutdownStage { get; set; } = string.Empty;
    public DateTimeOffset? RecordStopAcceptedAt { get; set; }
    public DateTimeOffset? RecordShutdownStageAt { get; set; }
    public bool StopCooldownApplied { get; set; }
    public string StopCooldownReason { get; set; } = string.Empty;
    public uint LastReadyCount { get; set; }
    public bool ReadyThresholdReached { get; set; }
    public bool LoadLibraryOk { get; set; }
    public bool CreateBonDriverOk { get; set; }
    public bool OpenTunerOk { get; set; }
    public bool SetChannel2Called { get; set; }
    public bool SetChannel2Ok { get; set; }
    public bool SetChannel1Called { get; set; }
    public bool SetChannel1Ok { get; set; }
    public bool SetChannelOk { get; set; }
    public bool TsReadStarted { get; set; }
    public bool TsReadOk { get; set; }
    public long BytesRead { get; set; }
    public long ChunksRead { get; set; }
    public long GetTsCalls { get; set; }
    public long EmptyReads { get; set; }
    public long PacketsRead { get; set; }
    public long SyncErrors { get; set; }
    public long TransportErrorPackets { get; set; }
    public long ScrambledLikePackets { get; set; }
    public long ReadyCountSamples { get; set; }
    public long NonZeroReadyCountSamples { get; set; }
    public uint LastRemain { get; set; }
    public bool PsiMinimalProbe { get; set; }
    public bool PsiMinimalOk { get; set; }
    public bool PatSeen { get; set; }
    public bool PmtSeen { get; set; }
    public bool SdtSeen { get; set; }
    public bool EitSeen { get; set; }
    public long PatPackets { get; set; }
    public long PmtPackets { get; set; }
    public long SdtPackets { get; set; }
    public long EitPackets { get; set; }
    public long NitPackets { get; set; }
    public long PatSections { get; set; }
    public long PmtSections { get; set; }
    public long SdtSections { get; set; }
    public long EitSections { get; set; }
    public List<int> PatServiceIds { get; set; } = new();
    public List<int> PmtPids { get; set; } = new();
    public List<int> StreamPids { get; set; } = new();
    public Dictionary<int, long> PidCounts { get; set; } = new();
    public long InputCdtPid29Packets { get; set; }
    public long OutputCdtPid29Packets { get; set; }
    public bool EpgLogoPid29PreserveRequested { get; set; }
    public Dictionary<int, long> TableIdCounts { get; set; } = new();
    public bool EitMinimalDecodeProbe { get; set; }
    public bool EitMinimalDecodeOk { get; set; }
    public long EitEventsDecoded { get; set; }
    public long EitEventsWithShortEvent { get; set; }
    public long EitEventsWithDecodedShortEvent { get; set; }
    public bool AribShortEventDecodeProbe { get; set; }
    public bool AribShortEventDecodeOk { get; set; }
    public string AribDecoderName { get; set; } = string.Empty;
    public Dictionary<int, long> EitServiceIdCounts { get; set; } = new();
    public Dictionary<int, long> EitTableIdCounts { get; set; } = new();
    public Dictionary<string, long> EitTableIdServiceIdCounts { get; set; } = new();
    public Dictionary<string, long> EitTripletCounts { get; set; } = new();
    public Dictionary<string, long> EitActualOtherCounts { get; set; } = new();
    public Dictionary<string, long> EitTargetTransportServiceCounts { get; set; } = new();
    public Dictionary<int, long> DescriptorTagCounts { get; set; } = new();
    public List<EitEventMinimal> EitEvents { get; set; } = new();
    public bool TargetServiceEitPriorityProbe { get; set; }
    public bool TargetServiceEitWaitProbe { get; set; }
    public bool TargetServiceEitPriorityOk { get; set; }
    public string TargetServiceEitWaitResult { get; set; } = string.Empty;
    public int TargetOriginalNetworkId { get; set; }
    public int TargetTransportStreamId { get; set; }
    public int TargetServiceId { get; set; }
    public long TargetServiceEventsDecoded { get; set; }
    public int TargetServiceEventMin { get; set; } = 1;
    public long TargetServiceEventsWithShortEvent { get; set; }
    public long TargetServiceEventsWithDecodedShortEvent { get; set; }
    public long TargetMatchedFromTripletCount { get; set; }
    public long TargetMatchedEventListCount { get; set; }
    public Dictionary<int, long> TargetServiceDescriptorTagCounts { get; set; } = new();
    public bool EpgIntermediateModelProbe { get; set; }
    public bool EpgIntermediateModelOk { get; set; }
    public long EpgIntermediateEventsBuilt { get; set; }
    public long TargetServiceIntermediateEventsBuilt { get; set; }
    public List<EpgIntermediateEvent> EpgIntermediateEvents { get; set; } = new();
    public List<EpgIntermediateEvent> TargetServiceIntermediateEvents { get; set; } = new();
    [JsonIgnore]
    public Dictionary<int, PsiSectionAssemblyState> PsiAssemblers { get; } = new();
    public List<EitEventMinimal> TargetServiceEitEvents { get; set; } = new();
    public bool CloseTunerCalled { get; set; }
    public bool ReleaseCalled { get; set; }
    public bool FreeLibraryCalled { get; set; }
    public int LastWin32Error { get; set; }
    public string? Error { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public CommonTsRouteAttachment? CommonTsRoute { get; set; }
    public CommonTsRouteExecutionBoundary? CommonTsRouteExecution { get; set; }
    public EpgCheckProbeSummary? EpgCheck { get; set; }
}

internal sealed class EpgCheckProbeSummary
{
    public string Rule { get; set; } = "v0.7.56_record_runtime_safe_rebase";
    public string Mode { get; set; } = "epg-check";
    public bool DbWrite { get; set; }
    public string Purpose { get; set; } = "short target-program timing confirmation only";
    public bool TargetServiceReady { get; set; }
    public bool EitSeen { get; set; }
    public long TargetServiceEventsDecoded { get; set; }
    public long TargetServiceIntermediateEventsBuilt { get; set; }
    public string TargetWaitResult { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
    public string[] ObservedEventKeys { get; set; } = [];
    public string[] ObservedTitles { get; set; } = [];
}

internal sealed class PsiSectionAssemblyState
{
    public byte[] Buffer = new byte[4096];
    public int Length { get; set; }
    public int ExpectedLength { get; set; }

    public void EnsureCapacity(int required)
    {
        if (required <= Buffer.Length) return;
        Array.Resize(ref Buffer, Math.Max(required, Buffer.Length * 2));
    }

    public void Reset()
    {
        Length = 0;
        ExpectedLength = 0;
    }
}

internal sealed class EitEventMinimal
{
    public int TableId { get; set; }
    public int ServiceId { get; set; }
    public int TransportStreamId { get; set; }
    public int OriginalNetworkId { get; set; }
    public int EventId { get; set; }
    public string StartTimeBcd { get; set; } = string.Empty;
    public string DurationBcd { get; set; } = string.Empty;
    public int RunningStatus { get; set; }
    public bool FreeCaMode { get; set; }
    public int DescriptorLoopLength { get; set; }
    public bool ShortEventDescriptorSeen { get; set; }
    public int ShortEventNameRawLength { get; set; }
    public int ShortEventTextRawLength { get; set; }
    public string ShortEventNameRawHex { get; set; } = string.Empty;
    public string ShortEventTextRawHex { get; set; } = string.Empty;
    public string ShortEventName { get; set; } = string.Empty;
    public string ShortEventText { get; set; } = string.Empty;
    public bool ShortEventDecoded { get; set; }
    public string ShortEventDecodeSource { get; set; } = string.Empty;
    public List<int> DescriptorTags { get; set; } = new();
}


internal sealed class EpgIntermediateEvent
{
    public int NetworkId { get; set; }
    public int TransportStreamId { get; set; }
    public int ServiceId { get; set; }
    public int EventId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public bool ServiceNameResolved { get; set; }
    public string ServiceNameResolution { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ExtendedDescription { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string GenreCodes { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public string SourceTableId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public bool FreeCaMode { get; set; }
    public int RunningStatus { get; set; }
    public bool DbWriteReady { get; set; }
    public string Validation { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
}

internal static class AribStringDecoder
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DecodeAribWDelegate(byte[] data, int length);

    private static readonly object Sync = new();
    private static bool _initialized;
    private static IntPtr _library;
    private static DecodeAribWDelegate? _decode;
    private static string _source = string.Empty;

    public static string Decode(byte[] buffer, int offset, int length, out string source)
    {
        source = string.Empty;
        if (length <= 0 || offset < 0 || offset + length > buffer.Length) return string.Empty;
        var data = new byte[length];
        Buffer.BlockCopy(buffer, offset, data, 0, length);
        var native = TryDecodeNative(data, out source);
        if (!string.IsNullOrWhiteSpace(native)) return native;
        source = string.IsNullOrWhiteSpace(source) ? "hex-decode" : source + "+hex-decode";
        return DecodeAsciiFallback(data);
    }

    private static string TryDecodeNative(byte[] data, out string source)
    {
        source = string.Empty;
        try
        {
            EnsureNative();
            if (_decode is null)
            {
                source = _source;
                return string.Empty;
            }

            var ptr = _decode(data, data.Length);
            if (ptr == IntPtr.Zero)
            {
                source = _source + ":null";
                return string.Empty;
            }

            var text = Marshal.PtrToStringUni(ptr) ?? string.Empty;
            source = _source;
            return text.Trim();
        }
        catch (Exception ex)
        {
            source = "AribDecodeBridge:" + ex.GetType().Name;
            return string.Empty;
        }
    }

    private static void EnsureNative()
    {
        if (_initialized) return;
        lock (Sync)
        {
            if (_initialized) return;
            _initialized = true;
            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "native", arch, "AribDecodeBridge.dll"),
                Path.Combine(AppContext.BaseDirectory, "AribDecodeBridge.dll"),
                Path.Combine(AppContext.BaseDirectory, "..", "AribDecodeBridge", "bin", "Release", arch, "AribDecodeBridge.dll"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "AribDecodeBridge", "bin", "Release", arch, "AribDecodeBridge.dll")
            };

            foreach (var candidate in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(candidate)) continue;
                if (!NativeLibrary.TryLoad(candidate, out _library)) continue;
                if (NativeLibrary.TryGetExport(_library, "DecodeAribW", out var proc))
                {
                    _decode = Marshal.GetDelegateForFunctionPointer<DecodeAribWDelegate>(proc);
                    _source = "AribDecodeBridge";
                    return;
                }
                _source = "AribDecodeBridge:DecodeAribW_not_found";
                return;
            }
            _source = "AribDecodeBridge:not_found";
        }
    }

    private static string DecodeAsciiFallback(byte[] data)
    {
        var chars = new char[data.Length];
        var pos = 0;
        foreach (var b in data)
        {
            chars[pos++] = b >= 0x20 && b <= 0x7E ? (char)b : '□';
        }
        return new string(chars, 0, pos).Trim();
    }
}


internal static class CommonTsRouteModeExecutionGate
{
    public static async Task RunModeAsync(string mode, string bonDriverPath, TsReadProbeSummary summary, Func<string, string, Task> progress)
    {
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "epg" : mode.Trim().ToLowerInvariant();
        var boundary = new CommonTsRouteExecutionBoundary
        {
            Rule = "v0.8.04_configured_sid_only_import_policy",
            Mode = normalizedMode,
            RouteBeforeMode = summary.CommonTsRoute?.RouteBeforeMode == true,
            FacadeAttached = summary.CommonTsRoute?.FacadeAttached == true,
            RouteReadyForMode = summary.CommonTsRoute?.RouteReadyForMode == true,
            ProductionRecordRoute = summary.CommonTsRoute?.ProductionRecordRoute ?? "DirectRecorderBridge",
            ProductionRecordRouteSwitchAllowed = summary.CommonTsRoute?.ProductionRecordRouteSwitchAllowed == true,
            ExistingRecordRouteTouched = summary.CommonTsRoute?.ExistingRecordRouteTouched == true,
            DirectRecorderRuntimeCodeShared = summary.CommonTsRoute?.DirectRecorderRuntimeCodeShared == true,
            ServiceScopedTsRequiredBeforeMode = normalizedMode == "record",
            ModeSpecificStage = normalizedMode,
            BonDriverOpenAllowed = summary.CommonTsRoute?.RouteReadyForMode == true,
            SetChannelAllowed = summary.CommonTsRoute?.RouteReadyForMode == true,
            TsReadAllowed = summary.CommonTsRoute?.RouteReadyForMode == true,
            ExecutionOwner = normalizedMode == "epg-check"
                ? "TvAIrEpgRec.exe single-process DirectRecorderBridge-derived runtime; mode=epg-check; dbWrite=false timing confirmation only"
                : normalizedMode == "record"
                ? "TvAIrEpgRec.exe single-process DirectRecorderBridge-derived runtime; mode=record; TS write and stop boundary candidate"
                : "TvAIrEpgRec.exe single-process DirectRecorderBridge-derived runtime; mode=epg",
            SharedRouteOwnerLineage = summary.CommonTsRoute?.OwnerLineage ?? "DirectRecorderBridge imported runtime inside TvAIrEpgRec",
            StopLine = [
                "No EPG-only BonDriver/Open/SetChannel route may bypass this gate.",
                "No station-name partial matching.",
                "No NEXT string search.",
                "Do not switch production recording.",
                "Do not launch DirectRecorderBridge.exe from TvAIrEpgRec.",
                "SleepGuard/process monitoring target is TvAIrEpgRec.exe only."
            ]
        };

        summary.CommonTsRouteExecution = boundary;

        await progress("common_ts_route_execution_gate_enter", $"rule={boundary.Rule} mode={boundary.Mode} facadeAttached={boundary.FacadeAttached} routeReady={boundary.RouteReadyForMode} bonDriverOpenAllowed={boundary.BonDriverOpenAllowed} setChannelAllowed={boundary.SetChannelAllowed} tsReadAllowed={boundary.TsReadAllowed} productionRecordRouteSwitchAllowed={boundary.ProductionRecordRouteSwitchAllowed} existingRecordRouteTouched={boundary.ExistingRecordRouteTouched}").ConfigureAwait(false);
        await progress("common_ts_route_scope_policy", $"rule=v0.8.04_configured_sid_only_import_policy mode={normalizedMode} recordServiceFilterAllowed={(normalizedMode == "record")} epgTransportStreamScope={(normalizedMode == "epg")} epgCheckTargetEventScope={(normalizedMode == "epg-check")} target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} note=record_filters_must_not_be_shared_by_normal_epg").ConfigureAwait(false);

        if (!boundary.BonDriverOpenAllowed || !boundary.SetChannelAllowed || !boundary.TsReadAllowed)
        {
            boundary.Blocked = true;
            boundary.BlockReason = "common_ts_route_not_ready_for_" + normalizedMode;
            summary.Error = boundary.BlockReason;
            await progress("common_ts_route_execution_gate_blocked", $"rule={boundary.Rule} mode={normalizedMode} reason={boundary.BlockReason} action=bonDriver_open_setchannel_tsread_not_started").ConfigureAwait(false);
            return;
        }

        await progress("common_ts_route_execution_gate_passed", $"rule={boundary.Rule} action=run_single_process_directrec_runtime_under_tvairepgrec mode={normalizedMode} service={summary.ServiceName} target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId}").ConfigureAwait(false);

        await BonDriverNativeProbe.TsReadAsync(bonDriverPath, summary, async (stage, message) =>
        {
            await progress("common_route_" + stage, message).ConfigureAwait(false);
        }).ConfigureAwait(false);

        boundary.Completed = true;
        boundary.TsReadOk = summary.TsReadOk;
        boundary.OpenTunerOk = summary.OpenTunerOk;
        boundary.SetChannelOk = summary.SetChannelOk;
        boundary.TargetServiceReady = summary.TargetServiceEitPriorityOk || summary.TargetServiceEventsDecoded > 0;
        boundary.ServiceScopedPacketsObserved = summary.PacketsRead > 0 && summary.TransportErrorPackets < summary.PacketsRead;
        boundary.Result = summary.TsReadOk ? "OK" : (string.IsNullOrWhiteSpace(summary.Error) ? "NG" : summary.Error);

        await progress("common_ts_route_execution_gate_result", $"rule={boundary.Rule} result={boundary.Result} mode={normalizedMode} tsReadOk={boundary.TsReadOk} openTunerOk={boundary.OpenTunerOk} setChannelOk={boundary.SetChannelOk} targetServiceReady={boundary.TargetServiceReady} packets={summary.PacketsRead} chunks={summary.ChunksRead}").ConfigureAwait(false);
    }
}

internal sealed class CommonTsRouteExecutionBoundary
{
    public string Rule { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public bool RouteBeforeMode { get; set; }
    public bool FacadeAttached { get; set; }
    public bool RouteReadyForMode { get; set; }
    public bool ServiceScopedTsRequiredBeforeMode { get; set; }
    public string ModeSpecificStage { get; set; } = string.Empty;
    public string ExecutionOwner { get; set; } = string.Empty;
    public string SharedRouteOwnerLineage { get; set; } = string.Empty;
    public string ProductionRecordRoute { get; set; } = string.Empty;
    public bool ProductionRecordRouteSwitchAllowed { get; set; }
    public bool ExistingRecordRouteTouched { get; set; }
    public bool DirectRecorderRuntimeCodeShared { get; set; }
    public bool BonDriverOpenAllowed { get; set; }
    public bool SetChannelAllowed { get; set; }
    public bool TsReadAllowed { get; set; }
    public bool Blocked { get; set; }
    public string BlockReason { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public bool OpenTunerOk { get; set; }
    public bool SetChannelOk { get; set; }
    public bool TsReadOk { get; set; }
    public bool TargetServiceReady { get; set; }
    public bool ServiceScopedPacketsObserved { get; set; }
    public string Result { get; set; } = string.Empty;
    public string[] StopLine { get; set; } = [];
}

internal static class BonDriverNativeProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateBonDriverDelegate();

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int OpenTunerDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void CloseTunerDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int SetChannel1Delegate(IntPtr self, byte channel);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int SetChannel2Delegate(IntPtr self, uint space, uint channel);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint WaitTsStreamDelegate(IntPtr self, uint timeout);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint GetReadyCountDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetTsStreamPtrDelegate(IntPtr self, out IntPtr ppDst, out uint size, out uint remain);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int GetTsStreamBufferDelegate(IntPtr self, IntPtr pDst, ref uint size, ref uint remain);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ReleaseDelegate(IntPtr self);

    public static async Task OpenCloseAsync(string bonDriverPath, BonDriverOpenProbeSummary summary, Func<string, string, Task> progress)
    {
        IntPtr module = IntPtr.Zero;
        IntPtr driver = IntPtr.Zero;
        try
        {
            var previousCwd = Environment.CurrentDirectory;
            var bonDir = Path.GetDirectoryName(Path.GetFullPath(bonDriverPath));
            if (!string.IsNullOrWhiteSpace(bonDir)) Environment.CurrentDirectory = bonDir;
            try
            {
                module = LoadLibraryW(bonDriverPath);
                summary.LastWin32Error = Marshal.GetLastWin32Error();
                summary.LoadLibraryOk = module != IntPtr.Zero;
                await progress("bondriver_load_library", $"result={(summary.LoadLibraryOk ? "OK" : "NG")} path={bonDriverPath} lastError={summary.LastWin32Error}").ConfigureAwait(false);
                if (module == IntPtr.Zero)
                {
                    summary.Error = "LoadLibrary failed.";
                    return;
                }

                var createPtr = GetProcAddress(module, "CreateBonDriver");
                summary.LastWin32Error = Marshal.GetLastWin32Error();
                if (createPtr == IntPtr.Zero)
                {
                    summary.Error = "CreateBonDriver export not found.";
                    await progress("bondriver_create_export", $"result=NG lastError={summary.LastWin32Error}").ConfigureAwait(false);
                    return;
                }
                await progress("bondriver_create_export", "result=OK export=CreateBonDriver").ConfigureAwait(false);

                var create = Marshal.GetDelegateForFunctionPointer<CreateBonDriverDelegate>(createPtr);
                driver = create();
                summary.CreateBonDriverOk = driver != IntPtr.Zero;
                await progress("bondriver_create", $"result={(summary.CreateBonDriverOk ? "OK" : "NG")} ptr=0x{driver.ToInt64():X}").ConfigureAwait(false);
                if (driver == IntPtr.Zero)
                {
                    summary.Error = "CreateBonDriver returned null.";
                    return;
                }

                var vtbl = Marshal.ReadIntPtr(driver);
                var openPtr = Marshal.ReadIntPtr(vtbl, 0 * IntPtr.Size);
                var closePtr = Marshal.ReadIntPtr(vtbl, 1 * IntPtr.Size);
                var releasePtr = Marshal.ReadIntPtr(vtbl, 9 * IntPtr.Size);
                var open = Marshal.GetDelegateForFunctionPointer<OpenTunerDelegate>(openPtr);
                var close = Marshal.GetDelegateForFunctionPointer<CloseTunerDelegate>(closePtr);
                var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releasePtr);
                var openResult = open(driver);
                summary.OpenTunerOk = openResult != 0;
                await progress("bondriver_open_tuner", $"result={(summary.OpenTunerOk ? "OK" : "NG")} raw={openResult}").ConfigureAwait(false);

                try
                {
                    close(driver);
                    summary.CloseTunerCalled = true;
                    await progress("bondriver_close_tuner", "result=CALLED").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await progress("bondriver_close_tuner", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                }

                try
                {
                    release(driver);
                    summary.ReleaseCalled = true;
                    driver = IntPtr.Zero;
                    await progress("bondriver_release", "result=CALLED").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await progress("bondriver_release", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                }
            }
            finally
            {
                try { Environment.CurrentDirectory = previousCwd; } catch { }
            }
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
            await progress("bondriver_open_runtime_failed", $"type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            if (module != IntPtr.Zero)
            {
                try
                {
                    summary.FreeLibraryCalled = FreeLibrary(module);
                    await progress("bondriver_free_library", $"result={(summary.FreeLibraryCalled ? "CALLED" : "NG")}").ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    public static async Task SetChannelAsync(string bonDriverPath, SetChannelProbeSummary summary, Func<string, string, Task> progress)
    {
        IntPtr module = IntPtr.Zero;
        IntPtr driver = IntPtr.Zero;
        try
        {
            var previousCwd = Environment.CurrentDirectory;
            var bonDir = Path.GetDirectoryName(Path.GetFullPath(bonDriverPath));
            if (!string.IsNullOrWhiteSpace(bonDir)) Environment.CurrentDirectory = bonDir;
            try
            {
                module = LoadLibraryW(bonDriverPath);
                summary.LastWin32Error = Marshal.GetLastWin32Error();
                summary.LoadLibraryOk = module != IntPtr.Zero;
                await progress("setchannel_load_library", $"result={(summary.LoadLibraryOk ? "OK" : "NG")} path={bonDriverPath} lastError={summary.LastWin32Error}").ConfigureAwait(false);
                if (module == IntPtr.Zero)
                {
                    summary.Error = "LoadLibrary failed.";
                    return;
                }

                var createPtr = GetProcAddress(module, "CreateBonDriver");
                summary.LastWin32Error = Marshal.GetLastWin32Error();
                if (createPtr == IntPtr.Zero)
                {
                    summary.Error = "CreateBonDriver export not found.";
                    await progress("setchannel_create_export", $"result=NG lastError={summary.LastWin32Error}").ConfigureAwait(false);
                    return;
                }
                await progress("setchannel_create_export", "result=OK export=CreateBonDriver").ConfigureAwait(false);

                var create = Marshal.GetDelegateForFunctionPointer<CreateBonDriverDelegate>(createPtr);
                driver = create();
                summary.CreateBonDriverOk = driver != IntPtr.Zero;
                await progress("setchannel_create", $"result={(summary.CreateBonDriverOk ? "OK" : "NG")} ptr=0x{driver.ToInt64():X}").ConfigureAwait(false);
                if (driver == IntPtr.Zero)
                {
                    summary.Error = "CreateBonDriver returned null.";
                    return;
                }

                var vtbl = Marshal.ReadIntPtr(driver);
                var openPtr = Marshal.ReadIntPtr(vtbl, 0 * IntPtr.Size);
                var closePtr = Marshal.ReadIntPtr(vtbl, 1 * IntPtr.Size);
                var setChannel1Ptr = Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size);
                var releasePtr = Marshal.ReadIntPtr(vtbl, 9 * IntPtr.Size);
                var setChannel2Ptr = Marshal.ReadIntPtr(vtbl, 14 * IntPtr.Size);

                var open = Marshal.GetDelegateForFunctionPointer<OpenTunerDelegate>(openPtr);
                var close = Marshal.GetDelegateForFunctionPointer<CloseTunerDelegate>(closePtr);
                var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releasePtr);
                var openResult = open(driver);
                summary.OpenTunerOk = openResult != 0;
                await progress("setchannel_open_tuner", $"result={(summary.OpenTunerOk ? "OK" : "NG")} raw={openResult}").ConfigureAwait(false);
                if (!summary.OpenTunerOk)
                {
                    summary.Error = "OpenTuner failed.";
                    return;
                }

                try
                {
                    var set2 = Marshal.GetDelegateForFunctionPointer<SetChannel2Delegate>(setChannel2Ptr);
                    var set2Result = set2(driver, (uint)Math.Max(0, summary.ChannelSpace), (uint)Math.Max(0, summary.ChannelIndex));
                    summary.SetChannel2Called = true;
                    summary.SetChannel2Ok = set2Result != 0;
                    await progress("setchannel_set_channel2", $"result={(summary.SetChannel2Ok ? "OK" : "NG")} chspace={summary.ChannelSpace} chi={summary.ChannelIndex} raw={set2Result}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await progress("setchannel_set_channel2", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                }

                if (!summary.SetChannel2Ok)
                {
                    if (summary.ChannelIndex < 0 || summary.ChannelIndex > 255)
                    {
                        summary.Error = "SetChannel1 fallback skipped because channel index is outside byte range.";
                    }
                    else
                    {
                        try
                        {
                            var set1 = Marshal.GetDelegateForFunctionPointer<SetChannel1Delegate>(setChannel1Ptr);
                            var set1Result = set1(driver, (byte)summary.ChannelIndex);
                            summary.SetChannel1Called = true;
                            summary.SetChannel1Ok = set1Result != 0;
                            await progress("setchannel_set_channel1", $"result={(summary.SetChannel1Ok ? "OK" : "NG")} chi={summary.ChannelIndex} raw={set1Result}").ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await progress("setchannel_set_channel1", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                        }
                    }
                }

                summary.SetChannelOk = summary.SetChannel2Ok || summary.SetChannel1Ok;
                if (!summary.SetChannelOk && string.IsNullOrWhiteSpace(summary.Error))
                {
                    summary.Error = "SetChannel failed.";
                }

                try
                {
                    close(driver);
                    summary.CloseTunerCalled = true;
                    await progress("setchannel_close_tuner", "result=CALLED").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await progress("setchannel_close_tuner", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                }

                try
                {
                    release(driver);
                    summary.ReleaseCalled = true;
                    driver = IntPtr.Zero;
                    await progress("setchannel_release", "result=CALLED").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await progress("setchannel_release", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                }
            }
            finally
            {
                try { Environment.CurrentDirectory = previousCwd; } catch { }
            }
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
            await progress("setchannel_runtime_failed", $"type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            if (module != IntPtr.Zero)
            {
                try
                {
                    summary.FreeLibraryCalled = FreeLibrary(module);
                    await progress("setchannel_free_library", $"result={(summary.FreeLibraryCalled ? "CALLED" : "NG")}").ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private sealed class RecordOutputContext : IAsyncDisposable
    {
        private FileStream? stream;
        private readonly List<RecordSegmentJob> segments;
        private int currentIndex = -1;

        private RecordOutputContext(List<RecordSegmentJob> segments)
        {
            this.segments = segments;
        }

        public FileStream? Stream => stream;

        public static async Task<RecordOutputContext?> OpenAsync(TsReadProbeSummary summary, Func<string, string, Task> progress)
        {
            if (!summary.RecordWriteEnabled)
            {
                return null;
            }

            if (summary.RecordSegments.Count == 0)
            {
                summary.Error = "record_output_path_missing";
                await progress("record_write_open", "result=NG reason=record_output_path_missing").ConfigureAwait(false);
                return null;
            }

            var context = new RecordOutputContext(summary.RecordSegments);
            var now = DateTime.Now;
            var index = context.ResolveSegmentIndex(now);
            await context.OpenSegmentAsync(index, summary, progress, "initial").ConfigureAwait(false);
            return context;
        }

        public async Task SwitchIfNeededAsync(TsReadProbeSummary summary, Func<string, string, Task> progress)
        {
            if (segments.Count <= 1) return;
            var now = DateTimeOffset.Now.LocalDateTime;
            var nextIndex = ResolveSegmentIndex(now);
            if (nextIndex <= currentIndex) return;
            await OpenSegmentAsync(nextIndex, summary, progress, "time_boundary").ConfigureAwait(false);
        }

        private int ResolveSegmentIndex(DateTime now)
        {
            var index = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                var switchAt = segments[i].SwitchAt == default ? segments[i].StartTime : segments[i].SwitchAt;
                if (switchAt == default || now >= switchAt) index = i;
                else break;
            }
            return index;
        }

        private async Task OpenSegmentAsync(int index, TsReadProbeSummary summary, Func<string, string, Task> progress, string reason)
        {
            if (index < 0 || index >= segments.Count) return;
            var segment = segments[index];
            var path = Path.GetFullPath(segment.OutputPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(path)) return;
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

            if (stream is not null)
            {
                await stream.FlushAsync().ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                await progress("record_chain_segment_close", $"result=OK previousIndex={currentIndex} nextIndex={index} reason={reason} bytesWrittenTotal={summary.RecordBytesWritten} chunksWrittenTotal={summary.RecordChunksWritten} rule=v0.9.79_chain_restart_same_tuner_cooldown_bypass").ConfigureAwait(false);
            }

            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            currentIndex = index;
            summary.RecordOutputOpened = true;
            summary.RecordOutputPath = path;
            summary.RecordCurrentSegmentReservationId = segment.ReservationId;
            summary.RecordCurrentSegmentTitle = segment.Title ?? string.Empty;
            var stage = reason == "initial" ? "record_write_open" : "record_chain_segment_switch";
            await progress(stage, $"result=OK reason={reason} segmentIndex={index} reservation=R{segment.ReservationId} title={SafeProgress(segment.Title)} switchAt={segment.SwitchAt:yyyy-MM-dd HH:mm:ss} start={segment.StartTime:yyyy-MM-dd HH:mm:ss} end={segment.EndTime:yyyy-MM-dd HH:mm:ss} path={path} share=Read mode=Create segmentCount={segments.Count} rule=v0.9.79_chain_restart_same_tuner_cooldown_bypass").ConfigureAwait(false);
        }

        private static string SafeProgress(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "-";
            var s = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length <= 80 ? s : s[..80] + "…";
        }


        public async Task WriteAsync(byte[] buffer)
        {
            if (stream is null || buffer.Length == 0) return;
            await stream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> buffer)
        {
            if (stream is null || buffer.Length == 0) return;
            await stream.WriteAsync(buffer).ConfigureAwait(false);
        }

        public async Task FlushAsync()
        {
            if (stream is null) return;
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (stream is not null)
            {
                await stream.FlushAsync().ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                stream = null;
            }
        }
    }

    private static Task<RecordOutputContext?> OpenRecordOutputAsync(TsReadProbeSummary summary, Func<string, string, Task> progress)
        => RecordOutputContext.OpenAsync(summary, progress);

    private static async Task WriteRecordBufferAsync(RecordOutputContext? context, byte[] buffer, int length, TsReadProbeSummary summary, Func<string, string, Task> progress)
    {
        if (context is null || context.Stream is null || !summary.RecordWriteEnabled || length <= 0)
        {
            return;
        }

        await context.SwitchIfNeededAsync(summary, progress).ConfigureAwait(false);
        var stream = context.Stream;

        byte[] writeBytes;
        int writeLength;
        if (summary.RecordDescrambler is not null && summary.RecordDescrambler.Available)
        {
            var decoded = summary.RecordDescrambler.Decode(buffer, length);
            summary.ExternalB25DecodeCalls++;
            if (decoded.Ok && decoded.Data.Length > 0)
            {
                summary.ExternalB25DecodeOk++;
                summary.ExternalB25DecodedBytes += decoded.Data.Length;
                if (decoded.Passthrough)
                {
                    // Passthrough from an available B25 decoder means the decoder returned input-shaped data.
                    // Keep writing it for compatibility, but count the output layer separately so TvAIr can judge it.
                    summary.ExternalB25Passthrough++;
                }
                writeBytes = decoded.Data;
                writeLength = decoded.Data.Length;
            }
            else
            {
                // Do not fall back to raw TS after the B25 route has been selected.
                // The previous fallback mixed clear and scrambled packets into one recording; playback looked
                // normal for the first seconds and then DROP/CC errors rapidly increased.
                if (decoded.Ok) summary.ExternalB25BufferedEmpty++;
                else summary.ExternalB25DecodeNg++;
                summary.ExternalB25RawFallbackSuppressed++;
                if (summary.ExternalB25RawFallbackSuppressed == 1 || summary.ExternalB25RawFallbackSuppressed % 1000 == 0)
                {
                    await progress("record_b25_raw_fallback_suppressed", $"count={summary.ExternalB25RawFallbackSuppressed} decodeOk={decoded.Ok} decodedBytes={decoded.Data.Length} inputBytes={length} rule=v0.8.04_configured_sid_only_import_policy").ConfigureAwait(false);
                }
                return;
            }
        }
        else
        {
            writeBytes = buffer;
            writeLength = length;
        }

        if (string.Equals(summary.Mode, "record", StringComparison.OrdinalIgnoreCase) && summary.TargetServiceId > 0)
        {
            if (summary.RecordBytesWritten == 0 && !summary.RecordServiceScopeReady && summary.RecordReadStartedAt is DateTimeOffset recordStart)
            {
                var elapsedSec = (DateTimeOffset.Now - recordStart).TotalSeconds;
                if (elapsedSec >= 25 && !summary.RecordStartupFallbackFullTsActive)
                {
                    summary.RecordStartupFallbackFullTsActive = true;
                    summary.RecordStartupRecoveryAction = summary.RecordStartupRecoveryCount > 0
                        ? "setchannel_retry_and_fullts_fallback"
                        : "fullts_fallback_after_target_scope_wait";
                    summary.RecordStartupRecoveryResult = "fullts_fallback_active_until_target_scope_ready";
                    await progress("record_startup_fullts_fallback", $"result=ACTIVE elapsedSec={(int)elapsedSec} reason=target_service_scope_not_ready bytesRead={summary.BytesRead} chunksRead={summary.ChunksRead} scopeInputPackets={summary.RecordServiceScopeInputPackets} target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} rule=v0.9.92_record_start_gate_result_response_stabilize").ConfigureAwait(false);
                }
            }
            writeBytes = FilterRecordServiceScope(writeBytes, writeLength, summary);
            writeLength = writeBytes.Length;
            if (summary.RecordChunksWritten == 0 || summary.RecordChunksWritten % 1000 == 0)
            {
                await progress("record_service_scope", $"enabled={summary.RecordServiceScopeEnabled} ready={summary.RecordServiceScopeReady} target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetPmt=0x{Math.Max(0, summary.RecordTargetPmtPid):X} pcr=0x{Math.Max(0, summary.RecordTargetPcrPid):X} streamPids={string.Join(',', summary.RecordTargetStreamPids.Select(x => "0x" + x.ToString("X")))} writtenServiceIds={string.Join(',', summary.RecordWrittenServiceIds)} excludedServiceIds={string.Join(',', summary.RecordExcludedServiceIds)} inputPackets={summary.RecordServiceScopeInputPackets} writtenPackets={summary.RecordServiceScopeWrittenPackets} droppedPackets={summary.RecordServiceScopeDroppedPackets} mediaPackets={summary.RecordServiceScopeMediaPackets} rule=v0.8.04_configured_sid_only_import_policy").ConfigureAwait(false);
            }
            if (writeLength <= 0)
            {
                return;
            }
        }

        await context.SwitchIfNeededAsync(summary, progress).ConfigureAwait(false);
        await context.WriteAsync(writeBytes.AsMemory(0, writeLength)).ConfigureAwait(false);
        summary.RecordBytesWritten += writeLength;
        summary.RecordChunksWritten++;
        AnalyzeRecordOutputBuffer(writeBytes.AsMemory(0, writeLength), summary);
        if (summary.RecordChunksWritten == 1 || summary.RecordChunksWritten % 1000 == 0)
        {
            await progress("record_write_chunk", $"segmentReservation=R{summary.RecordCurrentSegmentReservationId} segmentTitle={summary.RecordCurrentSegmentTitle} outputPath={summary.RecordOutputPath} chunks={summary.RecordChunksWritten} bytesWritten={summary.RecordBytesWritten} outputPackets={summary.OutputPacketsAnalyzed} outputSyncErrors={summary.OutputSyncErrors} outputScrambled={summary.OutputScrambledLikePackets} externalB25Available={summary.ExternalB25Available} externalB25Loaded={summary.ExternalB25LoadedPath ?? "-"} externalB25DecodeCalls={summary.ExternalB25DecodeCalls} externalB25DecodeOk={summary.ExternalB25DecodeOk} externalB25Passthrough={summary.ExternalB25Passthrough} externalB25DecodeNg={summary.ExternalB25DecodeNg} externalB25BufferedEmpty={summary.ExternalB25BufferedEmpty} rawFallbackSuppressed={summary.ExternalB25RawFallbackSuppressed} serviceScopeReady={summary.RecordServiceScopeReady} targetPmt=0x{Math.Max(0, summary.RecordTargetPmtPid):X} targetStreams={string.Join(',', summary.RecordTargetStreamPids.Select(x => "0x" + x.ToString("X")))} mediaPackets={summary.RecordServiceScopeMediaPackets} rule=v0.8.04_configured_sid_only_import_policy").ConfigureAwait(false);
        }
    }

    private static byte[] FilterRecordServiceScope(byte[] buffer, int length, TsReadProbeSummary summary)
    {
        summary.RecordServiceScopeEnabled = true;
        summary.RecordServiceScopeRule = "v0.8.04_configured_sid_only_import_policy";
        if (length < 188) return Array.Empty<byte>();

        var packetCount = length / 188;
        // v0.7.96 dropped non-target service payload while leaving the original multi-service PAT in place.
        // TVTest then opened the first PAT service and saw no usable video.  Keep the service filter, but
        // only start writing after the target PMT/ES set is known and rewrite PAT packets to a single target SID.
        var output = new byte[(packetCount + 4) * 188];
        var outOffset = 0;

        for (var i = 0; i < packetCount; i++)
        {
            var offset = i * 188;
            if (buffer[offset] != 0x47)
            {
                continue;
            }

            summary.RecordServiceScopeInputPackets++;
            var pid = ((buffer[offset + 1] & 0x1F) << 8) | buffer[offset + 2];
            UpdateRecordServiceScopeFromPacket(buffer, offset, summary);

            if (!summary.RecordServiceScopeReady)
            {
                if (summary.RecordStartupFallbackFullTsActive)
                {
                    Buffer.BlockCopy(buffer, offset, output, outOffset, 188);
                    outOffset += 188;
                    summary.RecordStartupFallbackFullTsBytes += 188;
                    summary.RecordServiceScopeWrittenPackets++;
                    continue;
                }
                summary.RecordServiceScopeDroppedPackets++;
                if (summary.PmtPids.Contains(pid) && pid != summary.RecordTargetPmtPid) summary.RecordServiceScopeOtherPmtPacketsDropped++;
                continue;
            }

            // Ensure the output begins with a single-service PAT before the first target PMT/media packet.
            if (summary.RecordServiceScopePatPackets == 0 && pid != 0x0000)
            {
                var injectedPat = BuildSingleServicePatPacket(buffer, offset, summary, 0);
                if (injectedPat.Length == 188)
                {
                    Buffer.BlockCopy(injectedPat, 0, output, outOffset, 188);
                    outOffset += 188;
                    summary.RecordServiceScopeWrittenPackets++;
                    summary.RecordServiceScopePatPackets++;
                }
            }

            var write = ShouldWriteRecordServiceScopedPacket(pid, summary);
            if (write)
            {
                if (pid == 0x0000)
                {
                    var pat = BuildSingleServicePatPacket(buffer, offset, summary, buffer[offset + 3] & 0x0F);
                    if (pat.Length != 188)
                    {
                        summary.RecordServiceScopeDroppedPackets++;
                        continue;
                    }
                    Buffer.BlockCopy(pat, 0, output, outOffset, 188);
                }
                else
                {
                    Buffer.BlockCopy(buffer, offset, output, outOffset, 188);
                }
                outOffset += 188;
                summary.RecordServiceScopeWrittenPackets++;
                if (pid == 0x0000) summary.RecordServiceScopePatPackets++;
                if (pid == summary.RecordTargetPmtPid) summary.RecordServiceScopeTargetPmtPackets++;
                if (summary.RecordTargetStreamPids.Contains(pid) || pid == summary.RecordTargetPcrPid) summary.RecordServiceScopeMediaPackets++;
            }
            else
            {
                summary.RecordServiceScopeDroppedPackets++;
                if (summary.PmtPids.Contains(pid) && pid != summary.RecordTargetPmtPid) summary.RecordServiceScopeOtherPmtPacketsDropped++;
            }
        }

        if (outOffset == output.Length) return output;
        var trimmed = new byte[outOffset];
        Buffer.BlockCopy(output, 0, trimmed, 0, outOffset);
        return trimmed;
    }

    private static bool ShouldWriteRecordServiceScopedPacket(int pid, TsReadProbeSummary summary)
    {
        if (!summary.RecordServiceScopeReady) return false;
        if (pid == 0x0000) return true;        // PAT rewritten to the target SID only.
        if (pid == 0x0010) return true;        // NIT
        if (pid == 0x0011) return true;        // SDT/BAT
        if (pid == 0x0012) return true;        // EIT
        if (pid == 0x0014) return true;        // TDT/TOT

        // EPG/EPG-check outputs are also used by the TvAIr-side service-logo
        // extractor. ARIB logo data is carried by CDT on PID 0x0029; without
        // preserving this PID, SDT logo mappings can be observed but the actual
        // logo payload can never be saved. Keep this scoped to EPG-like modes so
        // normal recording output remains service-scoped and unchanged.
        var normalizedMode = summary.Mode?.Trim().ToLowerInvariant();
        if ((normalizedMode == "epg" || normalizedMode == "epg-check") && pid == 0x0029) return true;

        if (pid == summary.RecordTargetPmtPid) return true;
        if (pid == summary.RecordTargetPcrPid) return true;
        if (summary.RecordTargetStreamPids.Contains(pid)) return true;
        return false;
    }

    private static byte[] BuildSingleServicePatPacket(byte[] sourcePacket, int sourceOffset, TsReadProbeSummary summary, int continuityCounter)
    {
        if (summary.TargetServiceId <= 0 || summary.RecordTargetPmtPid <= 0) return Array.Empty<byte>();

        var packet = new byte[188];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = 0x40; // payload-unit-start, PID 0
        packet[2] = 0x00;
        packet[3] = (byte)(0x10 | (continuityCounter & 0x0F));
        packet[4] = 0x00; // pointer_field

        var section = new byte[16];
        var tsid = summary.TargetTransportStreamId > 0 ? summary.TargetTransportStreamId : summary.TargetOriginalNetworkId;
        section[0] = 0x00;
        section[1] = 0xB0;
        section[2] = 0x0D; // section_length: TSID(2)+version(1)+sec(1)+last(1)+program(4)+CRC(4)
        section[3] = (byte)((tsid >> 8) & 0xFF);
        section[4] = (byte)(tsid & 0xFF);
        section[5] = 0xC1; // current_next=1, version=0
        section[6] = 0x00;
        section[7] = 0x00;
        section[8] = (byte)((summary.TargetServiceId >> 8) & 0xFF);
        section[9] = (byte)(summary.TargetServiceId & 0xFF);
        section[10] = (byte)(0xE0 | ((summary.RecordTargetPmtPid >> 8) & 0x1F));
        section[11] = (byte)(summary.RecordTargetPmtPid & 0xFF);
        var crc = MpegCrc32(section, 0, 12);
        section[12] = (byte)((crc >> 24) & 0xFF);
        section[13] = (byte)((crc >> 16) & 0xFF);
        section[14] = (byte)((crc >> 8) & 0xFF);
        section[15] = (byte)(crc & 0xFF);
        Buffer.BlockCopy(section, 0, packet, 5, section.Length);
        return packet;
    }

    private static uint MpegCrc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (var i = 0; i < length; i++)
        {
            crc ^= (uint)data[offset + i] << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }
        }
        return crc;
    }

    private static void UpdateRecordServiceScopeFromPacket(byte[] buffer, int offset, TsReadProbeSummary summary)
    {
        var pid = ((buffer[offset + 1] & 0x1F) << 8) | buffer[offset + 2];
        var hasPayload = (buffer[offset + 3] & 0x10) != 0;
        if (!hasPayload) return;

        var payloadOffset = offset + 4;
        var hasAdaptation = (buffer[offset + 3] & 0x20) != 0;
        if (hasAdaptation)
        {
            if (payloadOffset >= offset + 188) return;
            var adaptationLength = buffer[payloadOffset];
            payloadOffset += 1 + adaptationLength;
        }
        if (payloadOffset >= offset + 188) return;
        var payloadUnitStart = (buffer[offset + 1] & 0x40) != 0;
        if (!payloadUnitStart) return;

        if (pid == 0x0000)
        {
            ParseRecordServiceScopePat(buffer, payloadOffset, offset + 188, summary);
        }
        else if (pid == summary.RecordTargetPmtPid && summary.RecordTargetPmtPid > 0)
        {
            ParseRecordServiceScopePmt(buffer, payloadOffset, offset + 188, summary);
        }
    }

    private static void ParseRecordServiceScopePat(byte[] buffer, int payloadOffset, int packetEnd, TsReadProbeSummary summary)
    {
        if (payloadOffset >= packetEnd) return;
        var pointer = buffer[payloadOffset];
        var sectionOffset = payloadOffset + 1 + pointer;
        if (sectionOffset + 12 > packetEnd) return;
        if (buffer[sectionOffset] != 0x00) return;
        var sectionLength = ((buffer[sectionOffset + 1] & 0x0F) << 8) | buffer[sectionOffset + 2];
        var entriesEnd = Math.Min(sectionOffset + 3 + sectionLength - 4, packetEnd);
        for (var pos = sectionOffset + 8; pos + 4 <= entriesEnd; pos += 4)
        {
            var serviceId = (buffer[pos] << 8) | buffer[pos + 1];
            var pmtPid = ((buffer[pos + 2] & 0x1F) << 8) | buffer[pos + 3];
            if (serviceId == 0) continue;
            if (serviceId == summary.TargetServiceId)
            {
                summary.RecordTargetPmtPid = pmtPid;
                AddUnique(summary.RecordWrittenServiceIds, serviceId);
            }
            else
            {
                AddUnique(summary.RecordExcludedServiceIds, serviceId);
            }
        }
    }

    private static void ParseRecordServiceScopePmt(byte[] buffer, int payloadOffset, int packetEnd, TsReadProbeSummary summary)
    {
        if (payloadOffset >= packetEnd) return;
        var pointer = buffer[payloadOffset];
        var sectionOffset = payloadOffset + 1 + pointer;
        if (sectionOffset + 16 > packetEnd) return;
        if (buffer[sectionOffset] != 0x02) return;
        var sectionLength = ((buffer[sectionOffset + 1] & 0x0F) << 8) | buffer[sectionOffset + 2];
        var sectionEnd = Math.Min(sectionOffset + 3 + sectionLength, packetEnd);
        if (sectionOffset + 12 > sectionEnd) return;
        var programNumber = (buffer[sectionOffset + 3] << 8) | buffer[sectionOffset + 4];
        if (programNumber != summary.TargetServiceId) return;
        summary.RecordTargetPcrPid = ((buffer[sectionOffset + 8] & 0x1F) << 8) | buffer[sectionOffset + 9];
        var programInfoLength = ((buffer[sectionOffset + 10] & 0x0F) << 8) | buffer[sectionOffset + 11];
        var pos = sectionOffset + 12 + programInfoLength;
        var entriesEnd = Math.Min(sectionOffset + 3 + sectionLength - 4, packetEnd);
        while (pos + 5 <= entriesEnd)
        {
            var streamPid = ((buffer[pos + 1] & 0x1F) << 8) | buffer[pos + 2];
            var esInfoLength = ((buffer[pos + 3] & 0x0F) << 8) | buffer[pos + 4];
            AddUnique(summary.RecordTargetStreamPids, streamPid);
            pos += 5 + esInfoLength;
        }
        summary.RecordServiceScopeReady = summary.RecordTargetPmtPid > 0 && summary.RecordTargetStreamPids.Count > 0;
    }

    private static bool ShouldTraceTsReadCall(bool isRecordMode, long callIndex, bool ok, uint size)
    {
        if (!isRecordMode)
        {
            return callIndex == 1 || callIndex % 100 == 0 || ok || size > 0;
        }

        // 録画本線では GetTsStream 1回ごとのログ出力自体が読み出し遅延になり、
        // BonDriver 側の内部バッファが詰まって先頭十数秒以降にDROPが増える。
        // そのため正常読み出しは強く間引き、異常または節目だけを出す。
        return callIndex == 1 || callIndex % 1000 == 0 || (!ok && callIndex % 100 == 0) || (size == 0 && callIndex % 100 == 0);
    }

    private static int RecordReadWaitMs(bool isRecordMode) => isRecordMode ? 10 : 100;

    private static int RecordIdleDelayMs(bool isRecordMode) => isRecordMode ? 1 : 10;

    private static bool IsRecordStopRequested(TsReadProbeSummary summary)
    {
        if (!summary.RecordWriteEnabled || string.IsNullOrWhiteSpace(summary.RecordStopSignalPath))
        {
            return false;
        }

        if (File.Exists(summary.RecordStopSignalPath))
        {
            summary.RecordStopRequested = true;
            summary.RecordStopReason = "stop_signal_file_detected";
            return true;
        }

        return false;
    }

    private static async Task MarkRecordShutdownStageAsync(TsReadProbeSummary summary, Func<string, string, Task> progress, string stage, string message)
    {
        if (!string.Equals(summary.Mode, "record", StringComparison.OrdinalIgnoreCase)) return;
        summary.RecordShutdownStage = stage;
        summary.RecordShutdownStageAt = DateTimeOffset.Now;
        await progress("record_shutdown_stage", $"stage={stage} {message} bytesWritten={summary.RecordBytesWritten} chunksWritten={summary.RecordChunksWritten} stopRequested={summary.RecordStopRequested} rule=v0.8.21_stop_root_trace_no_wait_extension").ConfigureAwait(false);
    }

    public static async Task TsReadAsync(string bonDriverPath, TsReadProbeSummary summary, Func<string, string, Task> progress)
    {
        IntPtr module = IntPtr.Zero;
        IntPtr driver = IntPtr.Zero;
        try
        {
            var previousCwd = Environment.CurrentDirectory;
            var bonDir = Path.GetDirectoryName(Path.GetFullPath(bonDriverPath));
            if (!string.IsNullOrWhiteSpace(bonDir)) Environment.CurrentDirectory = bonDir;
            try
            {
                module = LoadLibraryW(bonDriverPath);
                summary.LastWin32Error = Marshal.GetLastWin32Error();
                summary.LoadLibraryOk = module != IntPtr.Zero;
                await progress("tsvariant_load_library", $"result={(summary.LoadLibraryOk ? "OK" : "NG")} path={bonDriverPath} lastError={summary.LastWin32Error}").ConfigureAwait(false);
                if (module == IntPtr.Zero)
                {
                    summary.Error = "LoadLibrary failed.";
                    return;
                }

                var createPtr = GetProcAddress(module, "CreateBonDriver");
                summary.LastWin32Error = Marshal.GetLastWin32Error();
                if (createPtr == IntPtr.Zero)
                {
                    summary.Error = "CreateBonDriver export not found.";
                    await progress("tsvariant_create_export", $"result=NG lastError={summary.LastWin32Error}").ConfigureAwait(false);
                    return;
                }
                await progress("tsvariant_create_export", "result=OK export=CreateBonDriver").ConfigureAwait(false);

                var create = Marshal.GetDelegateForFunctionPointer<CreateBonDriverDelegate>(createPtr);
                driver = create();
                summary.CreateBonDriverOk = driver != IntPtr.Zero;
                await progress("tsvariant_create", $"result={(summary.CreateBonDriverOk ? "OK" : "NG")} ptr=0x{driver.ToInt64():X}").ConfigureAwait(false);
                if (driver == IntPtr.Zero)
                {
                    summary.Error = "CreateBonDriver returned null.";
                    return;
                }

                SetChannel2Delegate? set2ForStartupRecovery = null;
                SetChannel1Delegate? set1ForStartupRecovery = null;

                var vtbl = Marshal.ReadIntPtr(driver);
                var openPtr = Marshal.ReadIntPtr(vtbl, 0 * IntPtr.Size);
                var closePtr = Marshal.ReadIntPtr(vtbl, 1 * IntPtr.Size);
                var setChannel1Ptr = Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size);
                var waitTsPtr = Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size);
                var readyPtr = Marshal.ReadIntPtr(vtbl, 5 * IntPtr.Size);
                var getTsPtr6 = Marshal.ReadIntPtr(vtbl, 6 * IntPtr.Size);
                var getTsPtr7 = Marshal.ReadIntPtr(vtbl, 7 * IntPtr.Size);
                var releasePtr = Marshal.ReadIntPtr(vtbl, 9 * IntPtr.Size);
                var setChannel2Ptr = Marshal.ReadIntPtr(vtbl, 14 * IntPtr.Size);

                var open = Marshal.GetDelegateForFunctionPointer<OpenTunerDelegate>(openPtr);
                var close = Marshal.GetDelegateForFunctionPointer<CloseTunerDelegate>(closePtr);
                var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releasePtr);
                var openResult = open(driver);
                summary.OpenTunerOk = openResult != 0;
                await progress("tsvariant_open_tuner", $"result={(summary.OpenTunerOk ? "OK" : "NG")} raw={openResult}").ConfigureAwait(false);
                if (!summary.OpenTunerOk)
                {
                    summary.Error = "OpenTuner failed.";
                    return;
                }

                try
                {
                    var set2 = Marshal.GetDelegateForFunctionPointer<SetChannel2Delegate>(setChannel2Ptr);
                    set2ForStartupRecovery = set2;
                    var set2Result = set2(driver, (uint)Math.Max(0, summary.ChannelSpace), (uint)Math.Max(0, summary.ChannelIndex));
                    summary.SetChannel2Called = true;
                    summary.SetChannel2Ok = set2Result != 0;
                    await progress("tsvariant_set_channel2", $"result={(summary.SetChannel2Ok ? "OK" : "NG")} chspace={summary.ChannelSpace} chi={summary.ChannelIndex} raw={set2Result}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await progress("tsvariant_set_channel2", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                }

                if (!summary.SetChannel2Ok)
                {
                    if (summary.ChannelIndex >= 0 && summary.ChannelIndex <= 255)
                    {
                        try
                        {
                            var set1 = Marshal.GetDelegateForFunctionPointer<SetChannel1Delegate>(setChannel1Ptr);
                            set1ForStartupRecovery = set1;
                            var set1Result = set1(driver, (byte)summary.ChannelIndex);
                            summary.SetChannel1Called = true;
                            summary.SetChannel1Ok = set1Result != 0;
                            await progress("tsvariant_set_channel1", $"result={(summary.SetChannel1Ok ? "OK" : "NG")} chi={summary.ChannelIndex} raw={set1Result}").ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await progress("tsvariant_set_channel1", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                        }
                    }
                }

                summary.SetChannelOk = summary.SetChannel2Ok || summary.SetChannel1Ok;
                if (!summary.SetChannelOk)
                {
                    summary.Error = "SetChannel failed.";
                    await progress("tsvariant_summary", "result=NG reason=setchannel_failed").ConfigureAwait(false);
                }
                else
                {
                    var waitTs = Marshal.GetDelegateForFunctionPointer<WaitTsStreamDelegate>(waitTsPtr);
                    var getReady = Marshal.GetDelegateForFunctionPointer<GetReadyCountDelegate>(readyPtr);
                    summary.TsReadStarted = true;
                    var deadline = DateTimeOffset.Now.AddSeconds(Math.Clamp(summary.ReadSeconds, 1, 15));
                    await progress("tsvariant_begin", $"variant={summary.Variant} seconds={summary.ReadSeconds} service={summary.ServiceName} chspace={summary.ChannelSpace} chi={summary.ChannelIndex}").ConfigureAwait(false);

                    async Task<bool> TryRecoverRecordStartupZeroOutputAsync(string reason)
                    {
                        if (!string.Equals(summary.Mode, "record", StringComparison.OrdinalIgnoreCase)) return false;
                        if (summary.RecordBytesWritten > 0) return false;
                        if (summary.RecordStartupRecoveryCount >= 2) return false;
                        var startedAt = summary.RecordReadStartedAt ?? DateTimeOffset.Now;
                        var elapsedSec = (DateTimeOffset.Now - startedAt).TotalSeconds;
                        if (elapsedSec < (summary.RecordStartupRecoveryCount == 0 ? 15 : 30)) return false;

                        summary.RecordStartupRecoveryCount++;
                        summary.RecordStartupRecoveryAction = summary.RecordStartupFallbackFullTsActive
                            ? "setchannel_retry_and_fullts_fallback"
                            : "setchannel_retry_before_fullts_fallback";

                        summary.RecordTargetPmtPid = -1;
                        summary.RecordTargetPcrPid = -1;
                        summary.RecordTargetStreamPids.Clear();
                        summary.RecordServiceScopeReady = false;
                        summary.RecordServiceScopePatPackets = 0;
                        summary.RecordServiceScopeTargetPmtPackets = 0;
                        summary.RecordServiceScopeMediaPackets = 0;

                        var set2Raw = -1;
                        var set1Raw = -1;
                        try
                        {
                            if (set2ForStartupRecovery is not null)
                            {
                                set2Raw = set2ForStartupRecovery(driver, (uint)Math.Max(0, summary.ChannelSpace), (uint)Math.Max(0, summary.ChannelIndex));
                            }
                            if (set2Raw == 0 && set1ForStartupRecovery is not null && summary.ChannelIndex >= 0 && summary.ChannelIndex <= 255)
                            {
                                set1Raw = set1ForStartupRecovery(driver, (byte)summary.ChannelIndex);
                            }
                            summary.RecordStartupRecoveryResult = set2Raw != 0 || set1Raw != 0
                                ? "setchannel_retry_sent_waiting_for_target_service"
                                : "setchannel_retry_returned_ng";
                            await progress("record_startup_zero_write_recovery", $"attempt={summary.RecordStartupRecoveryCount} reason={reason} elapsedSec={(int)elapsedSec} bytesRead={summary.BytesRead} chunksRead={summary.ChunksRead} bytesWritten={summary.RecordBytesWritten} scopeReady={summary.RecordServiceScopeReady} targetPmt=0x{Math.Max(0, summary.RecordTargetPmtPid):X} set2Raw={set2Raw} set1Raw={set1Raw} action={summary.RecordStartupRecoveryAction} result={summary.RecordStartupRecoveryResult} rule=v0.9.92_record_start_gate_result_response_stabilize").ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            summary.RecordStartupRecoveryResult = "setchannel_retry_exception_" + ex.GetType().Name;
                            await progress("record_startup_zero_write_recovery", $"attempt={summary.RecordStartupRecoveryCount} reason={reason} elapsedSec={(int)elapsedSec} exception={ex.GetType().Name} message={ex.Message} rule=v0.9.92_record_start_gate_result_response_stabilize").ConfigureAwait(false);
                        }

                        await Task.Delay(750).ConfigureAwait(false);
                        return true;
                    }

                    if (summary.Variant.Equals("ready-only", StringComparison.OrdinalIgnoreCase))
                    {
                        while (DateTimeOffset.Now < deadline)
                        {
                            var waitResult = waitTs(driver, 100);
                            var ready = getReady(driver);
                            summary.ReadyCountSamples++;
                            if (ready > 0 || waitResult > 0) summary.NonZeroReadyCountSamples++;
                            if (summary.ReadyCountSamples == 1 || summary.ReadyCountSamples % 10 == 0 || ready > 0 || waitResult > 0)
                            {
                                await progress("tsvariant_ready_sample", $"sample={summary.ReadyCountSamples} wait={waitResult} ready={ready}").ConfigureAwait(false);
                            }
                            await Task.Delay(10).ConfigureAwait(false);
                        }

                        summary.TsReadOk = true;
                        summary.Error = null;
                        await progress("tsvariant_summary", $"result=OK variant=ready-only getTsCalled=False bytes=0 packets=0 readyNonZero={summary.NonZeroReadyCountSamples}/{summary.ReadyCountSamples}").ConfigureAwait(false);
                    }
                    else if (summary.Variant.Equals("pointer-vtable6-ready-threshold-continuous", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("buffer-vtable7-ready-threshold-continuous", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("pointer-vtable6-psi-minimal", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("buffer-vtable7-psi-minimal", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("pointer-vtable6-eit-minimal-decode", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("buffer-vtable7-eit-minimal-decode", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("pointer-vtable6-eit-target-service", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("buffer-vtable7-eit-target-service", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("pointer-vtable6-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("buffer-vtable7-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("pointer-vtable6-epg-normalize", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("pointer-vtable6-epg-normalize-gr-logo-opportunistic", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("pointer-vtable6-epg-normalize-logo-pid29", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("buffer-vtable7-epg-normalize", StringComparison.OrdinalIgnoreCase))
                    {
                        var isPointer = summary.Variant.Equals("pointer-vtable6-ready-threshold-continuous", StringComparison.OrdinalIgnoreCase)
                                        || summary.Variant.Equals("pointer-vtable6-psi-minimal", StringComparison.OrdinalIgnoreCase)
                                        || summary.Variant.Equals("pointer-vtable6-eit-minimal-decode", StringComparison.OrdinalIgnoreCase)
                                        || summary.Variant.Equals("pointer-vtable6-eit-target-service", StringComparison.OrdinalIgnoreCase)
                                        || summary.Variant.Equals("pointer-vtable6-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                                        || summary.Variant.Equals("pointer-vtable6-epg-normalize", StringComparison.OrdinalIgnoreCase)
                                        || summary.Variant.Equals("pointer-vtable6-epg-normalize-gr-logo-opportunistic", StringComparison.OrdinalIgnoreCase)
                                        || summary.Variant.Equals("pointer-vtable6-epg-normalize-logo-pid29", StringComparison.OrdinalIgnoreCase);
                        var isPsiMinimal = summary.Variant.Equals("pointer-vtable6-psi-minimal", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-psi-minimal", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-eit-minimal-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-eit-minimal-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-eit-target-service", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-eit-target-service", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize-gr-logo-opportunistic", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize-logo-pid29", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-epg-normalize", StringComparison.OrdinalIgnoreCase);
                        var isTargetServiceEit = summary.Variant.Equals("pointer-vtable6-eit-target-service", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-eit-target-service", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize-gr-logo-opportunistic", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize-logo-pid29", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-epg-normalize", StringComparison.OrdinalIgnoreCase);
                        var isEitMinimal = summary.Variant.Equals("pointer-vtable6-eit-minimal-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-eit-minimal-decode", StringComparison.OrdinalIgnoreCase)
                                           || isTargetServiceEit;
                        summary.PsiMinimalProbe = isPsiMinimal;
                        summary.EitMinimalDecodeProbe = isEitMinimal;
                        var isAribDecode = summary.Variant.Equals("pointer-vtable6-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-eit-arib-decode", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize-gr-logo-opportunistic", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize-logo-pid29", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-epg-normalize", StringComparison.OrdinalIgnoreCase);
                        var isEpgNormalize = summary.Variant.Equals("pointer-vtable6-epg-normalize", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize-gr-logo-opportunistic", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("pointer-vtable6-epg-normalize-logo-pid29", StringComparison.OrdinalIgnoreCase)
                                           || summary.Variant.Equals("buffer-vtable7-epg-normalize", StringComparison.OrdinalIgnoreCase);
                        var isFullNormalEpgMode = string.Equals(summary.Mode, "epg", StringComparison.OrdinalIgnoreCase);
                        summary.EpgIntermediateModelProbe = isEpgNormalize;
                        summary.EpgLogoPid29PreserveRequested = summary.Variant.Equals("pointer-vtable6-epg-normalize-logo-pid29", StringComparison.OrdinalIgnoreCase);
                        summary.AribShortEventDecodeProbe = isAribDecode;
                        summary.TargetServiceEitPriorityProbe = isTargetServiceEit;
                        summary.TargetServiceEitWaitProbe = isTargetServiceEit;
                        var waitDeadline = DateTimeOffset.Now.AddSeconds(Math.Clamp(summary.ReadSeconds, 1, 15));
                        uint ready = 0;
                        uint waitResult = 0;
                        var sample = 0;
                        while (DateTimeOffset.Now < waitDeadline)
                        {
                            waitResult = waitTs(driver, 100);
                            ready = getReady(driver);
                            summary.ReadyCountSamples++;
                            sample++;
                            summary.LastReadyCount = ready;
                            if (ready > 0 || waitResult > 0) summary.NonZeroReadyCountSamples++;
                            if (ready >= summary.ReadyThreshold)
                            {
                                summary.ReadyThresholdReached = true;
                                await progress("tsvariant_ready_threshold_reached", $"variant={summary.Variant} sample={sample} threshold={summary.ReadyThreshold} wait={waitResult} ready={ready}").ConfigureAwait(false);
                                break;
                            }
                            if (sample == 1 || sample % 10 == 0 || ready > 0 || waitResult > 0)
                            {
                                await progress("tsvariant_ready_threshold_wait", $"variant={summary.Variant} sample={sample} threshold={summary.ReadyThreshold} wait={waitResult} ready={ready}").ConfigureAwait(false);
                            }
                            await Task.Delay(10).ConfigureAwait(false);
                        }

                        await progress("tsvariant_getts_ready_gate", $"variant={summary.Variant} threshold={summary.ReadyThreshold} reached={summary.ReadyThresholdReached} samples={summary.ReadyCountSamples} wait={waitResult} ready={ready}").ConfigureAwait(false);

                        // v0.7.08: EPG normalize is a DB-preparation runtime, so it must wait long enough for the target
                        // service event instead of failing only because another service on the same TS arrived first.
                        var isRecordModeForLimit = string.Equals(summary.Mode, "record", StringComparison.OrdinalIgnoreCase);
                        var readSecondsLimit = isRecordModeForLimit
                            ? Math.Clamp(summary.ReadSeconds, 1, 12 * 60 * 60)
                            : isEpgNormalize
                                ? Math.Clamp(Math.Max(summary.ReadSeconds, 60), 1, 30 * 60)
                                : isTargetServiceEit
                                    ? Math.Clamp(summary.ReadSeconds, 1, 30 * 60)
                                    : Math.Clamp(summary.ReadSeconds, 1, 15);
                        var readDeadline = DateTimeOffset.Now.AddSeconds(readSecondsLimit);
                        var maxChunks = isRecordModeForLimit
                            ? long.MaxValue
                            : isTargetServiceEit ? Math.Max(200, readSecondsLimit * 120) : 200;
                        var callIndex = 0;
                        if (isRecordModeForLimit) summary.RecordReadStartedAt = DateTimeOffset.Now;
                        await using var recordStream = await OpenRecordOutputAsync(summary, progress).ConfigureAwait(false);
                        if (isPointer)
                        {
                            var getTs = Marshal.GetDelegateForFunctionPointer<GetTsStreamPtrDelegate>(getTsPtr6);
                            uint pendingRemain = 0;
                            while (DateTimeOffset.Now < readDeadline && summary.ChunksRead < maxChunks && !IsRecordStopRequested(summary))
                            {
                                callIndex++;
                                if (isRecordModeForLimit && pendingRemain > 0)
                                {
                                    waitResult = pendingRemain;
                                    ready = pendingRemain;
                                }
                                else
                                {
                                    waitResult = waitTs(driver, (uint)RecordReadWaitMs(isRecordModeForLimit));
                                    ready = getReady(driver);
                                }
                                summary.ReadyCountSamples++;
                                summary.LastReadyCount = ready;
                                if (ready > 0 || waitResult > 0) summary.NonZeroReadyCountSamples++;
                                if (ready == 0 && waitResult == 0)
                                {
                                    if (await TryRecoverRecordStartupZeroOutputAsync("no_ready_no_wait").ConfigureAwait(false))
                                    {
                                        pendingRemain = 0;
                                    }
                                    await Task.Delay(RecordIdleDelayMs(isRecordModeForLimit)).ConfigureAwait(false);
                                    continue;
                                }

                                IntPtr data = IntPtr.Zero;
                                uint size = 0;
                                uint remain = 0;
                                var traceThisCall = ShouldTraceTsReadCall(isRecordModeForLimit, callIndex, false, 0);
                                if (traceThisCall)
                                {
                                    await progress("tsvariant_getts_before", $"variant={summary.Variant} call={callIndex} wait={waitResult} ready={ready} method=pointer_overload_vtable6 continuous=True readyThreshold={summary.ReadyThreshold}").ConfigureAwait(false);
                                }
                                var ok = getTs(driver, out data, out size, out remain);
                                summary.GetTsCalls++;
                                summary.LastRemain = remain;
                                pendingRemain = remain;
                                traceThisCall = traceThisCall || ShouldTraceTsReadCall(isRecordModeForLimit, callIndex, ok != 0, size);
                                if (traceThisCall)
                                {
                                    await progress("tsvariant_getts_after", $"variant={summary.Variant} call={callIndex} ok={ok} data=0x{data.ToInt64():X} size={size} remain={remain}").ConfigureAwait(false);
                                }

                                if (ok != 0 && data != IntPtr.Zero && size > 0 && size <= 8 * 1024 * 1024)
                                {
                                    summary.ChunksRead++;
                                    summary.BytesRead += size;
                                    var copySize = CheckedTsCopySize(size);
                                    var buffer = new byte[copySize];
                                    Marshal.Copy(data, buffer, 0, copySize);
                                    AnalyzeTsBuffer(buffer, copySize, summary);
                                    await WriteRecordBufferAsync(recordStream, buffer, copySize, summary, progress).ConfigureAwait(false);
                                    if (summary.RecordBytesWritten == 0) await TryRecoverRecordStartupZeroOutputAsync("ts_read_but_no_record_output").ConfigureAwait(false);
                                    if (isTargetServiceEit && summary.TargetServiceEventsWithShortEvent >= summary.TargetServiceEventMin)
                                    {
                                        await progress("tsvariant_eit_target_service_found", $"result=OK target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetEvents={summary.TargetServiceEventsDecoded} targetShortEvent={summary.TargetServiceEventsWithShortEvent} targetEventsMin={summary.TargetServiceEventMin} targetEventsReached=True fullNormalEpgMode={isFullNormalEpgMode} call={callIndex} chunks={summary.ChunksRead}").ConfigureAwait(false);
                                        if (!isFullNormalEpgMode) break;
                                    }
                                    if (isTargetServiceEit && summary.TargetServiceEventsWithShortEvent > 0 && summary.TargetServiceEventsWithShortEvent < summary.TargetServiceEventMin && (callIndex == 1 || callIndex % 100 == 0))
                                    {
                                        await progress("tsvariant_eit_target_service_wait_more", $"result=WAIT target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetEvents={summary.TargetServiceEventsDecoded} targetShortEvent={summary.TargetServiceEventsWithShortEvent} targetEventsMin={summary.TargetServiceEventMin} targetEventsReached=False call={callIndex} chunks={summary.ChunksRead}").ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    summary.EmptyReads++;
                                    await TryRecoverRecordStartupZeroOutputAsync("getts_empty_or_invalid").ConfigureAwait(false);
                                }

                                if (remain == 0) await Task.Delay(RecordIdleDelayMs(isRecordModeForLimit)).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            var getTs = Marshal.GetDelegateForFunctionPointer<GetTsStreamBufferDelegate>(getTsPtr7);
                            var bufferSize = isRecordModeForLimit ? 2 * 1024 * 1024 : 512 * 1024;
                            var unmanagedBuffer = Marshal.AllocHGlobal(bufferSize);
                            uint pendingRemain = 0;
                            try
                            {
                                while (DateTimeOffset.Now < readDeadline && summary.ChunksRead < maxChunks && !IsRecordStopRequested(summary))
                                {
                                    callIndex++;
                                    if (isRecordModeForLimit && pendingRemain > 0)
                                    {
                                        waitResult = pendingRemain;
                                        ready = pendingRemain;
                                    }
                                    else
                                    {
                                        waitResult = waitTs(driver, (uint)RecordReadWaitMs(isRecordModeForLimit));
                                        ready = getReady(driver);
                                    }
                                    summary.ReadyCountSamples++;
                                    summary.LastReadyCount = ready;
                                    if (ready > 0 || waitResult > 0) summary.NonZeroReadyCountSamples++;
                                    if (ready == 0 && waitResult == 0)
                                    {
                                        if (await TryRecoverRecordStartupZeroOutputAsync("no_ready_no_wait").ConfigureAwait(false))
                                        {
                                            pendingRemain = 0;
                                        }
                                        await Task.Delay(RecordIdleDelayMs(isRecordModeForLimit)).ConfigureAwait(false);
                                        continue;
                                    }

                                    uint size = (uint)bufferSize;
                                    uint remain = 0;
                                    var traceThisCall = ShouldTraceTsReadCall(isRecordModeForLimit, callIndex, false, 0);
                                    if (traceThisCall)
                                    {
                                        await progress("tsvariant_getts_before", $"variant={summary.Variant} call={callIndex} wait={waitResult} ready={ready} method=buffer_overload_vtable7 continuous=True bufferSize={bufferSize} readyThreshold={summary.ReadyThreshold}").ConfigureAwait(false);
                                    }
                                    var ok = getTs(driver, unmanagedBuffer, ref size, ref remain);
                                    summary.GetTsCalls++;
                                    summary.LastRemain = remain;
                                    pendingRemain = remain;
                                    traceThisCall = traceThisCall || ShouldTraceTsReadCall(isRecordModeForLimit, callIndex, ok != 0, size);
                                    if (traceThisCall)
                                    {
                                        await progress("tsvariant_getts_after", $"variant={summary.Variant} call={callIndex} ok={ok} size={size} remain={remain}").ConfigureAwait(false);
                                    }

                                    if (ok != 0 && size > 0 && size <= bufferSize)
                                    {
                                        summary.ChunksRead++;
                                        summary.BytesRead += size;
                                        var copySize = CheckedTsCopySize(size);
                                        var buffer = new byte[copySize];
                                        Marshal.Copy(unmanagedBuffer, buffer, 0, copySize);
                                        AnalyzeTsBuffer(buffer, copySize, summary);
                                        await WriteRecordBufferAsync(recordStream, buffer, copySize, summary, progress).ConfigureAwait(false);
                                        if (summary.RecordBytesWritten == 0) await TryRecoverRecordStartupZeroOutputAsync("ts_read_but_no_record_output").ConfigureAwait(false);
                                        if (isTargetServiceEit && summary.TargetServiceEventsWithShortEvent >= summary.TargetServiceEventMin)
                                        {
                                            await progress("tsvariant_eit_target_service_found", $"result=OK target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetEvents={summary.TargetServiceEventsDecoded} targetShortEvent={summary.TargetServiceEventsWithShortEvent} targetEventsMin={summary.TargetServiceEventMin} targetEventsReached=True fullNormalEpgMode={isFullNormalEpgMode} call={callIndex} chunks={summary.ChunksRead}").ConfigureAwait(false);
                                            if (!isFullNormalEpgMode) break;
                                        }
                                    }
                                    else
                                    {
                                        summary.EmptyReads++;
                                        await TryRecoverRecordStartupZeroOutputAsync("getts_empty_or_invalid").ConfigureAwait(false);
                                    }

                                    if (remain == 0) await Task.Delay(RecordIdleDelayMs(isRecordModeForLimit)).ConfigureAwait(false);
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(unmanagedBuffer);
                            }
                        }

                        if (summary.RecordStopRequested)
                        {
                            summary.RecordStopAcceptedAt ??= DateTimeOffset.Now;
                            await progress("record_stop_signal_seen", $"result=STOP_REQUESTED reason={summary.RecordStopReason} bytesWritten={summary.RecordBytesWritten} chunksWritten={summary.RecordChunksWritten} rule=v0.8.21_stop_root_trace_no_wait_extension").ConfigureAwait(false);
                            await MarkRecordShutdownStageAsync(summary, progress, "stop_signal_accepted", $"reason={summary.RecordStopReason}").ConfigureAwait(false);
                        }

                        summary.PsiMinimalOk = !isPsiMinimal || (summary.PatSeen && summary.PmtSeen && summary.SdtSeen && summary.EitSeen);
                        if (isTargetServiceEit)
                        {
                            var targetKey = $"onid={summary.TargetOriginalNetworkId}/tsid={summary.TargetTransportStreamId}/sid={summary.TargetServiceId}";
                            summary.TargetMatchedFromTripletCount = summary.EitTripletCounts.TryGetValue(targetKey, out var targetTripletCount) ? targetTripletCount : 0;
                            summary.TargetMatchedEventListCount = summary.TargetServiceEventsDecoded;
                        }
                        summary.TargetServiceEitPriorityOk = !isTargetServiceEit || (summary.TargetServiceId > 0 && summary.TargetServiceEventsDecoded > 0);
                        if (isTargetServiceEit)
                        {
                            summary.TargetServiceEitWaitResult = summary.TargetServiceEitPriorityOk ? "TARGET_SEEN" : "TARGET_NOT_SEEN";
                        }
                        summary.AribShortEventDecodeOk = !isAribDecode || (summary.TargetServiceEventsWithDecodedShortEvent > 0 || summary.EitEventsWithDecodedShortEvent > 0);
                        summary.AribDecoderName = summary.TargetServiceEitEvents.FirstOrDefault(e => e.ShortEventDecoded)?.ShortEventDecodeSource
                                                  ?? summary.EitEvents.FirstOrDefault(e => e.ShortEventDecoded)?.ShortEventDecodeSource
                                                  ?? string.Empty;
                        if (isEpgNormalize)
                        {
                            BuildEpgIntermediateModel(summary);
                            summary.EpgIntermediateModelOk = summary.TargetServiceIntermediateEventsBuilt > 0;
                        }
                        summary.EitMinimalDecodeOk = !isEitMinimal || (summary.EitSeen && summary.EitEventsDecoded > 0 && summary.TargetServiceEitPriorityOk && summary.AribShortEventDecodeOk && (!isEpgNormalize || summary.EpgIntermediateModelOk));

                        var isEpgCheckMode = string.Equals(summary.CommonTsRoute?.Mode, "epg-check", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(summary.Purpose, "mode_epg_check_after_directrecbridge_common_ts_route_facade_dbwrite_false", StringComparison.OrdinalIgnoreCase);
                        var baseTsReadOk = summary.BytesRead > 0 && summary.PacketsRead > 0 && summary.SyncErrors == 0;
                        var epgCheckTargetOk = summary.TargetServiceEitPriorityOk
                            && string.Equals(summary.TargetServiceEitWaitResult, "TARGET_SEEN", StringComparison.OrdinalIgnoreCase)
                            && summary.EpgIntermediateModelOk
                            && summary.TargetServiceIntermediateEventsBuilt >= Math.Max(1, summary.TargetServiceEventMin);

                        var isRecordMode = string.Equals(summary.Mode, "record", StringComparison.OrdinalIgnoreCase);
                        summary.TsReadOk = isRecordMode
                            ? baseTsReadOk && summary.RecordBytesWritten > 0
                            : isEpgCheckMode && isEpgNormalize
                                ? baseTsReadOk && epgCheckTargetOk
                                : baseTsReadOk && summary.PsiMinimalOk && summary.EitMinimalDecodeOk;

                        if (!summary.TsReadOk)
                        {
                            if (isRecordMode)
                            {
                                summary.Error = !baseTsReadOk
                                    ? $"RECORD_TS_READ_INCOMPLETE bytes={summary.BytesRead} packets={summary.PacketsRead} syncErrors={summary.SyncErrors}"
                                    : $"RECORD_WRITE_EMPTY output={summary.RecordOutputPath ?? "-"} written={summary.RecordBytesWritten}";
                            }
                            else if (isEpgCheckMode && isEpgNormalize)
                            {
                                summary.Error = !baseTsReadOk
                                    ? $"EPG_CHECK_TS_READ_INCOMPLETE bytes={summary.BytesRead} packets={summary.PacketsRead} syncErrors={summary.SyncErrors}"
                                    : !summary.TargetServiceEitPriorityOk
                                        ? $"TARGET_NOT_SEEN target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetEvents={summary.TargetServiceEventsDecoded} allEvents={summary.EitEventsDecoded} shortEvent={summary.TargetServiceEventsWithShortEvent} readSeconds={readSecondsLimit} targetEventsMin={summary.TargetServiceEventMin} chunks={summary.ChunksRead} calls={summary.GetTsCalls} serviceIdCounts={FormatIntCounts(summary.EitServiceIdCounts)} tripletCounts={FormatStringCounts(summary.EitTripletCounts, 16)} actualOther={FormatStringCounts(summary.EitActualOtherCounts, 16)}"
                                        : !summary.EpgIntermediateModelOk
                                            ? $"EPG_CHECK_INTERMEDIATE_MODEL_NG target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetBuilt={summary.TargetServiceIntermediateEventsBuilt} targetEventsMin={summary.TargetServiceEventMin}"
                                            : $"EPG_CHECK_TARGET_EVENTS_NOT_ENOUGH target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetBuilt={summary.TargetServiceIntermediateEventsBuilt} targetEventsMin={summary.TargetServiceEventMin}";
                            }
                            else
                            {
                                summary.Error = isEitMinimal
                                    ? (isTargetServiceEit
                                        ? $"TARGET_NOT_SEEN target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetEvents={summary.TargetServiceEventsDecoded} allEvents={summary.EitEventsDecoded} shortEvent={summary.TargetServiceEventsWithShortEvent} readSeconds={readSecondsLimit} targetEventsMin={summary.TargetServiceEventMin} chunks={summary.ChunksRead} calls={summary.GetTsCalls} serviceIdCounts={FormatIntCounts(summary.EitServiceIdCounts)} tripletCounts={FormatStringCounts(summary.EitTripletCounts, 16)} actualOther={FormatStringCounts(summary.EitActualOtherCounts, 16)}"
                                        : $"EIT minimal decode did not produce events. eit={summary.EitSeen} events={summary.EitEventsDecoded} shortEvent={summary.EitEventsWithShortEvent}")
                                    : isPsiMinimal
                                        ? $"PSI minimal extraction did not include all required tables. pat={summary.PatSeen} pmt={summary.PmtSeen} sdt={summary.SdtSeen} eit={summary.EitSeen}"
                                        : $"GetTsStream continuous read did not produce valid TS. variant={summary.Variant} bytes={summary.BytesRead} packets={summary.PacketsRead} syncErrors={summary.SyncErrors}";
                            }
                        }
                        if (recordStream is not null && summary.RecordDescrambler is not null && summary.RecordDescrambler.Available)
                        {
                            await MarkRecordShutdownStageAsync(summary, progress, "b25_flush_enter", "phase=before_b25_flush").ConfigureAwait(false);
                            var flushed = summary.RecordDescrambler.Flush();
                            summary.ExternalB25FlushCalls++;
                            if (flushed.Ok && flushed.Data.Length > 0)
                            {
                                var flushBytes = flushed.Data;
                                if (string.Equals(summary.Mode, "record", StringComparison.OrdinalIgnoreCase) && summary.TargetServiceId > 0)
                                {
                                    flushBytes = FilterRecordServiceScope(flushBytes, flushBytes.Length, summary);
                                }
                                if (flushBytes.Length > 0)
                                {
                                    summary.ExternalB25FlushBytes += flushBytes.Length;
                                    await recordStream.WriteAsync(flushBytes).ConfigureAwait(false);
                                    summary.RecordBytesWritten += flushBytes.Length;
                                    AnalyzeRecordOutputBuffer(flushBytes, summary);
                                }
                            }
                            await progress("record_b25_flush", $"result={(flushed.Ok ? "OK" : "NG")} bytes={flushed.Data.Length} flushCalls={summary.ExternalB25FlushCalls} flushBytes={summary.ExternalB25FlushBytes} serviceScopeReady={summary.RecordServiceScopeReady} mediaPackets={summary.RecordServiceScopeMediaPackets} rule=v0.8.04_configured_sid_only_import_policy").ConfigureAwait(false);
                            await MarkRecordShutdownStageAsync(summary, progress, "b25_flush_exit", $"result={(flushed.Ok ? "OK" : "NG")} flushBytes={summary.ExternalB25FlushBytes}").ConfigureAwait(false);
                        }
                        if (recordStream is not null && string.Equals(summary.Mode, "record", StringComparison.OrdinalIgnoreCase))
                        {
                            await MarkRecordShutdownStageAsync(summary, progress, "record_stream_flush_enter", "phase=before_record_stream_flush").ConfigureAwait(false);
                            await recordStream.FlushAsync().ConfigureAwait(false);
                            await MarkRecordShutdownStageAsync(summary, progress, "record_stream_flush_exit", "result=OK").ConfigureAwait(false);
                        }

                        await progress("tsvariant_continuous_summary", $"result={(summary.BytesRead > 0 && summary.PacketsRead > 0 && summary.SyncErrors == 0 ? "OK" : "NG")} variant={summary.Variant} calls={summary.GetTsCalls} empty={summary.EmptyReads} bytes={summary.BytesRead} chunks={summary.ChunksRead} packets={summary.PacketsRead} syncErrors={summary.SyncErrors} tei={summary.TransportErrorPackets} scrambledLike={summary.ScrambledLikePackets} readyNonZero={summary.NonZeroReadyCountSamples}/{summary.ReadyCountSamples} threshold={summary.ReadyThreshold} reached={summary.ReadyThresholdReached} lastReady={summary.LastReadyCount} lastRemain={summary.LastRemain} readSecondsLimit={readSecondsLimit} maxChunks={maxChunks} externalB25Available={summary.ExternalB25Available} externalB25Loaded={summary.ExternalB25LoadedPath ?? "-"} externalB25DecodeCalls={summary.ExternalB25DecodeCalls} externalB25DecodeOk={summary.ExternalB25DecodeOk} externalB25Passthrough={summary.ExternalB25Passthrough} externalB25FlushBytes={summary.ExternalB25FlushBytes} cdtPid29In={summary.InputCdtPid29Packets} cdtPid29Out={summary.OutputCdtPid29Packets} logoPid29Preserve={summary.EpgLogoPid29PreserveRequested} rule=v0.9.43_gr_cdt_data_module_logo_save_bscs_no_deep").ConfigureAwait(false);
                        if (isPsiMinimal)
                        {
                            await progress("tsvariant_psi_minimal_summary", $"result={(summary.PsiMinimalOk ? "OK" : "NG")} pat={summary.PatSeen}/{summary.PatSections} pmt={summary.PmtSeen}/{summary.PmtSections} sdt={summary.SdtSeen}/{summary.SdtSections} eit={summary.EitSeen}/{summary.EitSections} nitPackets={summary.NitPackets} pmtPids={string.Join(',', summary.PmtPids)} streamPids={string.Join(',', summary.StreamPids.Take(16))}").ConfigureAwait(false);
                        }
                        if (isEitMinimal)
                        {
                            var first = summary.EitEvents.FirstOrDefault();
                            var firstText = first is null ? "-" : $"eventId={first.EventId} running={first.RunningStatus} freeCa={first.FreeCaMode} descLen={first.DescriptorLoopLength} short={first.ShortEventDescriptorSeen} nameRaw={first.ShortEventNameRawLength} textRaw={first.ShortEventTextRawLength}";
                            await progress("tsvariant_eit_minimal_decode_summary", $"result={(summary.EitMinimalDecodeOk ? "OK" : "NG")} events={summary.EitEventsDecoded} shortEvent={summary.EitEventsWithShortEvent} samples={summary.EitEvents.Count} serviceIdCounts={FormatIntCounts(summary.EitServiceIdCounts)} tableIdCounts={FormatIntCounts(summary.EitTableIdCounts)} actualOther={FormatStringCounts(summary.EitActualOtherCounts, 16)} first={firstText}").ConfigureAwait(false);
                            await progress("tsvariant_eit_service_id_map", $"tableServiceCounts={FormatStringCounts(summary.EitTableIdServiceIdCounts, 24)} tripletCounts={FormatStringCounts(summary.EitTripletCounts, 24)} targetTsServiceCounts={FormatStringCounts(summary.EitTargetTransportServiceCounts, 24)}").ConfigureAwait(false);
                            if (isTargetServiceEit)
                            {
                                var targetFirst = summary.TargetServiceEitEvents.FirstOrDefault();
                                var targetFirstText = targetFirst is null ? "-" : $"eventId={targetFirst.EventId} running={targetFirst.RunningStatus} freeCa={targetFirst.FreeCaMode} descLen={targetFirst.DescriptorLoopLength} short={targetFirst.ShortEventDescriptorSeen} nameRaw={targetFirst.ShortEventNameRawLength} textRaw={targetFirst.ShortEventTextRawLength}";
                                await progress("tsvariant_eit_target_service_summary", $"result={(summary.TargetServiceEitPriorityOk ? "OK" : "TARGET_NOT_SEEN")} waitResult={summary.TargetServiceEitWaitResult} target={summary.TargetOriginalNetworkId}/{summary.TargetTransportStreamId}/{summary.TargetServiceId} targetEvents={summary.TargetServiceEventsDecoded} targetShortEvent={summary.TargetServiceEventsWithShortEvent} targetEventsMin={summary.TargetServiceEventMin} tripletMatched={summary.TargetMatchedFromTripletCount} eventListMatched={summary.TargetMatchedEventListCount} samples={summary.TargetServiceEitEvents.Count} serviceIdCounts={FormatIntCounts(summary.EitServiceIdCounts)} tableServiceCounts={FormatStringCounts(summary.EitTableIdServiceIdCounts, 16)} tripletCounts={FormatStringCounts(summary.EitTripletCounts, 16)} actualOther={FormatStringCounts(summary.EitActualOtherCounts, 16)} targetTsServiceCounts={FormatStringCounts(summary.EitTargetTransportServiceCounts, 16)} first={targetFirstText}").ConfigureAwait(false);
                                if (isAribDecode)
                                {
                                    var decodedName = targetFirst?.ShortEventName ?? string.Empty;
                                    var decodedText = targetFirst?.ShortEventText ?? string.Empty;
                                    await progress("tsvariant_arib_short_event_decode_summary", $"result={(summary.AribShortEventDecodeOk ? "OK" : "NG")} decoder={summary.AribDecoderName} targetDecoded={summary.TargetServiceEventsWithDecodedShortEvent}/{summary.TargetServiceEventsWithShortEvent} allDecoded={summary.EitEventsWithDecodedShortEvent}/{summary.EitEventsWithShortEvent} name={decodedName} text={decodedText}").ConfigureAwait(false);
                                    if (isEpgNormalize)
                                    {
                                        var normalized = summary.TargetServiceIntermediateEvents.FirstOrDefault();
                                        var normalizedText = normalized is null ? "-" : $"key={normalized.EventKey} start={normalized.StartTime} end={normalized.EndTime} duration={normalized.DurationSeconds} title={normalized.Title} dbReady={normalized.DbWriteReady} validation={normalized.Validation}";
                                        await progress("tsvariant_epg_intermediate_model_summary", $"result={(summary.EpgIntermediateModelOk ? "OK" : "NG")} targetBuilt={summary.TargetServiceIntermediateEventsBuilt} allBuilt={summary.EpgIntermediateEventsBuilt} first={normalizedText}").ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                    }
                    else if (summary.Variant.Equals("pointer-vtable6-ready-threshold-single", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("buffer-vtable7-ready-threshold-single", StringComparison.OrdinalIgnoreCase))
                    {
                        var isPointer = summary.Variant.Equals("pointer-vtable6-ready-threshold-single", StringComparison.OrdinalIgnoreCase);
                        var waitDeadline = DateTimeOffset.Now.AddSeconds(Math.Clamp(summary.ReadSeconds, 1, 15));
                        uint ready = 0;
                        uint waitResult = 0;
                        var sample = 0;
                        while (DateTimeOffset.Now < waitDeadline)
                        {
                            waitResult = waitTs(driver, 100);
                            ready = getReady(driver);
                            summary.ReadyCountSamples++;
                            sample++;
                            summary.LastReadyCount = ready;
                            if (ready > 0 || waitResult > 0) summary.NonZeroReadyCountSamples++;
                            if (ready >= summary.ReadyThreshold)
                            {
                                summary.ReadyThresholdReached = true;
                                await progress("tsvariant_ready_threshold_reached", $"variant={summary.Variant} sample={sample} threshold={summary.ReadyThreshold} wait={waitResult} ready={ready}").ConfigureAwait(false);
                                break;
                            }
                            if (sample == 1 || sample % 10 == 0 || ready > 0 || waitResult > 0)
                            {
                                await progress("tsvariant_ready_threshold_wait", $"variant={summary.Variant} sample={sample} threshold={summary.ReadyThreshold} wait={waitResult} ready={ready}").ConfigureAwait(false);
                            }
                            await Task.Delay(10).ConfigureAwait(false);
                        }

                        await progress("tsvariant_getts_ready_gate", $"variant={summary.Variant} threshold={summary.ReadyThreshold} reached={summary.ReadyThresholdReached} samples={summary.ReadyCountSamples} wait={waitResult} ready={ready}").ConfigureAwait(false);

                        if (isPointer)
                        {
                            var getTs = Marshal.GetDelegateForFunctionPointer<GetTsStreamPtrDelegate>(getTsPtr6);
                            await progress("tsvariant_getts_before", $"variant={summary.Variant} wait={waitResult} ready={ready} method=pointer_overload_vtable6 singleCall=True readyThreshold={summary.ReadyThreshold}").ConfigureAwait(false);
                            IntPtr data = IntPtr.Zero;
                            uint size = 0;
                            uint remain = 0;
                            var ok = getTs(driver, out data, out size, out remain);
                            summary.LastRemain = remain;
                            await progress("tsvariant_getts_after", $"variant={summary.Variant} ok={ok} data=0x{data.ToInt64():X} size={size} remain={remain}").ConfigureAwait(false);

                            if (ok != 0 && data != IntPtr.Zero && size > 0 && size <= 8 * 1024 * 1024)
                            {
                                summary.ChunksRead++;
                                summary.BytesRead += size;
                                var copySize = CheckedTsCopySize(size);
                                var buffer = new byte[copySize];
                                Marshal.Copy(data, buffer, 0, copySize);
                                AnalyzeTsBuffer(buffer, copySize, summary);
                            }
                            summary.TsReadOk = ok != 0 && summary.BytesRead > 0;
                            if (!summary.TsReadOk) summary.Error = $"GetTsStream ready-threshold single-call returned false or empty. variant={summary.Variant} ready={ready} threshold={summary.ReadyThreshold}";
                        }
                        else
                        {
                            var getTs = Marshal.GetDelegateForFunctionPointer<GetTsStreamBufferDelegate>(getTsPtr7);
                            var bufferSize = 512 * 1024;
                            var unmanagedBuffer = Marshal.AllocHGlobal(bufferSize);
                            try
                            {
                                uint size = (uint)bufferSize;
                                uint remain = 0;
                                await progress("tsvariant_getts_before", $"variant={summary.Variant} wait={waitResult} ready={ready} method=buffer_overload_vtable7 singleCall=True bufferSize={bufferSize} readyThreshold={summary.ReadyThreshold}").ConfigureAwait(false);
                                var ok = getTs(driver, unmanagedBuffer, ref size, ref remain);
                                summary.LastRemain = remain;
                                await progress("tsvariant_getts_after", $"variant={summary.Variant} ok={ok} size={size} remain={remain}").ConfigureAwait(false);
                                if (ok != 0 && size > 0 && size <= bufferSize)
                                {
                                    summary.ChunksRead++;
                                    summary.BytesRead += size;
                                    var copySize = CheckedTsCopySize(size);
                                    var buffer = new byte[copySize];
                                    Marshal.Copy(unmanagedBuffer, buffer, 0, copySize);
                                    AnalyzeTsBuffer(buffer, copySize, summary);
                                }
                                summary.TsReadOk = ok != 0 && summary.BytesRead > 0;
                                if (!summary.TsReadOk) summary.Error = $"GetTsStream ready-threshold buffer single-call returned false or empty. ready={ready} threshold={summary.ReadyThreshold}";
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(unmanagedBuffer);
                            }
                        }

                        await progress("tsvariant_summary", $"result={(summary.TsReadOk ? "OK" : "NG")} variant={summary.Variant} bytes={summary.BytesRead} chunks={summary.ChunksRead} packets={summary.PacketsRead} syncErrors={summary.SyncErrors} readyNonZero={summary.NonZeroReadyCountSamples}/{summary.ReadyCountSamples} threshold={summary.ReadyThreshold} reached={summary.ReadyThresholdReached} lastReady={summary.LastReadyCount}").ConfigureAwait(false);
                    }
                    else if (summary.Variant.Equals("pointer-vtable6-single", StringComparison.OrdinalIgnoreCase)
                             || summary.Variant.Equals("pointer-vtable7-single", StringComparison.OrdinalIgnoreCase))
                    {
                        var vtableIndex = summary.Variant.Equals("pointer-vtable6-single", StringComparison.OrdinalIgnoreCase) ? 6 : 7;
                        var getTsPtr = vtableIndex == 6 ? getTsPtr6 : getTsPtr7;
                        var getTs = Marshal.GetDelegateForFunctionPointer<GetTsStreamPtrDelegate>(getTsPtr);
                        var waitResult = waitTs(driver, 100);
                        var ready = getReady(driver);
                        summary.ReadyCountSamples++;
                        if (ready > 0 || waitResult > 0) summary.NonZeroReadyCountSamples++;
                        await progress("tsvariant_getts_before", $"variant={summary.Variant} wait={waitResult} ready={ready} method=pointer_overload_vtable{vtableIndex} singleCall=True").ConfigureAwait(false);

                        IntPtr data = IntPtr.Zero;
                        uint size = 0;
                        uint remain = 0;
                        var ok = getTs(driver, out data, out size, out remain);
                        summary.LastRemain = remain;
                        await progress("tsvariant_getts_after", $"variant={summary.Variant} ok={ok} data=0x{data.ToInt64():X} size={size} remain={remain}").ConfigureAwait(false);

                        if (ok != 0 && data != IntPtr.Zero && size > 0 && size <= 8 * 1024 * 1024)
                        {
                            summary.ChunksRead++;
                            summary.BytesRead += size;
                            var copySize = CheckedTsCopySize(size);
                            var buffer = new byte[copySize];
                            Marshal.Copy(data, buffer, 0, copySize);
                            AnalyzeTsBuffer(buffer, copySize, summary);
                        }

                        summary.TsReadOk = ok != 0 && summary.BytesRead > 0;
                        if (!summary.TsReadOk) summary.Error = $"GetTsStream single-call returned false or empty. variant={summary.Variant}";
                        await progress("tsvariant_summary", $"result={(summary.TsReadOk ? "OK" : "NG")} variant={summary.Variant} bytes={summary.BytesRead} chunks={summary.ChunksRead} packets={summary.PacketsRead} syncErrors={summary.SyncErrors} readyNonZero={summary.NonZeroReadyCountSamples}/{summary.ReadyCountSamples}").ConfigureAwait(false);
                    }
                    else if (summary.Variant.Equals("buffer-vtable7-single", StringComparison.OrdinalIgnoreCase))
                    {
                        var getTs = Marshal.GetDelegateForFunctionPointer<GetTsStreamBufferDelegate>(getTsPtr7);
                        var waitResult = waitTs(driver, 100);
                        var ready = getReady(driver);
                        summary.ReadyCountSamples++;
                        if (ready > 0 || waitResult > 0) summary.NonZeroReadyCountSamples++;
                        var bufferSize = 512 * 1024;
                        var unmanagedBuffer = Marshal.AllocHGlobal(bufferSize);
                        try
                        {
                            uint size = (uint)bufferSize;
                            uint remain = 0;
                            await progress("tsvariant_getts_before", $"variant=buffer-vtable7-single wait={waitResult} ready={ready} method=buffer_overload_vtable7 singleCall=True bufferSize={bufferSize}").ConfigureAwait(false);
                            var ok = getTs(driver, unmanagedBuffer, ref size, ref remain);
                            summary.LastRemain = remain;
                            await progress("tsvariant_getts_after", $"variant=buffer-vtable7-single ok={ok} size={size} remain={remain}").ConfigureAwait(false);

                            if (ok != 0 && size > 0 && size <= bufferSize)
                            {
                                summary.ChunksRead++;
                                summary.BytesRead += size;
                                var copySize = CheckedTsCopySize(size);
                                var buffer = new byte[copySize];
                                Marshal.Copy(unmanagedBuffer, buffer, 0, copySize);
                                AnalyzeTsBuffer(buffer, copySize, summary);
                            }

                            summary.TsReadOk = ok != 0 && summary.BytesRead > 0;
                            if (!summary.TsReadOk) summary.Error = "GetTsStream buffer single-call returned false or empty.";
                            await progress("tsvariant_summary", $"result={(summary.TsReadOk ? "OK" : "NG")} variant=buffer-vtable7-single bytes={summary.BytesRead} chunks={summary.ChunksRead} packets={summary.PacketsRead} syncErrors={summary.SyncErrors} readyNonZero={summary.NonZeroReadyCountSamples}/{summary.ReadyCountSamples}").ConfigureAwait(false);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(unmanagedBuffer);
                        }
                    }
                    else
                    {
                        summary.TsReadOk = false;
                        summary.Error = $"Unsupported GetTsStream variant: {summary.Variant}";
                        await progress("tsvariant_summary", $"result=NG reason=unsupported_variant variant={summary.Variant}").ConfigureAwait(false);
                    }
                }

                try
                {
                    await MarkRecordShutdownStageAsync(summary, progress, "bon_driver_close_enter", "phase=before_close_tuner").ConfigureAwait(false);
                    close(driver);
                    summary.CloseTunerCalled = true;
                    await progress("tsvariant_close_tuner", "result=CALLED").ConfigureAwait(false);
                    await MarkRecordShutdownStageAsync(summary, progress, "bon_driver_close_exit", "result=CALLED").ConfigureAwait(false);
                    if (summary.StopCooldownMs > 0)
                    {
                        summary.StopCooldownApplied = true;
                        summary.StopCooldownReason = "after_close_before_release_directrecorder_stop_boundary";
                        await progress("tsvariant_stop_cooldown", $"result=WAIT ms={summary.StopCooldownMs} reason={summary.StopCooldownReason}").ConfigureAwait(false);
                        await Task.Delay(summary.StopCooldownMs).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    await progress("tsvariant_close_tuner", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                }

                try
                {
                    await MarkRecordShutdownStageAsync(summary, progress, "bon_driver_release_enter", "phase=before_release").ConfigureAwait(false);
                    release(driver);
                    summary.ReleaseCalled = true;
                    driver = IntPtr.Zero;
                    await progress("tsvariant_release", "result=CALLED").ConfigureAwait(false);
                    await MarkRecordShutdownStageAsync(summary, progress, "bon_driver_release_exit", "result=CALLED").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await progress("tsvariant_release", $"result=ERROR type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
                }
            }
            finally
            {
                try { Environment.CurrentDirectory = previousCwd; } catch { }
            }
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
            await progress("integrated_ts_runtime_failed", $"type={ex.GetType().Name} message={ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            if (summary.RecordDescrambler is not null)
            {
                try
                {
                    await MarkRecordShutdownStageAsync(summary, progress, "b25_decoder_dispose_enter", "phase=before_b25_dispose").ConfigureAwait(false);
                    summary.RecordDescrambler.Dispose();
                    await MarkRecordShutdownStageAsync(summary, progress, "b25_decoder_dispose_exit", "result=CALLED").ConfigureAwait(false);
                    await progress("record_b25_decoder_release", $"result=CALLED loaded={summary.ExternalB25LoadedPath ?? "-"} rule=v0.7.78_tvairepgrec_camel_result_and_b25_no_raw_fallback").ConfigureAwait(false);
                }
                catch { }
            }
            if (module != IntPtr.Zero)
            {
                try
                {
                    await MarkRecordShutdownStageAsync(summary, progress, "free_library_enter", "phase=before_free_library").ConfigureAwait(false);
                    summary.FreeLibraryCalled = FreeLibrary(module);
                    await MarkRecordShutdownStageAsync(summary, progress, "free_library_exit", $"result={(summary.FreeLibraryCalled ? "CALLED" : "NG")}").ConfigureAwait(false);
                    await progress("tsvariant_free_library", $"result={(summary.FreeLibraryCalled ? "CALLED" : "NG")}").ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private static int CheckedTsCopySize(uint size)
    {
        // v0.7.69: Do not truncate BonDriver GetTsStream buffers to 256 KiB.
        // Truncating a live TS buffer cuts continuity counters in the middle of the stream and creates
        // replay-breaking DROP/CC errors even when B25 descrambling itself succeeds.
        if (size == 0 || size > 8 * 1024 * 1024)
        {
            return 0;
        }

        return checked((int)size);
    }


    private static void AnalyzeRecordOutputBuffer(ReadOnlyMemory<byte> buffer, TsReadProbeSummary summary)
    {
        var span = buffer.Span;
        var packets = span.Length / 188;
        for (var i = 0; i < packets; i++)
        {
            var offset = i * 188;
            if (span[offset] != 0x47)
            {
                summary.OutputSyncErrors++;
                continue;
            }

            summary.OutputPacketsAnalyzed++;
            var outputPid = ((span[offset + 1] & 0x1F) << 8) | span[offset + 2];
            if (outputPid == 0x0029) summary.OutputCdtPid29Packets++;
            if ((span[offset + 1] & 0x80) != 0) summary.OutputTransportErrorPackets++;
            if ((span[offset + 3] & 0xC0) != 0) summary.OutputScrambledLikePackets++;
        }
    }

    private static void AnalyzeTsBuffer(byte[] buffer, int length, TsReadProbeSummary summary)
    {
        var packets = length / 188;
        for (var i = 0; i < packets; i++)
        {
            var offset = i * 188;
            if (buffer[offset] != 0x47)
            {
                summary.SyncErrors++;
                continue;
            }

            summary.PacketsRead++;
            var pid = ((buffer[offset + 1] & 0x1F) << 8) | buffer[offset + 2];
            Increment(summary.PidCounts, pid);
            if (pid == 0x0029) summary.InputCdtPid29Packets++;
            if ((buffer[offset + 1] & 0x80) != 0) summary.TransportErrorPackets++;
            if ((buffer[offset + 3] & 0xC0) != 0) summary.ScrambledLikePackets++;

            if (pid == 0x0000) summary.PatPackets++;
            if (pid == 0x0010) summary.NitPackets++;
            if (pid == 0x0011) summary.SdtPackets++;
            if (pid == 0x0012) summary.EitPackets++;
            if (summary.PmtPids.Contains(pid)) summary.PmtPackets++;

            var hasPayload = (buffer[offset + 3] & 0x10) != 0;
            if (!hasPayload) continue;

            var payloadOffset = offset + 4;
            var hasAdaptation = (buffer[offset + 3] & 0x20) != 0;
            if (hasAdaptation)
            {
                if (payloadOffset >= offset + 188) continue;
                var adaptationLength = buffer[payloadOffset];
                payloadOffset += 1 + adaptationLength;
            }

            if (payloadOffset >= offset + 188) continue;
            var payloadUnitStart = (buffer[offset + 1] & 0x40) != 0;
            if (!payloadUnitStart) continue;

            if (pid == 0x0012)
            {
                ParsePsiSectionsWithAssembly(buffer, payloadOffset, offset + 188, pid, payloadUnitStart, summary);
            }
            else if (pid == 0x0000 || pid == 0x0010 || pid == 0x0011 || summary.PmtPids.Contains(pid))
            {
                ParsePsiSection(buffer, payloadOffset, offset + 188, pid, summary);
            }
        }
    }


    private static void ParsePsiSectionsWithAssembly(byte[] buffer, int payloadOffset, int packetEnd, int pid, bool payloadUnitStart, TsReadProbeSummary summary)
    {
        if (payloadOffset >= packetEnd) return;

        var pos = payloadOffset;
        var state = GetPsiAssembler(summary, pid);

        if (payloadUnitStart)
        {
            var pointerField = buffer[pos];
            pos++;

            if (pointerField > 0)
            {
                var pointerEnd = Math.Min(pos + pointerField, packetEnd);
                if (state.Length > 0)
                {
                    AppendPsiAssemblyBytes(buffer, pos, pointerEnd - pos, pid, summary);
                }
                pos = pointerEnd;
            }
            else if (state.Length > 0)
            {
                state.Reset();
            }

            while (pos + 3 <= packetEnd)
            {
                if (buffer[pos] == 0xFF) break;
                var sectionLength = ((buffer[pos + 1] & 0x0F) << 8) | buffer[pos + 2];
                if (sectionLength < 5 || sectionLength > 4093) break;
                var totalLength = 3 + sectionLength;
                if (pos + totalLength <= packetEnd)
                {
                    ParseCompletePsiSection(buffer, pos, totalLength, pid, summary);
                    pos += totalLength;
                    continue;
                }

                state.Reset();
                state.ExpectedLength = totalLength;
                AppendPsiAssemblyBytes(buffer, pos, packetEnd - pos, pid, summary);
                break;
            }
        }
        else
        {
            AppendPsiAssemblyBytes(buffer, pos, packetEnd - pos, pid, summary);
        }
    }

    private static PsiSectionAssemblyState GetPsiAssembler(TsReadProbeSummary summary, int pid)
    {
        if (!summary.PsiAssemblers.TryGetValue(pid, out var state))
        {
            state = new PsiSectionAssemblyState();
            summary.PsiAssemblers[pid] = state;
        }
        return state;
    }

    private static void AppendPsiAssemblyBytes(byte[] buffer, int offset, int count, int pid, TsReadProbeSummary summary)
    {
        if (count <= 0) return;
        var state = GetPsiAssembler(summary, pid);
        var pos = offset;
        var remaining = count;

        while (remaining > 0)
        {
            if (state.Length == 0 && remaining > 0 && buffer[pos] == 0xFF) break;

            if (state.Length < 3 && state.ExpectedLength <= 0)
            {
                var needHeader = 3 - state.Length;
                var takeHeader = Math.Min(needHeader, remaining);
                state.EnsureCapacity(state.Length + takeHeader);
                Buffer.BlockCopy(buffer, pos, state.Buffer, state.Length, takeHeader);
                state.Length += takeHeader;
                pos += takeHeader;
                remaining -= takeHeader;
                if (state.Length < 3) return;

                var sectionLength = ((state.Buffer[1] & 0x0F) << 8) | state.Buffer[2];
                if (sectionLength < 5 || sectionLength > 4093)
                {
                    state.Reset();
                    return;
                }
                state.ExpectedLength = 3 + sectionLength;
                state.EnsureCapacity(state.ExpectedLength);
            }

            if (state.ExpectedLength <= 0)
            {
                if (remaining < 3) return;
                var sectionLength = ((buffer[pos + 1] & 0x0F) << 8) | buffer[pos + 2];
                if (sectionLength < 5 || sectionLength > 4093) return;
                state.ExpectedLength = 3 + sectionLength;
                state.EnsureCapacity(state.ExpectedLength);
            }

            var need = state.ExpectedLength - state.Length;
            var take = Math.Min(need, remaining);
            state.EnsureCapacity(state.Length + take);
            Buffer.BlockCopy(buffer, pos, state.Buffer, state.Length, take);
            state.Length += take;
            pos += take;
            remaining -= take;

            if (state.ExpectedLength > 0 && state.Length >= state.ExpectedLength)
            {
                ParseCompletePsiSection(state.Buffer, 0, state.ExpectedLength, pid, summary);
                state.Reset();
            }
        }
    }

    private static void ParseCompletePsiSection(byte[] buffer, int sectionOffset, int totalLength, int pid, TsReadProbeSummary summary)
    {
        if (totalLength < 8) return;
        var tableId = buffer[sectionOffset];
        var sectionLength = ((buffer[sectionOffset + 1] & 0x0F) << 8) | buffer[sectionOffset + 2];
        if (sectionLength < 5 || sectionOffset + 3 + sectionLength > sectionOffset + totalLength) return;
        Increment(summary.TableIdCounts, tableId);

        if (pid == 0x0012 && tableId >= 0x4E && tableId <= 0x6F)
        {
            summary.EitSeen = true;
            summary.EitSections++;
            ParseEitEvents(buffer, sectionOffset, sectionOffset + totalLength, tableId, sectionLength, summary);
        }
    }

    private static void ParsePsiSection(byte[] buffer, int payloadOffset, int packetEnd, int pid, TsReadProbeSummary summary)
    {
        if (payloadOffset >= packetEnd) return;
        var pointerField = buffer[payloadOffset];
        var sectionOffset = payloadOffset + 1 + pointerField;
        if (sectionOffset + 3 > packetEnd) return;

        var tableId = buffer[sectionOffset];
        var sectionLength = ((buffer[sectionOffset + 1] & 0x0F) << 8) | buffer[sectionOffset + 2];
        if (sectionLength < 5) return;
        var sectionEnd = Math.Min(sectionOffset + 3 + sectionLength, packetEnd);
        Increment(summary.TableIdCounts, tableId);

        if (pid == 0x0000 && tableId == 0x00)
        {
            summary.PatSeen = true;
            summary.PatSections++;
            var entriesEnd = Math.Min(sectionOffset + 3 + sectionLength - 4, packetEnd);
            for (var pos = sectionOffset + 8; pos + 4 <= entriesEnd; pos += 4)
            {
                var serviceId = (buffer[pos] << 8) | buffer[pos + 1];
                var pmtPid = ((buffer[pos + 2] & 0x1F) << 8) | buffer[pos + 3];
                if (serviceId != 0)
                {
                    AddUnique(summary.PatServiceIds, serviceId);
                    AddUnique(summary.PmtPids, pmtPid);
                }
            }
            return;
        }

        if (summary.PmtPids.Contains(pid) && tableId == 0x02)
        {
            summary.PmtSeen = true;
            summary.PmtSections++;
            if (sectionOffset + 12 > sectionEnd) return;
            var programInfoLength = ((buffer[sectionOffset + 10] & 0x0F) << 8) | buffer[sectionOffset + 11];
            var pos = sectionOffset + 12 + programInfoLength;
            var entriesEnd = Math.Min(sectionOffset + 3 + sectionLength - 4, packetEnd);
            while (pos + 5 <= entriesEnd)
            {
                var streamPid = ((buffer[pos + 1] & 0x1F) << 8) | buffer[pos + 2];
                var esInfoLength = ((buffer[pos + 3] & 0x0F) << 8) | buffer[pos + 4];
                AddUnique(summary.StreamPids, streamPid);
                pos += 5 + esInfoLength;
            }
            return;
        }

        if (pid == 0x0011 && (tableId == 0x42 || tableId == 0x46))
        {
            summary.SdtSeen = true;
            summary.SdtSections++;
            return;
        }

        if (pid == 0x0012 && tableId >= 0x4E && tableId <= 0x6F)
        {
            summary.EitSeen = true;
            summary.EitSections++;
            ParseEitEvents(buffer, sectionOffset, sectionEnd, tableId, sectionLength, summary);
        }
    }

    private static void ParseEitEvents(byte[] buffer, int sectionOffset, int sectionEnd, int tableId, int sectionLength, TsReadProbeSummary summary)
    {
        if (sectionOffset + 14 > sectionEnd) return;
        var serviceId = (buffer[sectionOffset + 3] << 8) | buffer[sectionOffset + 4];
        var transportStreamId = (buffer[sectionOffset + 8] << 8) | buffer[sectionOffset + 9];
        var originalNetworkId = (buffer[sectionOffset + 10] << 8) | buffer[sectionOffset + 11];
        Increment(summary.EitServiceIdCounts, serviceId);
        Increment(summary.EitTableIdCounts, tableId);
        Increment(summary.EitTableIdServiceIdCounts, $"0x{tableId:X2}/sid={serviceId}");
        Increment(summary.EitTripletCounts, $"onid={originalNetworkId}/tsid={transportStreamId}/sid={serviceId}");
        Increment(summary.EitActualOtherCounts, ClassifyEitTableId(tableId));
        if (summary.TargetOriginalNetworkId > 0 && summary.TargetTransportStreamId > 0
            && originalNetworkId == summary.TargetOriginalNetworkId && transportStreamId == summary.TargetTransportStreamId)
        {
            Increment(summary.EitTargetTransportServiceCounts, $"sid={serviceId}");
        }
        var eventsEnd = Math.Min(sectionOffset + 3 + sectionLength - 4, sectionEnd);
        var pos = sectionOffset + 14;
        while (pos + 12 <= eventsEnd)
        {
            var eventId = (buffer[pos] << 8) | buffer[pos + 1];
            var descriptorLoopLength = ((buffer[pos + 10] & 0x0F) << 8) | buffer[pos + 11];
            var descStart = pos + 12;
            var descEnd = descStart + descriptorLoopLength;
            if (descEnd > eventsEnd) break;

            var ev = new EitEventMinimal
            {
                TableId = tableId,
                ServiceId = serviceId,
                TransportStreamId = transportStreamId,
                OriginalNetworkId = originalNetworkId,
                EventId = eventId,
                StartTimeBcd = ToHex(buffer, pos + 2, 5),
                DurationBcd = ToHex(buffer, pos + 7, 3),
                RunningStatus = (buffer[pos + 10] >> 5) & 0x07,
                FreeCaMode = (buffer[pos + 10] & 0x10) != 0,
                DescriptorLoopLength = descriptorLoopLength
            };

            for (var d = descStart; d + 2 <= descEnd;)
            {
                var tag = buffer[d];
                var len = buffer[d + 1];
                var body = d + 2;
                var next = body + len;
                if (next > descEnd) break;
                Increment(summary.DescriptorTagCounts, tag);
                AddUnique(ev.DescriptorTags, tag);
                if (tag == 0x4D && len >= 5)
                {
                    ev.ShortEventDescriptorSeen = true;
                    var nameLenOffset = body + 3;
                    if (nameLenOffset < next)
                    {
                        var langValid = body + 3 <= next
                            && buffer[body] >= (byte)'a' && buffer[body] <= (byte)'z'
                            && buffer[body + 1] >= (byte)'a' && buffer[body + 1] <= (byte)'z'
                            && buffer[body + 2] >= (byte)'a' && buffer[body + 2] <= (byte)'z';
                        var nameLen = buffer[nameLenOffset];
                        var nameStart = nameLenOffset + 1;
                        var nameEnd = nameStart + nameLen;
                        if (langValid && nameEnd <= next)
                        {
                            ev.ShortEventNameRawLength = nameLen;
                            if (ev.ShortEventNameRawLength > 0)
                            {
                                ev.ShortEventNameRawHex = ToHex(buffer, nameStart, ev.ShortEventNameRawLength);
                                ev.ShortEventName = AribStringDecoder.Decode(buffer, nameStart, ev.ShortEventNameRawLength, out var nameSource);
                                ev.ShortEventDecodeSource = nameSource;
                            }
                            if (nameEnd < next)
                            {
                                var textLen = buffer[nameEnd];
                                var textStart = nameEnd + 1;
                                var textEnd = textStart + textLen;
                                if (textEnd <= next)
                                {
                                    ev.ShortEventTextRawLength = textLen;
                                    if (ev.ShortEventTextRawLength > 0)
                                    {
                                        ev.ShortEventTextRawHex = ToHex(buffer, textStart, ev.ShortEventTextRawLength);
                                        ev.ShortEventText = AribStringDecoder.Decode(buffer, textStart, ev.ShortEventTextRawLength, out var textSource);
                                        if (string.IsNullOrWhiteSpace(ev.ShortEventDecodeSource)) ev.ShortEventDecodeSource = textSource;
                                        else if (!string.Equals(ev.ShortEventDecodeSource, textSource, StringComparison.OrdinalIgnoreCase)) ev.ShortEventDecodeSource += "+" + textSource;
                                    }
                                }
                            }
                            ev.ShortEventDecoded = !string.IsNullOrWhiteSpace(ev.ShortEventName) || !string.IsNullOrWhiteSpace(ev.ShortEventText);
                        }
                    }
                }
                d = next;
            }

            summary.EitEventsDecoded++;
            if (ev.ShortEventDescriptorSeen) summary.EitEventsWithShortEvent++;
            if (ev.ShortEventDecoded) summary.EitEventsWithDecodedShortEvent++;
            if (summary.EitEvents.Count < 12) summary.EitEvents.Add(ev);
            if (IsTargetEitEvent(summary, originalNetworkId, transportStreamId, serviceId))
            {
                summary.TargetServiceEventsDecoded++;
                if (ev.ShortEventDescriptorSeen) summary.TargetServiceEventsWithShortEvent++;
                if (ev.ShortEventDecoded) summary.TargetServiceEventsWithDecodedShortEvent++;
                foreach (var tag in ev.DescriptorTags) Increment(summary.TargetServiceDescriptorTagCounts, tag);
                if (summary.TargetServiceEitEvents.Count < 12) summary.TargetServiceEitEvents.Add(ev);
            }
            pos = descEnd;
        }
    }

    private static bool IsTargetEitEvent(TsReadProbeSummary summary, int originalNetworkId, int transportStreamId, int serviceId)
    {
        if (summary.TargetServiceId <= 0) return false;
        if (serviceId != summary.TargetServiceId) return false;
        if (summary.TargetOriginalNetworkId > 0 && originalNetworkId != summary.TargetOriginalNetworkId) return false;
        if (summary.TargetTransportStreamId > 0 && transportStreamId != summary.TargetTransportStreamId) return false;
        return true;
    }

    private static void BuildEpgIntermediateModel(TsReadProbeSummary summary)
    {
        summary.EpgIntermediateEvents.Clear();
        summary.TargetServiceIntermediateEvents.Clear();

        foreach (var ev in summary.EitEvents)
        {
            var item = ToEpgIntermediateEvent(ev, ResolveServiceNameForIntermediate(ev, summary, targetOnly: false));
            if (item is null) continue;
            summary.EpgIntermediateEvents.Add(item);
        }

        foreach (var ev in summary.TargetServiceEitEvents)
        {
            var item = ToEpgIntermediateEvent(ev, ResolveServiceNameForIntermediate(ev, summary, targetOnly: true));
            if (item is null) continue;
            summary.TargetServiceIntermediateEvents.Add(item);
        }

        summary.EpgIntermediateEventsBuilt = summary.EpgIntermediateEvents.Count;
        summary.TargetServiceIntermediateEventsBuilt = summary.TargetServiceIntermediateEvents.Count;
    }

    private readonly record struct IntermediateServiceName(string Name, bool Resolved, string Resolution);

    private static IntermediateServiceName ResolveServiceNameForIntermediate(EitEventMinimal ev, TsReadProbeSummary summary, bool targetOnly)
    {
        var isTarget = IsTargetEitEvent(summary, ev.OriginalNetworkId, ev.TransportStreamId, ev.ServiceId);
        if (isTarget && !string.IsNullOrWhiteSpace(summary.ServiceName))
        {
            return new IntermediateServiceName(summary.ServiceName.Trim(), true, "target_request_service");
        }

        // v0.7.08: never stamp the requested service name onto other TS/SID events.
        // Non-target events are kept raw-side only until service mapping is available for projection.
        return new IntermediateServiceName(string.Empty, false, targetOnly ? "target_service_not_matched" : "unresolved_non_target_service");
    }

    private static EpgIntermediateEvent? ToEpgIntermediateEvent(EitEventMinimal ev, IntermediateServiceName serviceName)
    {
        var start = DecodeAribMjdBcd(ev.StartTimeBcd);
        var duration = DecodeDurationSeconds(ev.DurationBcd);
        if (start is null || duration <= 0) return null;
        var end = start.Value.AddSeconds(duration);
        var title = (ev.ShortEventName ?? string.Empty).Trim();
        var description = (ev.ShortEventText ?? string.Empty).Trim();
        var dbReady = ev.OriginalNetworkId > 0
                      && ev.TransportStreamId > 0
                      && ev.ServiceId > 0
                      && ev.EventId > 0
                      && !string.IsNullOrWhiteSpace(title)
                      && duration > 0;

        return new EpgIntermediateEvent
        {
            NetworkId = ev.OriginalNetworkId,
            TransportStreamId = ev.TransportStreamId,
            ServiceId = ev.ServiceId,
            EventId = ev.EventId,
            ServiceName = serviceName.Name,
            ServiceNameResolved = serviceName.Resolved,
            ServiceNameResolution = serviceName.Resolution,
            Title = title,
            Description = description,
            ExtendedDescription = string.Empty,
            Genre = string.Empty,
            GenreCodes = string.Empty,
            DurationSeconds = duration,
            StartTime = start.Value.ToString("O"),
            EndTime = end.ToString("O"),
            SourceTableId = $"0x{ev.TableId:X2}",
            SourceKind = ClassifyEitTableId(ev.TableId),
            FreeCaMode = ev.FreeCaMode,
            RunningStatus = ev.RunningStatus,
            DbWriteReady = dbReady,
            Validation = dbReady ? "OK" : "NOT_READY",
            EventKey = $"{ev.OriginalNetworkId}/{ev.TransportStreamId}/{ev.ServiceId}/{ev.EventId}"
        };
    }

    private static DateTimeOffset? DecodeAribMjdBcd(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 10) return null;
        var bytes = HexToBytes(hex);
        if (bytes.Length < 5) return null;
        var mjd = (bytes[0] << 8) | bytes[1];
        var date = MjdToDate(mjd);
        var hour = DecodeBcd(bytes[2]);
        var minute = DecodeBcd(bytes[3]);
        var second = DecodeBcd(bytes[4]);
        if (hour < 0 || minute < 0 || second < 0 || hour > 23 || minute > 59 || second > 59) return null;
        return new DateTimeOffset(date.Year, date.Month, date.Day, hour, minute, second, TimeSpan.FromHours(9));
    }

    private static int DecodeDurationSeconds(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 6) return 0;
        var bytes = HexToBytes(hex);
        if (bytes.Length < 3) return 0;
        var hour = DecodeBcd(bytes[0]);
        var minute = DecodeBcd(bytes[1]);
        var second = DecodeBcd(bytes[2]);
        if (hour < 0 || minute < 0 || second < 0 || minute > 59 || second > 59) return 0;
        return hour * 3600 + minute * 60 + second;
    }

    private static DateTime MjdToDate(int mjd)
    {
        var jd = mjd + 2400001;
        var l = jd + 68569;
        var n = 4 * l / 146097;
        l -= (146097 * n + 3) / 4;
        var i = 4000 * (l + 1) / 1461001;
        l = l - 1461 * i / 4 + 31;
        var j = 80 * l / 2447;
        var day = l - 2447 * j / 80;
        l = j / 11;
        var month = j + 2 - 12 * l;
        var year = 100 * (n - 49) + i + l;
        return new DateTime(year, month, day);
    }

    private static int DecodeBcd(byte value)
    {
        var hi = (value >> 4) & 0x0F;
        var lo = value & 0x0F;
        if (hi > 9 || lo > 9) return -1;
        return hi * 10 + lo;
    }

    private static byte[] HexToBytes(string hex)
    {
        var clean = hex.Trim();
        if (clean.Length % 2 != 0) clean = "0" + clean;
        var result = new byte[clean.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        }
        return result;
    }

    private static string ClassifyEitTableId(int tableId)
    {
        if (tableId == 0x4E) return "pf_actual_0x4E";
        if (tableId == 0x4F) return "pf_other_0x4F";
        if (tableId >= 0x50 && tableId <= 0x5F) return "schedule_actual_0x50_0x5F";
        if (tableId >= 0x60 && tableId <= 0x6F) return "schedule_other_0x60_0x6F";
        return $"unknown_0x{tableId:X2}";
    }

    private static string FormatIntCounts(Dictionary<int, long> counts)
    {
        return string.Join(',', counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    private static string FormatStringCounts(Dictionary<string, long> counts, int take = 32)
    {
        return string.Join(',', counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(take).Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    private static string ToHex(byte[] buffer, int offset, int count)
    {
        if (offset < 0 || count <= 0 || offset >= buffer.Length) return string.Empty;
        var end = Math.Min(buffer.Length, offset + count);
        return string.Concat(buffer.Skip(offset).Take(end - offset).Select(b => b.ToString("X2")));
    }

    private static void Increment(Dictionary<int, long> dict, int key)
    {
        dict[key] = dict.TryGetValue(key, out var current) ? current + 1 : 1;
    }

    private static void Increment(Dictionary<string, long> dict, string key)
    {
        dict[key] = dict.TryGetValue(key, out var current) ? current + 1 : 1;
    }

    private static void AddUnique(List<int> values, int value)
    {
        if (!values.Contains(value)) values.Add(value);
    }

}

internal sealed class WorkerProgress
{
    public DateTimeOffset Timestamp { get; set; }
    public string Version { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int ProcessId { get; set; }
}

internal sealed class DirectRecorderCompatibleResult
{
    public bool Success { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public string StartedAt { get; set; } = string.Empty;
    public string EndedAt { get; set; } = string.Empty;
    public long BytesWritten { get; set; }
    public long PacketsWritten { get; set; }
    public string QualityVerdict { get; set; } = string.Empty;
    public string RuntimeStatsPath { get; set; } = string.Empty;
    public long RuntimeStatsEmitted { get; set; }
    public bool StartupStabilityGateReleased { get; set; }
    public bool StartupStabilityGateTimedOut { get; set; }
    public long StartupStabilityGateWaitMs { get; set; }
    public string StartupStabilityGateReason { get; set; } = string.Empty;
    public long StartupStabilityDiscardedClearPackets { get; set; }
    public long StartupStabilityDiscardedDroppedPackets { get; set; }
    public long StartupStabilityDiscardedPendingPackets { get; set; }
    public long StartupStabilityDiscardedScrambledBlockedPackets { get; set; }
    public long StartupStabilityDiscardedOutputDrops { get; set; }
    public string StartupRecoveryAction { get; set; } = string.Empty;
    public long StartupRecoveryCount { get; set; }
    public string StartupRecoveryResult { get; set; } = string.Empty;
    public long RawPackets { get; set; }
    public long RawSyncErrors { get; set; }
    public long RawTransportErrors { get; set; }
    public long RawScrambledPackets { get; set; }
    public long RawContinuityErrors { get; set; }
    public long RawContinuityDrops { get; set; }
    public long OutputPackets { get; set; }
    public long OutputSyncErrors { get; set; }
    public long OutputTransportErrors { get; set; }
    public long OutputScrambledPackets { get; set; }
    public long OutputContinuityErrors { get; set; }
    public long OutputContinuityDrops { get; set; }
    public bool RecordServiceScopeEnabled { get; set; }
    public bool RecordServiceScopeReady { get; set; }
    public int TargetServiceId { get; set; }
    public int TargetPmtPid { get; set; }
    public int TargetPcrPid { get; set; }
    public List<int> TargetStreamPids { get; set; } = new();
    public List<int> WrittenServiceIds { get; set; } = new();
    public List<int> ExcludedServiceIds { get; set; } = new();
    public long ServiceScopeInputPackets { get; set; }
    public long ServiceScopeWrittenPackets { get; set; }
    public long ServiceScopeDroppedPackets { get; set; }
    public long ServiceScopeMediaPackets { get; set; }
    public string Message { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string ResolveInternalRuntimeStatsPath(TvAIrEpgRecJob? job, string? outputPath)
    {
        // 表版: 内部runtime統計JSONLは生成しない。
        return string.Empty;
#pragma warning disable CS0162
        var configured = job?.RuntimeStatsPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var dir = Path.GetDirectoryName(configured);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                return configured;
            }
            catch { return configured; }
        }

        try
        {
            var baseDir = !string.IsNullOrWhiteSpace(job?.ResultPath)
                ? Path.GetDirectoryName(job!.ResultPath!)
                : AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDir)) baseDir = AppContext.BaseDirectory;
            var name = !string.IsNullOrWhiteSpace(outputPath)
                ? Path.GetFileName(outputPath) + ".runtime.jsonl"
                : $"tvairepgrec_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.runtime.jsonl";
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, name);
        }
        catch
        {
            return string.Empty;
        }
#pragma warning restore CS0162
    }

    public static DirectRecorderCompatibleResult FromTsReadProbe(TvAIrEpgRecJob? job, TsReadProbeSummary summary, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var bytesWritten = summary.RecordBytesWritten;
        if (bytesWritten <= 0 && !string.IsNullOrWhiteSpace(summary.RecordOutputPath) && File.Exists(summary.RecordOutputPath))
        {
            try { bytesWritten = new FileInfo(summary.RecordOutputPath).Length; } catch { }
        }
        var packetsWritten = bytesWritten > 0 ? bytesWritten / 188 : 0;
        var outputPackets = summary.OutputPacketsAnalyzed > 0 ? summary.OutputPacketsAnalyzed : packetsWritten;
        var outputScrambledPackets = summary.OutputPacketsAnalyzed > 0
            ? summary.OutputScrambledLikePackets
            : (summary.ExternalB25Available && summary.ExternalB25DecodeOk > 0 ? 0 : summary.ScrambledLikePackets);
        var outputSyncErrors = summary.OutputPacketsAnalyzed > 0 ? summary.OutputSyncErrors : summary.SyncErrors;
        var outputTransportErrors = summary.OutputPacketsAnalyzed > 0 ? summary.OutputTransportErrorPackets : 0;
        var success = summary.RecordOutputOpened
                      && bytesWritten > 0
                      && packetsWritten > 0
                      && summary.OpenTunerOk
                      && summary.SetChannelOk
                      && summary.TsReadStarted
                      && summary.SyncErrors == 0
                      && outputSyncErrors == 0
                      && outputScrambledPackets == 0
                      && (!summary.RecordServiceScopeEnabled || (summary.RecordServiceScopeReady && summary.RecordServiceScopeMediaPackets > 0));
        var verdict = success
            ? (summary.RecordServiceScopeEnabled ? "LIVE_CLEAR_TARGET_SERVICE_TS_OK" : "LIVE_CLEAR_TS_OK")
            : summary.RecordServiceScopeEnabled && (!summary.RecordServiceScopeReady || summary.RecordServiceScopeMediaPackets <= 0)
                ? "TARGET_SERVICE_SCOPE_NOT_READY"
                : bytesWritten > 0 && outputScrambledPackets > 0
                ? "LIVE_TS_WRITTEN_OUTPUT_SCRAMBLED"
                : bytesWritten > 0 && summary.ExternalB25RawFallbackSuppressed > 0
                    ? "LIVE_TS_WRITTEN_B25_BUFFERED_WITH_SUPPRESSED_RAW"
                    : "TVAIREPGREC_RECORD_RUNTIME_NG";
        var runtimeStatsPath = ResolveInternalRuntimeStatsPath(job, summary.RecordOutputPath);
        var runtimeStatsEmitted = 0L;
        if (!string.IsNullOrWhiteSpace(runtimeStatsPath))
        {
            try
            {
                var runtimeLine = JsonSerializer.Serialize(new
                {
                    timestamp = endedAt,
                    version = "0.7.78",
                    verdict,
                    bytesWritten,
                    packetsWritten,
                    rawPackets = summary.PacketsRead,
                    rawSyncErrors = summary.SyncErrors,
                    rawScrambledPackets = summary.ScrambledLikePackets,
                    outputPackets,
                    outputSyncErrors,
                    outputTransportErrors,
                    outputScrambledPackets,
                    externalB25Available = summary.ExternalB25Available,
                    externalB25LoadedPath = summary.ExternalB25LoadedPath,
                    externalB25DecodeCalls = summary.ExternalB25DecodeCalls,
                    externalB25DecodeOk = summary.ExternalB25DecodeOk,
                    externalB25DecodeNg = summary.ExternalB25DecodeNg,
                    externalB25BufferedEmpty = summary.ExternalB25BufferedEmpty,
                    externalB25RawFallbackSuppressed = summary.ExternalB25RawFallbackSuppressed,
                    externalB25FlushCalls = summary.ExternalB25FlushCalls,
                    externalB25FlushBytes = summary.ExternalB25FlushBytes,
                    chunksWritten = summary.RecordChunksWritten,
                    recordServiceScopeEnabled = summary.RecordServiceScopeEnabled,
                    recordServiceScopeReady = summary.RecordServiceScopeReady,
                    targetServiceId = summary.TargetServiceId,
                    targetPmtPid = summary.RecordTargetPmtPid,
                    targetPcrPid = summary.RecordTargetPcrPid,
                    targetStreamPids = summary.RecordTargetStreamPids,
                    writtenServiceIds = summary.RecordWrittenServiceIds,
                    excludedServiceIds = summary.RecordExcludedServiceIds,
                    serviceScopeInputPackets = summary.RecordServiceScopeInputPackets,
                    serviceScopeWrittenPackets = summary.RecordServiceScopeWrittenPackets,
                    serviceScopeDroppedPackets = summary.RecordServiceScopeDroppedPackets,
                    serviceScopeMediaPackets = summary.RecordServiceScopeMediaPackets,
                    startupRecoveryAction = summary.RecordStartupRecoveryAction,
                    startupRecoveryCount = summary.RecordStartupRecoveryCount,
                    startupRecoveryResult = summary.RecordStartupRecoveryResult,
                    startupFallbackFullTsActive = summary.RecordStartupFallbackFullTsActive,
                    startupFallbackFullTsBytes = summary.RecordStartupFallbackFullTsBytes,
                    stopRequested = summary.RecordStopRequested,
                    stopReason = summary.RecordStopReason,
                    shutdownStage = summary.RecordShutdownStage,
                    stopAcceptedAt = summary.RecordStopAcceptedAt,
                    shutdownStageAt = summary.RecordShutdownStageAt
                }, ResultJsonOptions);
                File.WriteAllText(runtimeStatsPath, runtimeLine + Environment.NewLine);
                runtimeStatsEmitted = 1;
            }
            catch
            {
                runtimeStatsEmitted = 0;
            }
        }

        return new DirectRecorderCompatibleResult
        {
            Success = success,
            OutputPath = summary.RecordOutputPath ?? string.Empty,
            StartedAt = startedAt.ToString("O"),
            EndedAt = endedAt.ToString("O"),
            BytesWritten = bytesWritten,
            PacketsWritten = packetsWritten,
            QualityVerdict = verdict,
            RuntimeStatsPath = runtimeStatsPath,
            RuntimeStatsEmitted = runtimeStatsEmitted,
            StartupStabilityGateReleased = summary.ReadyThresholdReached || summary.NonZeroReadyCountSamples > 0,
            StartupStabilityGateTimedOut = !summary.ReadyThresholdReached,
            StartupStabilityGateWaitMs = 0,
            StartupStabilityGateReason = summary.ReadyThresholdReached
                ? "ready_threshold_reached"
                : summary.RecordBytesWritten > 0
                    ? (summary.RecordServiceScopeReady ? "target_service_output_ready" : "record_output_ready_without_target_scope")
                    : summary.RecordStartupRecoveryCount > 0 || summary.RecordStartupFallbackFullTsActive
                        ? "zero_byte_startup_recovery_attempted"
                        : "timeout_without_output",
            StartupStabilityDiscardedClearPackets = 0,
            StartupStabilityDiscardedDroppedPackets = 0,
            StartupStabilityDiscardedPendingPackets = 0,
            StartupStabilityDiscardedScrambledBlockedPackets = 0,
            StartupStabilityDiscardedOutputDrops = summary.ExternalB25RawFallbackSuppressed,
            StartupRecoveryAction = summary.RecordStartupRecoveryAction != "none"
                ? summary.RecordStartupRecoveryAction
                : summary.ExternalB25RawFallbackSuppressed > 0 ? "raw_fallback_suppressed" : "none",
            StartupRecoveryCount = summary.RecordStartupRecoveryCount > 0
                ? summary.RecordStartupRecoveryCount
                : summary.ExternalB25RawFallbackSuppressed,
            StartupRecoveryResult = summary.RecordStartupRecoveryResult != "not_required"
                ? summary.RecordStartupRecoveryResult
                : summary.ExternalB25RawFallbackSuppressed > 0 ? "raw_packets_not_written_after_b25_selected" : "not_required",
            RawPackets = summary.PacketsRead,
            RawSyncErrors = summary.SyncErrors,
            RawTransportErrors = summary.TransportErrorPackets,
            RawScrambledPackets = summary.ScrambledLikePackets,
            RawContinuityErrors = 0,
            RawContinuityDrops = 0,
            OutputPackets = outputPackets,
            OutputSyncErrors = outputSyncErrors,
            OutputTransportErrors = outputTransportErrors,
            OutputScrambledPackets = outputScrambledPackets,
            OutputContinuityErrors = 0,
            OutputContinuityDrops = summary.ExternalB25RawFallbackSuppressed,
            RecordServiceScopeEnabled = summary.RecordServiceScopeEnabled,
            RecordServiceScopeReady = summary.RecordServiceScopeReady,
            TargetServiceId = summary.TargetServiceId,
            TargetPmtPid = summary.RecordTargetPmtPid,
            TargetPcrPid = summary.RecordTargetPcrPid,
            TargetStreamPids = summary.RecordTargetStreamPids.ToList(),
            WrittenServiceIds = summary.RecordWrittenServiceIds.ToList(),
            ExcludedServiceIds = summary.RecordExcludedServiceIds.ToList(),
            ServiceScopeInputPackets = summary.RecordServiceScopeInputPackets,
            ServiceScopeWrittenPackets = summary.RecordServiceScopeWrittenPackets,
            ServiceScopeDroppedPackets = summary.RecordServiceScopeDroppedPackets,
            ServiceScopeMediaPackets = summary.RecordServiceScopeMediaPackets,
            Message = success
                ? $"TvAIrEpgRec record runtime completed. target service scope is active where target SID is available; bytesWritten={bytesWritten} packetsWritten={packetsWritten} outputScrambled={outputScrambledPackets} targetSid={summary.TargetServiceId} scopeReady={summary.RecordServiceScopeReady} writtenServiceIds={string.Join(',', summary.RecordWrittenServiceIds)} excludedServiceIds={string.Join(',', summary.RecordExcludedServiceIds)} startupRecovery={summary.RecordStartupRecoveryAction}/{summary.RecordStartupRecoveryResult} fallbackFullTsBytes={summary.RecordStartupFallbackFullTsBytes} b25={summary.ExternalB25ProbeResult}"
                : $"TvAIrEpgRec record runtime failed or needs investigation. opened={summary.RecordOutputOpened} bytesWritten={bytesWritten} packetsWritten={packetsWritten} open={summary.OpenTunerOk} setChannel={summary.SetChannelOk} tsStarted={summary.TsReadStarted} rawSyncErrors={summary.SyncErrors} outputSyncErrors={outputSyncErrors} outputScrambled={outputScrambledPackets} targetSid={summary.TargetServiceId} scopeReady={summary.RecordServiceScopeReady} mediaPackets={summary.RecordServiceScopeMediaPackets} startupRecovery={summary.RecordStartupRecoveryAction}/{summary.RecordStartupRecoveryResult} fallbackFullTsBytes={summary.RecordStartupFallbackFullTsBytes} rawFallbackSuppressed={summary.ExternalB25RawFallbackSuppressed} error={summary.Error}"
        };
    }

}

internal sealed class WorkerResult
{
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public string Version { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public int ProcessId { get; set; }
    public int ExitCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorType { get; set; }
    public string? Error { get; set; }
    public TvAIrEpgRecJob? Job { get; set; }
    public EpgContractSummary? EpgContract { get; set; }
    public BonDriverOpenProbeSummary? BonDriverOpenProbe { get; set; }
    public SetChannelProbeSummary? SetChannelProbe { get; set; }
    public TsReadProbeSummary? TsReadProbe { get; set; }
    [JsonPropertyName("result")]
    public DirectRecorderCompatibleResult? Result { get; set; }
    public ExecutionLineageSummary? Lineage { get; set; }
    public Dictionary<string, string>? Arguments { get; set; }
}

internal sealed class ExecutionLineageSummary
{
    public string ExecutableName { get; set; } = string.Empty;
    public string LineageSource { get; set; } = string.Empty;
    public bool ExistingRecordRouteTouched { get; set; }
    public bool DirectRecorderBridgeStillRequired { get; set; }
    public bool DirectRecorderBridgeRemovalAllowed { get; set; }
    public string RecordDecisionOwner { get; set; } = string.Empty;
    public string RecordExecutionOwner { get; set; } = string.Empty;
    public string EpgExecutionOwner { get; set; } = string.Empty;
    public string EpgCheckExecutionOwner { get; set; } = string.Empty;
    public string ServiceIdentityRule { get; set; } = string.Empty;
    public string ChainRecordingRule { get; set; } = string.Empty;
    public string SharedRouteRoot { get; set; } = string.Empty;
    public string ExistingRecordModule { get; set; } = string.Empty;
    public string EpgModule { get; set; } = string.Empty;
    public string EpgCheckModule { get; set; } = string.Empty;
    public string MigrationSafetyRule { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
}
