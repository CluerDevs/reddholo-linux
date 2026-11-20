using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using BcuHolo.Themes;

namespace Reddholo.App
{
    /// <summary>
    /// Turns a <see cref="Theme"/> palette into Application resources, with the same keys as the WPF app's ThemeManager:
    /// solid hues (Accent), hue at hex alpha (Accent.24), colours (Accent.C, Accent.24.C), glass gradients and aliases.
    /// Consumers use DynamicResource so a theme switch applies live.
    /// </summary>
    internal static class Themes
    {
        private static readonly string SettingsPath = Path.Combine(App.DataDir, "settings.json");

        public static Theme Current { get; private set; } = Theme.All[0];

        private sealed class Settings { public string Theme { get; set; } }

        public static void LoadSaved()
        {
            string name = null;
            try { if (File.Exists(SettingsPath)) name = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.Theme; }
            catch (Exception) { /* bad settings file: fall back to the default theme */ }
            Apply(Theme.All.FirstOrDefault(t => t.Name == name) ?? Theme.All[0], save: false);
        }

        public static void Apply(Theme t, bool save = true)
        {
            var r = Application.Current.Resources;
            foreach (var hue in Theme.Hues)
            {
                var c = Color.Parse(t.Hex(hue));
                r[hue] = new ImmutableSolidColorBrush(c);
                r[hue + ".C"] = c;
                foreach (var a in Theme.Alphas)
                {
                    var ac = WithAlpha(c, Convert.ToByte(a, 16));
                    r[$"{hue}.{a}"] = new ImmutableSolidColorBrush(ac);
                    r[$"{hue}.{a}.C"] = ac;
                }
            }
            r["AccentSoft"] = r["Accent.1A"];
            r["DangerSoft"] = r["Danger.12"];
            r["PanelLine"] = r["Line.24"];

            Color accent = Color.Parse(t.Accent), line = Color.Parse(t.Line), danger = Color.Parse(t.Danger);
            // Glass: light catches the top-left edge, fades through the middle, picks up the accent bottom-right.
            r["GlassLine"] = Gradient(new Point(0, 0), new Point(1, 1),
                (WithAlpha(Lerp(line, Colors.White, 0.6), 0x8C), 0), (WithAlpha(line, 0x2E), 0.35), (WithAlpha(line, 0x14), 0.7), (WithAlpha(accent, 0x59), 1));
            r["GlassFill"] = Gradient(new Point(0, 0), new Point(0, 1), (Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x07, 0xFF, 0xFF, 0xFF), 1));
            r["AccentGlassFill"] = Gradient(new Point(0, 0), new Point(0, 1), (WithAlpha(accent, 0x40), 0), (WithAlpha(accent, 0x1A), 1));
            r["RailSelected"] = Gradient(new Point(0, 0), new Point(1, 0), (WithAlpha(accent, 0x38), 0), (WithAlpha(accent, 0x0A), 1));
            r["AccentText"] = new ImmutableSolidColorBrush(Lerp(accent, Colors.White, 0.7));
            r["OnDanger"] = new ImmutableSolidColorBrush(Lerp(danger, Colors.Black, 0.9));
            // Fluent controls (sliders, focus) take their highlight from these.
            r["SystemAccentColor"] = accent;
            for (int i = 1; i <= 3; i++)
            {
                r["SystemAccentColorLight" + i] = Lerp(accent, Colors.White, 0.15 * i);
                r["SystemAccentColorDark" + i] = Lerp(accent, Colors.Black, 0.15 * i);
            }

            Current = t;
            if (save) Save();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(App.DataDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings { Theme = Current.Name }));
            }
            catch (Exception) { /* the theme still applies for this session */ }
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        private static Color Lerp(Color a, Color b, double t) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        private static IBrush Gradient(Point start, Point end, params (Color c, double o)[] stops) =>
            new ImmutableLinearGradientBrush(
                stops.Select(s => new ImmutableGradientStop(s.o, s.c)).ToArray(),
                startPoint: new RelativePoint(start, RelativeUnit.Relative),
                endPoint: new RelativePoint(end, RelativeUnit.Relative));
    }
}
