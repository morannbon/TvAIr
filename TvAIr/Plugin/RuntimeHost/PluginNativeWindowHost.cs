using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using TvAIrPlugin.Windows;

namespace TvAIr.Plugin.RuntimeHost;

/// <summary>
/// Owns native plugin windows and their content hosts. New runtime windows do not route through
/// the legacy ToolWindow HTTP/WebBrowser host.
/// </summary>
internal sealed class PluginNativeWindowHost : IDisposable
{
    private readonly ConcurrentDictionary<string, HostedWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _onUserHidden;
    private int _disposed;

    public PluginNativeWindowHost(Action<string> onUserHidden)
    {
        _onUserHidden = onUserHidden ?? throw new ArgumentNullException(nameof(onUserHidden));
    }

    public void EnsureCreated(PluginWindowState state, PluginWindowDefinition definition)
    {
        ThrowIfDisposed();
        _windows.GetOrAdd(state.WindowInstanceId, _ => new HostedWindow(state, definition, OnClosed, _onUserHidden)).EnsureStarted();
    }

    public void Show(PluginWindowState state, PluginWindowDefinition definition, bool activate)
    {
        EnsureCreated(state, definition);
        _windows[state.WindowInstanceId].Post(form =>
        {
            form.Text = state.Title;
            form.ShowManaged(activate);
        });
    }

    public void Hide(string windowInstanceId)
    {
        if (_windows.TryGetValue(windowInstanceId, out var hosted))
            hosted.Post(form => form.Hide());
    }

    public void Close(string windowInstanceId)
    {
        if (_windows.TryGetValue(windowInstanceId, out var hosted))
            hosted.Post(form => form.CloseFromHost());
    }

    public Task InvokeContentAsync(string windowInstanceId, Func<Control, Task> action)
    {
        ThrowIfDisposed();
        if (!_windows.TryGetValue(windowInstanceId, out var hosted))
            throw new InvalidOperationException("Plugin window host was not found.");
        return hosted.InvokeContentAsync(action);
    }

    private void OnClosed(string windowInstanceId) => _windows.TryRemove(windowInstanceId, out _);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var hosted in _windows.Values.ToArray())
            hosted.Post(form => form.CloseFromHost());
        _windows.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class HostedWindow
    {
        private readonly PluginWindowState _state;
        private readonly PluginWindowDefinition _definition;
        private readonly Action<string> _onClosed;
        private readonly Action<string> _onUserHidden;
        private readonly BlockingCollection<Action<PluginWindowForm>> _queue = new();
        private readonly TaskCompletionSource<PluginWindowForm> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Thread? _thread;
        private PluginWindowForm? _form;
        private int _started;

        public HostedWindow(PluginWindowState state, PluginWindowDefinition definition, Action<string> onClosed, Action<string> onUserHidden)
        {
            _state = state;
            _definition = definition;
            _onClosed = onClosed;
            _onUserHidden = onUserHidden;
        }

        public void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = $"TvAIr.PluginWindow.{_state.WindowInstanceId}"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            _created.Task.GetAwaiter().GetResult();
        }

        public void Post(Action<PluginWindowForm> action)
        {
            EnsureStarted();
            TryPost(action);
        }

        private bool TryPost(Action<PluginWindowForm> action)
        {
            if (_queue.IsAddingCompleted) return false;
            try
            {
                _queue.Add(action);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public Task InvokeContentAsync(Func<Control, Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            EnsureStarted();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryPost(async form =>
            {
                try
                {
                    await action(form.ContentHost);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(new InvalidOperationException("Plugin window content operation failed.", ex));
                }
            }))
            {
                completion.TrySetException(new ObjectDisposedException(nameof(HostedWindow), "Plugin window is already closed."));
            }
            return completion.Task;
        }

        private void Run()
        {
            try
            {
                using var form = new PluginWindowForm(_state, _definition, _onUserHidden);
                _form = form;
                using var context = new ApplicationContext();
                form.FormClosed += (_, _) =>
                {
                    _queue.CompleteAdding();
                    _onClosed(_state.WindowInstanceId);
                    context.ExitThread();
                };
                using var timer = new System.Windows.Forms.Timer { Interval = 15 };
                timer.Tick += (_, _) => Drain(form);
                timer.Start();
                _created.TrySetResult(form);
                Application.Run(context);
                timer.Stop();
            }
            catch (Exception ex)
            {
                _queue.CompleteAdding();
                _created.TrySetException(new InvalidOperationException("Plugin window initialization failed.", ex));
                _onClosed(_state.WindowInstanceId);
            }
            finally
            {
                _form = null;
            }
        }

        private void Drain(PluginWindowForm form)
        {
            while (_queue.TryTake(out var action))
            {
                try { action(form); } catch { }
            }
        }
    }

    private sealed class PluginWindowForm : Form
    {
        private bool _hostClose;

        private readonly Action<string> _onUserHidden;
        private readonly string _windowInstanceId;

        public PluginWindowForm(PluginWindowState state, PluginWindowDefinition definition, Action<string> onUserHidden)
        {
            _onUserHidden = onUserHidden;
            _windowInstanceId = state.WindowInstanceId;
            Text = state.Title;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = definition.ShowInTaskbar;
            FormBorderStyle = definition.Resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;
            ClientSize = new Size(
                Math.Max(1, (int)Math.Round(definition.InitialSize.Width)),
                Math.Max(1, (int)Math.Round(definition.InitialSize.Height)));
            MinimumSize = new Size(
                Math.Max(1, (int)Math.Round(definition.MinimumSize.Width)),
                Math.Max(1, (int)Math.Round(definition.MinimumSize.Height)));
            Padding = Padding.Empty;
            Margin = Padding.Empty;
            ContentHost = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            Controls.Add(ContentHost);
            Layout += (_, _) => EnsureContentHostOccupancy();
            ClientSizeChanged += (_, _) => EnsureContentHostOccupancy();
            FormClosing += (_, e) =>
            {
                if (_hostClose) return;
                e.Cancel = true;
                Hide();
                _onUserHidden(_windowInstanceId);
            };
        }

        private void EnsureContentHostOccupancy()
        {
            Padding = Padding.Empty;
            Margin = Padding.Empty;
            ContentHost.Dock = DockStyle.Fill;
            ContentHost.Margin = Padding.Empty;
            ContentHost.Padding = Padding.Empty;
            ContentHost.Bounds = ClientRectangle;
        }

        public void ShowManaged(bool activate)
        {
            if (activate)
            {
                if (!Visible) Show();
                Activate();
                return;
            }

            if (!IsHandleCreated) CreateControl();
            ShowWindow(Handle, SwShowNoActivate);
        }

        public void CloseFromHost()
        {
            _hostClose = true;
            Close();
        }

        private const int SwShowNoActivate = 4;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public Panel ContentHost { get; }
    }
}
