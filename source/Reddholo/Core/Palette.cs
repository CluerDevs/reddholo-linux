using System.Collections.Generic;

namespace BcuHolo.Themes
{
    /// <summary>One dark palette (hex colours). Glass whites/blacks stay neutral in every theme, so light themes aren't supported.
    /// Shared by the WPF and Avalonia shells; each turns it into its own brushes.</summary>
    public sealed record Theme(string Name, string Blurb,
        string Bg, string Rail, string Tile, string Text, string Muted, string Other,
        string Accent, string Line, string Good, string Danger, string Violet, string Blue)
    {
        public static readonly IReadOnlyList<Theme> All = new[]
        {
            new Theme("Holo", "Cyan glass, the original",
                "#070B14", "#0A1020", "#121B2E", "#E6F1FF", "#9FB2CC", "#3A4A66", "#3DDCFF", "#78C8FF", "#5BE7A9", "#FF8A3D", "#A78BFA", "#5B8DEF"),
            new Theme("Ember", "Amber on charcoal",
                "#0E0907", "#150D0A", "#22140F", "#FFF1E6", "#CDB4A3", "#5A4034", "#FFB13D", "#FF9A6B", "#8BE38B", "#FF4D6A", "#F472B6", "#FFCF7A"),
            new Theme("Phosphor", "Green terminal glow",
                "#040C08", "#07130D", "#0E1F16", "#E6FFF0", "#9CC4AE", "#2F4A3B", "#39FF88", "#5CE6A0", "#B6FF5C", "#FFB13D", "#5CC8FF", "#3DDCB0"),
            new Theme("Synthwave", "Magenta and violet neon",
                "#0D0616", "#140A20", "#1E1030", "#F7E8FF", "#BCA3D1", "#4A3466", "#FF4FD8", "#B07CFF", "#3DF5E0", "#FFB13D", "#7C8CFF", "#FF7AB6"),
            new Theme("Stealth", "Graphite with a red edge",
                "#0A0B0D", "#0F1113", "#181A1E", "#EEF1F5", "#A3A9B3", "#3D424B", "#FF4D4D", "#A0AEC0", "#5BE7A9", "#FFB13D", "#9AA5B8", "#C7CED9"),
            new Theme("Aurum", "Gold on midnight blue",
                "#070A12", "#0B1020", "#131A2C", "#F5F0E1", "#B8B09A", "#4A4636", "#FFD24D", "#E0C27A", "#7CE3A0", "#FF7A4D", "#C9A6FF", "#8FB4FF"),
        };

        public static readonly string[] Hues = { "Bg", "Rail", "Tile", "Text", "Muted", "Other", "Accent", "Line", "Good", "Danger", "Violet", "Blue" };
        public static readonly string[] Alphas =
            { "00", "08", "0A", "0F", "12", "14", "1A", "1F", "24", "29", "2E", "33", "38", "40", "4D", "52", "55", "59", "5A", "66", "80", "99", "B3", "BF", "CC", "E6", "EB" };

        public string Hex(string hue) => hue switch
        {
            "Bg" => Bg, "Rail" => Rail, "Tile" => Tile, "Text" => Text, "Muted" => Muted, "Other" => Other,
            "Accent" => Accent, "Line" => Line, "Good" => Good, "Danger" => Danger, "Violet" => Violet, "Blue" => Blue,
            _ => null,
        };
    }
}
