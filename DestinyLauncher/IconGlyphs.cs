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
    // Brand paths: Simple Icons (CC0), github.com/simple-icons/simple-icons.
    public static Geometry Discord { get; } = Geometry.Parse("F1 M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z");
    public static Geometry YouTube { get; } = Geometry.Parse("F1 M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814zM9.545 15.568V8.432L15.818 12l-6.273 3.568z");
    public static Geometry X { get; } = Geometry.Parse("F1 M14.234 10.162 22.977 0h-2.072l-7.591 8.824L7.251 0H.258l9.168 13.343L.258 24H2.33l8.016-9.318L16.749 24h6.993zm-2.837 3.299-.929-1.329L3.076 1.56h3.182l5.965 8.532.929 1.329 7.754 11.09h-3.182z");
    public static Geometry Arrow { get; } = Geometry.Parse("M4 12h15 M13 6l6 6-6 6");
    public static Geometry Check { get; } = Geometry.Parse("M4 12l5 5L20 6");
    public static Geometry Edit { get; } = Geometry.Parse("M4 16.5V20h3.5L19 8.5 15.5 5 4 16.5z M13.5 7l3.5 3.5");
    public static Geometry Folder { get; } = Geometry.Parse("M3 6h7l2 2h9v11H3z M3 9h18");
    public static Geometry Minimize { get; } = Geometry.Parse("M5 12h14");
    public static Geometry Maximize { get; } = Geometry.Parse("M5 5h14v14H5z");
    public static Geometry Restore { get; } = Geometry.Parse("M8 5h11v11H8z M5 8v11h11");
    public static Geometry Close { get; } = Geometry.Parse("M6 6l12 12 M18 6 6 18");
}
