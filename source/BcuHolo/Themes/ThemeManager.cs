using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace BcuHolo.Themes
{
    /// <summary>
    /// Swaps palette resources on Application.Resources. Consumers must use DynamicResource / SetResourceReference to follow
    /// a live switch (brushes are replaced, never mutated: brushes inside sealed styles are frozen).
    ///
    /// Keys:  Bg Rail Tile Text Muted Other Accent Line Good Danger Violet Blue    solid brushes
    ///        Wsl SysFiles AccentSoft DangerSoft PanelLine RowLine Track Panel     legacy aliases
    ///        GlassLine GlassFill AccentGlassFill                                   gradients
    ///        AccentText (accent-tinted text on accent glass)  OnDanger (text on a Danger-filled button)
    ///        <Hue>.<AA>   e.g. Accent.24, Good.1A  brush of that hue at hex alpha AA (Alphas below)
    ///        <Hue>.<AA>.C the same as a Color (for GradientStop.Color etc.)
    ///        <Hue>.C      the opaque Color
    /// </summary>
    public static class ThemeManager
    {
        public static readonly string[] Hues = Theme.Hues;
        public static readonly string[] Alphas = Theme.Alphas;
        public static IReadOnlyList<Theme> All => Theme.All;

        private static readonly string SettingsPath = Path.Combine(BcuHolo.App.DataDir, "settings.json");

        public static Theme Current { get; private set; } = All[0];
        public static event Action Changed;

        private sealed class Settings { public string Theme { get; set; } }

        public static void LoadSaved()
        {
            string name = null;
            try { if (File.Exists(SettingsPath)) name = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.Theme; }
            catch (Exception) { /* bad settings file: fall back to the default theme */ }
            Apply(All.FirstOrDefault(t => t.Name == name) ?? All[0], save: false);
        }

        public static void Apply(Theme t, bool save = true)
        {
            var r = Application.Current.Resources;
            var baseColors = new Dictionary<string, Color>
            {
                ["Bg"] = C(t.Bg), ["Rail"] = C(t.Rail), ["Tile"] = C(t.Tile), ["Text"] = C(t.Text), ["Muted"] = C(t.Muted), ["Other"] = C(t.Other),
                ["Accent"] = C(t.Accent), ["Line"] = C(t.Line), ["Good"] = C(t.Good), ["Danger"] = C(t.Danger), ["Violet"] = C(t.Violet), ["Blue"] = C(t.Blue),
            };
            foreach (var (hue, c) in baseColors)
            {
                r[hue] = Brush(c);
                r[hue + ".C"] = c;
                foreach (var a in Alphas)
                {
                    var ac = WithAlpha(c, Convert.ToByte(a, 16));
                    r[$"{hue}.{a}"] = Brush(ac);
                    r[$"{hue}.{a}.C"] = ac;
                }
            }
            // Names older views use.
            r["Wsl"] = Brush(baseColors["Violet"]);
            r["SysFiles"] = Brush(baseColors["Blue"]);
            r["AccentSoft"] = r["Accent.1A"];
            r["DangerSoft"] = r["Danger.12"];
            r["PanelLine"] = r["Line.24"];
            r["RowLine"] = r["Line.12"];
            r["Track"] = r["Line.14"];
            r["Panel"] = Brush(Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF));

            // Glass: light catches the top-left edge, fades through the middle, picks up the accent bottom-right.
            var line = baseColors["Line"];
            r["GlassLine"] = Gradient(new Point(0, 0), new Point(1, 1),
                (WithAlpha(Lerp(line, Colors.White, 0.6), 0x8C), 0), (WithAlpha(line, 0x2E), 0.35), (WithAlpha(line, 0x14), 0.7), (WithAlpha(baseColors["Accent"], 0x59), 1));
            r["AccentGlassFill"] = Gradient(new Point(0, 0), new Point(0, 1), (WithAlpha(baseColors["Accent"], 0x40), 0), (WithAlpha(baseColors["Accent"], 0x1A), 1));
            r["AccentText"] = Brush(Lerp(baseColors["Accent"], Colors.White, 0.7));
            r["OnDanger"] = Brush(Lerp(baseColors["Danger"], Colors.Black, 0.9));
            r["GlassFill"] = Gradient(new Point(0, 0), new Point(0, 1),
                (Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x07, 0xFF, 0xFF, 0xFF), 1));

            Current = t;
            if (save) Save();
            Changed?.Invoke();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { Theme = Current.Name }));
            }
            catch (Exception) { /* the theme still applies for this session */ }
        }

        public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        private static Color Lerp(Color a, Color b, double t) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        private static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static LinearGradientBrush Gradient(Point start, Point end, params (Color c, double o)[] stops)
        {
            var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            foreach (var (c, o) in stops) g.GradientStops.Add(new GradientStop(c, o));
            g.Freeze();
            return g;
        }
    }
}
