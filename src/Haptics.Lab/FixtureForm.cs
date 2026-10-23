using System.Text.Json;
using Haptics.Core;

namespace Haptics.Lab;

public sealed record FixtureTruthItem(
    string Key,
    string ExpectedControlType,
    int[] Rect,
    bool Interactive,
    bool ExpectEnabled = true,
    bool ExpectPassword = false,
    string? AutomationId = null,
    bool WellFormed = true);

/// <summary>
/// Controlled fixture (SPEC §6.4 EXP-040): known controls with known semantics. With --truth it writes the
/// physical-pixel screen rectangles and expected UIA types so the lab can compare UIA against ground truth.
/// It also doubles as a playground: hover the toolbar, drag the slider, etc. with the Live Observer on.
/// </summary>
public sealed class FixtureForm : Form
{
    private readonly string? _truthPath;
    private readonly ToolStrip _toolbar = new() { Name = "toolbar", Dock = DockStyle.None, Location = new Point(12, 36), AutoSize = true, GripStyle = ToolStripGripStyle.Hidden };
    private readonly MenuStrip _menu = new() { Name = "menu" };
    private readonly Button _ok = new() { Name = "btn_ok", Text = "OK", Location = new Point(12, 80), Size = new Size(110, 36) };
    private readonly Button _cancel = new() { Name = "btn_cancel", Text = "Cancel", Location = new Point(132, 80), Size = new Size(110, 36) };
    private readonly Button _disabled = new() { Name = "btn_disabled", Text = "Disabled", Location = new Point(252, 80), Size = new Size(110, 36), Enabled = false };
    private readonly CheckBox _check = new() { Name = "chk_a", Text = "Check me", Location = new Point(12, 128), AutoSize = true };
    private readonly RadioButton _r1 = new() { Name = "rb_a", Text = "Option A", Location = new Point(132, 128), AutoSize = true, Checked = true };
    private readonly RadioButton _r2 = new() { Name = "rb_b", Text = "Option B", Location = new Point(252, 128), AutoSize = true };
    private readonly TabControl _tabs = new() { Name = "tabs", Location = new Point(12, 164), Size = new Size(350, 90) };
    private readonly TrackBar _slider = new() { Name = "slider", Location = new Point(12, 262), Size = new Size(350, 45), Minimum = 0, Maximum = 100, TickFrequency = 10, LargeChange = 10, SmallChange = 1, Value = 5 };
    private readonly TextBox _password = new() { Name = "txt_password", Location = new Point(12, 316), Size = new Size(200, 27), UseSystemPasswordChar = true, Text = "fixture-not-a-secret" };
    private readonly ListBox _list = new() { Name = "list", Location = new Point(380, 36), Size = new Size(180, 140) };
    private readonly Panel _opaque = new() { Name = "opaque_canvas", Location = new Point(380, 190), Size = new Size(180, 150), BackColor = Color.FromArgb(40, 44, 52) };

    public FixtureForm(string? truthPath)
    {
        _truthPath = truthPath;
        Text = "Haptics Lab Fixture (controlled UI — safe to hover)";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(80, 80);
        ClientSize = new Size(580, 360);
        Font = new Font("Segoe UI", 9.5f);
        TopMost = truthPath is not null;

        _menu.Items.Add(new ToolStripMenuItem("File") { Name = "menu_file" });
        _menu.Items.Add(new ToolStripMenuItem("Edit") { Name = "menu_edit" });
        ((ToolStripMenuItem)_menu.Items[0]).DropDownItems.AddRange([new ToolStripMenuItem("New"), new ToolStripMenuItem("Open"), new ToolStripMenuItem("Save")]);
        foreach (var (n, t, en) in new[] { ("tb_bold", "Bold", true), ("tb_italic", "Italic", true), ("tb_underline", "Underline", true), ("tb_disabled", "Off", false) })
        {
            _toolbar.Items.Add(new ToolStripButton(t) { Name = n, Enabled = en, AutoSize = false, Size = new Size(64, 28) });
        }

        foreach (var t in new[] { "One", "Two", "Three" })
        {
            _tabs.TabPages.Add(new TabPage(t) { Name = "tab_" + t.ToLowerInvariant() });
        }

        _list.Items.AddRange(["Alpha", "Bravo", "Charlie", "Delta", "Echo"]);
        _opaque.Paint += (_, e) =>
        {
            // Looks like buttons, exposes nothing: the honest answer for UIA is "opaque".
            using var b = new SolidBrush(Color.FromArgb(90, 120, 200));
            for (var i = 0; i < 3; i++)
            {
                e.Graphics.FillRectangle(b, 12, 12 + (i * 44), 150, 34);
                TextRenderer.DrawText(e.Graphics, $"Painted {i + 1}", Font, new Point(24, 20 + (i * 44)), Color.White);
            }
        };

        Controls.AddRange([_toolbar, _ok, _cancel, _disabled, _check, _r1, _r2, _tabs, _slider, _password, _list, _opaque, _menu]);
        MainMenuStrip = _menu;
        Shown += async (_, _) =>
        {
            if (_truthPath is not null)
            {
                // Re-assert z-order on our own window only (no activation). TopMost set before handle creation
                // is not reliably applied when launched from a background process.
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }

            await Task.Delay(400);
            WriteTruth();
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private void WriteTruth()
    {
        if (_truthPath is null)
        {
            return;
        }

        int[] R(Rectangle r) => [r.X, r.Y, r.Width, r.Height];
        Rectangle Screen(Control c) => c.RectangleToScreen(c.ClientRectangle);
        Rectangle Item(ToolStrip s, ToolStripItem i) => s.RectangleToScreen(i.Bounds);

        var items = new List<FixtureTruthItem>
        {
            new("btn_ok", "Button", R(Screen(_ok)), true, AutomationId: "btn_ok"),
            new("btn_cancel", "Button", R(Screen(_cancel)), true, AutomationId: "btn_cancel"),
            new("btn_disabled", "Button", R(Screen(_disabled)), true, ExpectEnabled: false, AutomationId: "btn_disabled"),
            new("chk_a", "CheckBox", R(Screen(_check)), true, AutomationId: "chk_a"),
            new("rb_a", "RadioButton", R(Screen(_r1)), true, AutomationId: "rb_a"),
            new("rb_b", "RadioButton", R(Screen(_r2)), true, AutomationId: "rb_b"),
            new("slider", "Slider", R(Screen(_slider)), true, AutomationId: "slider"),
            new("txt_password", "Edit", R(Screen(_password)), false, ExpectPassword: true, AutomationId: "txt_password"),
            new("opaque_canvas", "Pane", R(Screen(_opaque)), false, AutomationId: "opaque_canvas"),
        };

        foreach (ToolStripItem i in _toolbar.Items)
        {
            items.Add(new FixtureTruthItem(i.Name!, "Button", R(Item(_toolbar, i)), true, ExpectEnabled: i.Enabled));
        }

        foreach (ToolStripItem i in _menu.Items)
        {
            items.Add(new FixtureTruthItem(i.Name!, "MenuItem", R(Item(_menu, i)), true));
        }

        for (var t = 0; t < _tabs.TabCount; t++)
        {
            items.Add(new FixtureTruthItem($"tab_{t}", "TabItem", R(_tabs.RectangleToScreen(_tabs.GetTabRect(t))), true));
        }

        for (var l = 0; l < _list.Items.Count; l++)
        {
            items.Add(new FixtureTruthItem($"list_{l}", "ListItem", R(_list.RectangleToScreen(_list.GetItemRectangle(l))), true));
        }

        var json = JsonSerializer.Serialize(new { pid = Environment.ProcessId, dpi = DeviceDpi, items }, HapticsJson.Indented);
        var tmp = _truthPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _truthPath, overwrite: true);
    }
}
