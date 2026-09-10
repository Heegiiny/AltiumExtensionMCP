using System;
using System.Drawing;
using System.Windows.Forms;
using AltiumMcp.Extension.Bridge;
using DXP;

namespace AltiumMcp.Extension.Panel;

/// <summary>
/// Minimal WinForms status panel: bridge URL/state, request counter and a rolling log.
/// Its main purpose is operational: opening (and docking) this panel makes Altium load the extension at startup,
/// which is how the bridge becomes available without user interaction in later sessions.
/// </summary>
public sealed class BridgePanelForm : ServerPanelForm
{
    private readonly Label _status = new();
    private readonly TextBox _log = new();
    private readonly Button _copyUrl = new();
    private readonly Button _restart = new();
    private readonly CheckBox _follow = new();
    private bool _syncingFollow;
    private readonly Func<string> _statusText;
    private readonly Action _restartBridge;
    private string _url = string.Empty;

    public BridgePanelForm(Func<string> statusText, Action restartBridge)
    {
        _statusText = statusText;
        _restartBridge = restartBridge;
        InitializeComponent();
        BridgeLog.LineWritten += OnLogLine;
        BridgeSettings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(Contracts.Model.BridgeSettingsInfo s)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            _syncingFollow = true;
            _follow.Checked = s.FollowMcpQueries;
        }
        catch
        {
            // ignore — UI may be tearing down
        }
        finally
        {
            _syncingFollow = false;
        }
    }

    public void SetUrl(string url) => _url = url;

    public void RefreshStatus()
    {
        try
        {
            _status.Text = _statusText();
        }
        catch (Exception ex)
        {
            _status.Text = "status error: " + ex.Message;
        }
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(420, 320);
        Text = "MCP Bridge";

        _status.Dock = DockStyle.Top;
        _status.Height = 64;
        _status.Padding = new Padding(6);
        _status.Text = "starting…";

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(4, 2, 4, 2) };
        _copyUrl.Text = "Copy URL";
        _copyUrl.AutoSize = true;
        _copyUrl.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(_url))
                {
                    Clipboard.SetText(_url);
                }
            }
            catch
            {
                // ignore
            }
        };
        _restart.Text = "Restart bridge";
        _restart.AutoSize = true;
        _restart.Click += (_, _) =>
        {
            try
            {
                _restartBridge();
                RefreshStatus();
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Restart failed", ex);
            }
        };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => RefreshStatus();
        buttons.Controls.AddRange(new Control[] { _copyUrl, _restart, refresh });

        // Cross Probe: when checked, component/net lookups made through MCP select and zoom to the object in the
        // editor (editor state only; documents are never modified). Persisted in settings.json.
        var options = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(6, 4, 4, 2) };
        _follow.Text = "Follow MCP queries in Altium (cross probe)";
        _follow.AutoSize = true;
        try
        {
            _follow.Checked = BridgeSettings.FollowMcpQueries;
        }
        catch
        {
            // settings unreadable → default off
        }

        _follow.CheckedChanged += (_, _) =>
        {
            if (_syncingFollow)
            {
                return;
            }

            try
            {
                BridgeSettings.FollowMcpQueries = _follow.Checked;
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Saving the cross-probe setting failed", ex);
            }
        };
        options.Controls.Add(_follow);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.Font = new Font(FontFamily.GenericMonospace, 8.25f);
        _log.Text = string.Join(Environment.NewLine, BridgeLog.GetRecent());

        Controls.Add(_log);
        Controls.Add(options);
        Controls.Add(buttons);
        Controls.Add(_status);
        ResumeLayout(false);
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLine), line);
            }
            else
            {
                AppendLine(line);
            }
        }
        catch
        {
            // ignore — UI may be tearing down
        }
    }

    private void AppendLine(string line)
    {
        if (_log.TextLength > 200_000)
        {
            _log.Clear();
        }

        _log.AppendText(line + Environment.NewLine);
        RefreshStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            BridgeLog.LineWritten -= OnLogLine;
            BridgeSettings.Changed -= OnSettingsChanged;
        }

        base.Dispose(disposing);
    }
}
