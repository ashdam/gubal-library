namespace GubalDesktop;

internal sealed class InstallationFooter : StatusStrip
{
    private readonly ToolStripStatusLabel label = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, AutoToolTip = false };

    public InstallationFooter()
    {
        Dock = DockStyle.Bottom;
        SizingGrip = false;
        ShowItemToolTips = true;
        Padding = new Padding(12, 3, 12, 3);
        Items.Add(label);
    }

    public void SetFolder(string folder)
    {
        var found = GameDetection.Normalize(folder);
        label.Text = UiText.Get(found is null ? "ChooseGameFolder" : "GameFound");
        label.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText
            : found is null ? Color.FromArgb(237, 200, 125) : Color.FromArgb(155, 213, 175);
        label.ToolTipText = found ?? "";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(SystemInformation.HighContrast ? SystemColors.ControlDark : Color.FromArgb(52, 56, 61));
        e.Graphics.DrawLine(border, 0, 0, ClientSize.Width, 0);
    }
}
