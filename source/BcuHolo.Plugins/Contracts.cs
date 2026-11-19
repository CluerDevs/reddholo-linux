using System.Runtime.CompilerServices;
using System.Windows;

// The sensor contracts moved to the cross-platform Reddholo.Core; forwarded so plugins built against this assembly still load.
[assembly: TypeForwardedTo(typeof(BcuHolo.Plugins.ISensorSource))]
[assembly: TypeForwardedTo(typeof(BcuHolo.Plugins.SensorReading))]

namespace BcuHolo.Plugins
{
    /// <summary>
    /// One entry in the rail: a full-page view plus an optional dashboard tile.
    /// Built-in modules and third-party plugins implement the same interface.
    /// </summary>
    public interface IHoloModule
    {
        /// <summary>Stable lowercase id, e.g. "apps". Used by <see cref="IHoloHost.Navigate"/>.</summary>
        string Id { get; }
        string Title { get; }
        /// <summary>Segoe Fluent Icons glyph, e.g. "\uE71D".</summary>
        string Glyph { get; }
        /// <summary>Rail position; lower comes first.</summary>
        int Order { get; }

        /// <summary>Called once; the host caches the view and re-shows it on navigation.</summary>
        FrameworkElement CreateView(IHoloHost host);

        /// <summary>Called once for the dashboard; return null for no tile.</summary>
        FrameworkElement CreateTile(IHoloHost host);
    }

    /// <summary>What the shell offers to modules.</summary>
    public interface IHoloHost
    {
        void Navigate(string moduleId);

        /// <summary>Yes/No dialog, defaulting to No. Every destructive action goes through this.</summary>
        bool Confirm(string title, string message);

        /// <summary>Small text badge next to the module in the rail (e.g. "75 GB"); null clears it.</summary>
        void SetBadge(string moduleId, string text);
    }
}
