using System.Windows.Media;

namespace DestinyLauncher;

public static class IconGlyphs
{
    public static Geometry Home { get; } = Geometry.Parse("M3 10.5 12 3l9 7.5 M5.5 9.5V21h13V9.5 M9.5 21v-6h5v6");
    public static Geometry News { get; } = Geometry.Parse("M4 4h16v16H4z M8 8h8 M8 12h8 M8 16h5");
    public static Geometry Cube { get; } = Geometry.Parse("M12 3 21 8l-9 5-9-5 9-5z M3 8v9l9 5 9-5V8 M12 13v9");
    public static Geometry Settings { get; } = Geometry.Parse("M10 2h4l1 3 2 .8 2.7-1.5 2 3.5-2.2 2.2v2l2.2 2.2-2 3.5-2.7-1.5-2 .8-1 3h-4l-1-3-2-.8-2.7 1.5-2-3.5 2.2-2.2v-2L2.3 8 4.3 4.5 7 6l2-.8 1-3z M14.5 12a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0z");
    public static Geometry Memory { get; } = Geometry.Parse("M7 7h10v10H7z M9 2v3 M15 2v3 M9 19v3 M15 19v3 M2 9h3 M2 15h3 M19 9h3 M19 15h3");
    public static Geometry Repair { get; } = Geometry.Parse("M14.5 6.5a5.5 5.5 0 0 0-7 7L3 18l3 3 4.5-4.5a5.5 5.5 0 0 0 7-7l-3.2 3.2-3-3 3.2-3.2z");
    public static Geometry Globe { get; } = Geometry.Parse("M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z M2 12h20 M12 2a15 15 0 0 1 0 20 M12 2a15 15 0 0 0 0 20");
    public static Geometry Discord { get; } = Geometry.Parse("M7 7c3-2 7-2 10 0l2 11c-3 2-5 2-7 1-2 1-4 1-7-1L7 7z M9 12h.01 M15 12h.01 M9 15c2 1 4 1 6 0");
    public static Geometry Arrow { get; } = Geometry.Parse("M4 12h15 M13 6l6 6-6 6");
    public static Geometry Check { get; } = Geometry.Parse("M4 12l5 5L20 6");
    public static Geometry Minimize { get; } = Geometry.Parse("M5 12h14");
    public static Geometry Maximize { get; } = Geometry.Parse("M5 5h14v14H5z");
    public static Geometry Restore { get; } = Geometry.Parse("M8 5h11v11H8z M5 8v11h11");
    public static Geometry Close { get; } = Geometry.Parse("M6 6l12 12 M18 6 6 18");
}
