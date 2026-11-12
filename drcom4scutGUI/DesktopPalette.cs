using Avalonia.Media;

namespace drcom4scutGUI;

// Window-owned brushes update existing controls without rebuilding their trees.
internal sealed class DesktopPalette
{
    internal SolidColorBrush Ink { get; } = new();
    internal SolidColorBrush Muted { get; } = new();
    internal SolidColorBrush Accent { get; } = new();
    internal SolidColorBrush OnAccent { get; } = new();
    internal SolidColorBrush Surface { get; } = new();
    internal SolidColorBrush Paper { get; } = new();
    internal SolidColorBrush Soft { get; } = new();
    internal SolidColorBrush Line { get; } = new();
    internal SolidColorBrush Hover { get; } = new();
    internal SolidColorBrush Pressed { get; } = new();
    internal SolidColorBrush AccentHover { get; } = new();
    internal SolidColorBrush AccentPressed { get; } = new();
    internal SolidColorBrush Error { get; } = new();

    internal DesktopPalette() => Apply(false);

    internal void Apply(bool dark)
    {
        Ink.Color = Color.Parse(dark ? "#E5ECEC" : "#25373B");
        Muted.Color = Color.Parse(dark ? "#B1C1C5" : "#57666C");
        Accent.Color = Color.Parse(dark ? "#A7D5E2" : "#23566D");
        OnAccent.Color = Color.Parse(dark ? "#152E39" : "#FAFCFC");
        Surface.Color = Color.Parse(dark ? "#172227" : "#F8FAF9");
        Paper.Color = Color.Parse(dark ? "#1D2B31" : "#FEFEFC");
        Soft.Color = Color.Parse(dark ? "#24353B" : "#ECF1F0");
        Line.Color = Color.Parse(dark ? "#788D95" : "#7B8E91");
        Hover.Color = Color.Parse(dark ? "#2C424A" : "#E2ECEE");
        Pressed.Color = Color.Parse(dark ? "#39525A" : "#D1E0E3");
        AccentHover.Color = Color.Parse(dark ? "#C5E4EC" : "#1B485D");
        AccentPressed.Color = Color.Parse(dark ? "#8EC5D4" : "#143B50");
        Error.Color = Color.Parse(dark ? "#FFB4A3" : "#9C4337");
    }
}
