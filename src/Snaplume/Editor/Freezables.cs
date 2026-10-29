using System.Windows;

namespace Snaplume.Editor;

/// <summary>Helpers shared by the editor's drawing code (imported with <c>using static</c>).</summary>
internal static class Freezables
{
    /// <summary>Freezes a brush, pen or geometry and returns it: frozen objects are faster to draw and can be shared.</summary>
    internal static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
