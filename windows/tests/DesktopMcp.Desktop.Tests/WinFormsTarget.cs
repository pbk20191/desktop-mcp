using System.Drawing;
using System.Windows.Forms;
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Desktop.Tests;

/// <summary>
/// A WinForms window the desktop tests own: a Button, a ToolStrip holding one ToolStripButton and a TextBox, on its own
/// STA thread with its own message loop. It is shown without activation; <see cref="ActivateAndWait"/> makes it the
/// foreground window (or throws, sending nothing). Every member that touches a control runs on the form's thread.
/// </summary>
internal sealed class WinFormsTarget : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private readonly Thread _thread;
    private readonly TaskCompletionSource<TargetForm> _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TargetForm _form;
    private int _buttonClicks;
    private int _toolStripClicks;

    public WinFormsTarget(int left = 160, int top = 160)
    {
        TestDpi.EnsurePerMonitorV2();
        Title = $"desktop-mcp winforms test {Guid.NewGuid():N}";
        _thread = new Thread(() => Run(left, top)) { IsBackground = true, Name = "winforms test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _form = _shown.Task.WaitAsync(WaitLimit).GetAwaiter().GetResult();
        Handle = OnFormThread(() => _form.Handle);
    }

    public string Title { get; }

    public nint Handle { get; }

    public int ButtonClicks => Volatile.Read(ref _buttonClicks);

    public int ToolStripClicks => Volatile.Read(ref _toolStripClicks);

    public ScreenPoint ButtonCenter => OnFormThread(() => CenterOf(_form.Button, _form.Button.ClientRectangle));

    public ScreenPoint TextBoxCenter => OnFormThread(() => CenterOf(_form.TextBox, _form.TextBox.ClientRectangle));

    /// <summary>The ToolStripButton's centre: <c>ToolStrip.Items[0].Bounds</c> mapped with <c>PointToScreen</c>.</summary>
    public ScreenPoint ToolStripButtonCenter => OnFormThread(() => CenterOf(_form.ToolStrip, _form.ToolStrip.Items[0].Bounds));

    public string TextBoxText => OnFormThread(() => _form.TextBox.Text);

    public void ActivateAndWait()
    {
        TestForeground.Activate(Handle);
        OnFormThread(() => _form.Activate()); // runs once the form's thread has processed the activation
    }

    public void Dispose()
    {
        try
        {
            _form.BeginInvoke(_form.Close);
        }
        catch (InvalidOperationException)
        {
            // the form is already gone
        }
        if (!_thread.Join(WaitLimit)) throw new TimeoutException("The WinForms test window's thread did not end within 10 s.");
    }

    private static ScreenPoint CenterOf(Control control, Rectangle bounds)
    {
        var point = control.PointToScreen(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        return new ScreenPoint(point.X, point.Y);
    }

    private T OnFormThread<T>(Func<T> read) => (T)_form.Invoke(read)!;

    private void OnFormThread(Action action) => _form.Invoke(action);

    private void Run(int left, int top)
    {
        try
        {
            using var form = new TargetForm(Title, left, top);
            form.Button.Click += (_, _) => Interlocked.Increment(ref _buttonClicks);
            form.ToolStrip.Items[0].Click += (_, _) => Interlocked.Increment(ref _toolStripClicks);
            form.Shown += (_, _) => _shown.TrySetResult(form);
            Application.Run(form);
        }
        catch (Exception e)
        {
            _shown.TrySetException(e);
        }
    }

    private sealed class TargetForm : Form
    {
        public TargetForm(string title, int left, int top)
        {
            Text = title;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(left, top, 480, 300);
            ToolStrip = new ToolStrip { Dock = DockStyle.Top };
            ToolStrip.Items.Add(new ToolStripButton("Tool") { DisplayStyle = ToolStripItemDisplayStyle.Text });
            Button = new Button { Text = "Press me", Location = new Point(20, 60), Size = new Size(120, 32) };
            TextBox = new TextBox { Location = new Point(20, 110), Width = 300 };
            Controls.Add(Button);
            Controls.Add(TextBox);
            Controls.Add(ToolStrip);
        }

        public ToolStrip ToolStrip { get; }

        public Button Button { get; }

        public TextBox TextBox { get; }

        protected override bool ShowWithoutActivation => true;
    }
}
