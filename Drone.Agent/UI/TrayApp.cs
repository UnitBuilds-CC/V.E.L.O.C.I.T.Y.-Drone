using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Collections.Concurrent;

namespace Drone.Agent.UI;

public class TrayApp : ApplicationContext
{
    private NotifyIcon _notifyIcon = null!;
    private DroneConsoleWindow? _consoleWindow;
    private readonly ConcurrentQueue<string> _logBuffer = new();
    private const int MaxBufferSize = 500;
    private string _status = "Starting...";
    private bool _connected;
    private Icon? _cachedIcon;

    public TrayApp()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        _cachedIcon = CreateDroneIcon(false);
        _notifyIcon = new NotifyIcon
        {
            Icon = _cachedIcon,
            Text = "Velocity Drone",
            Visible = true,
            ContextMenuStrip = CreateContextMenu()
        };
        _notifyIcon.DoubleClick += (s, e) => ShowConsole();
        _notifyIcon.BalloonTipClicked += (s, e) => ShowConsole();
    }

    private Icon CreateDroneIcon(bool useConnectedState = true)
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Drone body - hexagonal shape
        var bodyPoints = new PointF[]
        {
            new(16, 4), new(26, 10), new(26, 22),
            new(16, 28), new(6, 22), new(6, 10)
        };
        using var bodyBrush = new SolidBrush(Color.FromArgb(0, 150, 255));
        g.FillPolygon(bodyBrush, bodyPoints);

        // Propeller arms
        using var armPen = new Pen(Color.FromArgb(80, 80, 80), 2);
        g.DrawLine(armPen, 16, 16, 4, 4);
        g.DrawLine(armPen, 16, 16, 28, 4);
        g.DrawLine(armPen, 16, 16, 4, 28);
        g.DrawLine(armPen, 16, 16, 28, 28);

        // Propeller circles
        using var propBrush = new SolidBrush(Color.FromArgb(120, 200, 255));
        g.FillEllipse(propBrush, 0, 0, 10, 10);
        g.FillEllipse(propBrush, 22, 0, 10, 10);
        g.FillEllipse(propBrush, 0, 22, 10, 10);
        g.FillEllipse(propBrush, 22, 22, 10, 10);

        // Center LED - use connected state only if requested
        var ledColor = (useConnectedState && _connected) ? Color.FromArgb(0, 255, 100) : Color.FromArgb(255, 100, 0);
        using var ledBrush = new SolidBrush(ledColor);
        g.FillEllipse(ledBrush, 12, 12, 8, 8);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.BackColor = Color.FromArgb(30, 30, 40);
        menu.ForeColor = Color.White;
        menu.ShowImageMargin = false;
        menu.Renderer = new DarkMenuRenderer();

        var statusItem = new ToolStripMenuItem("Status: Starting...") { Enabled = false };
        statusItem.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        menu.Items.Add(statusItem);

        menu.Items.Add(new ToolStripSeparator());

        var consoleItem = new ToolStripMenuItem("Show Console");
        consoleItem.Font = new Font("Segoe UI", 9);
        consoleItem.Click += (s, e) => ShowConsole();
        menu.Items.Add(consoleItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Font = new Font("Segoe UI", 9);
        exitItem.Click += (s, e) => Exit();
        menu.Items.Add(exitItem);

        return menu;
    }

    public void ShowConsole()
    {
        if (_consoleWindow == null || _consoleWindow.IsDisposed)
        {
            _consoleWindow = new DroneConsoleWindow(this);
        }
        _consoleWindow.Show();
        _consoleWindow.BringToFront();
    }

    public void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{timestamp}] {message}";
        _logBuffer.Enqueue(entry);
        while (_logBuffer.Count > MaxBufferSize)
            _logBuffer.TryDequeue(out _);

        _consoleWindow?.AppendLog(entry);
    }

    public void SetStatus(string status, bool connected)
    {
        _status = status;
        _connected = connected;
        _notifyIcon.Text = $"Velocity Drone - {status}";
        // Don't recreate icon - just update tooltip to avoid GDI leak

        if (_notifyIcon.ContextMenuStrip?.Items[0] is ToolStripMenuItem statusItem)
        {
            statusItem.Text = $"Status: {status}";
            statusItem.ForeColor = connected ? Color.FromArgb(0, 255, 100) : Color.FromArgb(255, 150, 0);
        }

        _consoleWindow?.UpdateStatus(status, connected);
    }

    public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    public void Exit()
    {
        _notifyIcon.Visible = false;
        _consoleWindow?.Close();
        Application.Exit();
    }

    public string[] GetLogHistory() => _logBuffer.ToArray();

    public (string Status, bool Connected) GetCurrentStatus() => (_status, _connected);
}

public class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rc = new Rectangle(Point.Empty, e.Item.Size);
        if (e.Item.Selected)
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(60, 60, 80)), rc);
        else
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 40)), rc);
    }
}

public class DroneConsoleWindow : Form
{
    private readonly TrayApp _trayApp;
    private readonly RichTextBox _logBox;
    private readonly Label _statusLabel;
    private readonly Label _titleLabel;
    private readonly TextBox _commandInput;
    private readonly Panel _headerPanel;
    private readonly Panel _footerPanel;

    public DroneConsoleWindow(TrayApp trayApp)
    {
        _trayApp = trayApp;

        // Form setup
        Text = "Velocity Drone";
        Size = new Size(700, 450);
        MinimumSize = new Size(500, 300);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;

        // Header panel
        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 50,
            BackColor = Color.FromArgb(32, 32, 44),
            Padding = new Padding(16, 0, 16, 0)
        };

        _titleLabel = new Label
        {
            Text = "VELOCITY DRONE",
            Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 160, 255),
            Dock = DockStyle.Left,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _headerPanel.Controls.Add(_titleLabel);

        _statusLabel = new Label
        {
            Text = "Starting...",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(180, 180, 180),
            Dock = DockStyle.Right,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight
        };
        _headerPanel.Controls.Add(_statusLabel);

        // Log box
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(20, 20, 28),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Cascadia Code", 9),
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            Margin = new Padding(0)
        };

        // Footer panel with command input
        _footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            BackColor = Color.FromArgb(32, 32, 44),
            Padding = new Padding(8, 6, 8, 6)
        };

        var promptLabel = new Label
        {
            Text = ">",
            Font = new Font("Cascadia Code", 10),
            ForeColor = Color.FromArgb(0, 160, 255),
            Dock = DockStyle.Left,
            Width = 20,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _footerPanel.Controls.Add(promptLabel);

        _commandInput = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(40, 40, 52),
            ForeColor = Color.White,
            Font = new Font("Cascadia Code", 10),
            BorderStyle = BorderStyle.None,
            Margin = new Padding(4, 0, 0, 0)
        };
        _commandInput.KeyDown += CommandInput_KeyDown;
        _footerPanel.Controls.Add(_commandInput);

        // Add controls in correct order
        Controls.Add(_logBox);
        Controls.Add(_footerPanel);
        Controls.Add(_headerPanel);

        // Load history
        foreach (var entry in _trayApp.GetLogHistory())
        {
            AppendLog(entry, false);
        }

        // Load current status
        var (status, connected) = _trayApp.GetCurrentStatus();
        UpdateStatus(status, connected);
    }

    private void CommandInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !string.IsNullOrWhiteSpace(_commandInput.Text))
        {
            var cmd = _commandInput.Text.Trim();
            _commandInput.Clear();
            
            // Log the command
            AppendLog($"[CMD] > {cmd}");
            
            // TODO: Process command - for now just echo
            // This could send commands to the drone via Messenger or execute locally
        }
    }

    public void AppendLog(string message, bool scrollToBottom = true)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message, scrollToBottom));
            return;
        }

        var color = message.Contains("ERROR") ? Color.FromArgb(255, 100, 100) :
                    message.Contains("WARN") ? Color.FromArgb(255, 200, 0) :
                    message.Contains("INFO") ? Color.FromArgb(100, 200, 255) :
                    message.Contains("CMD") ? Color.FromArgb(150, 255, 150) :
                    Color.FromArgb(200, 200, 200);

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionColor = color;
        _logBox.AppendText(message + Environment.NewLine);

        if (scrollToBottom)
        {
            _logBox.ScrollToCaret();
        }
    }

    public void UpdateStatus(string status, bool connected)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateStatus(status, connected));
            return;
        }

        _statusLabel.Text = status;
        _statusLabel.ForeColor = connected ? Color.FromArgb(100, 255, 100) : Color.FromArgb(255, 150, 100);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }
}
