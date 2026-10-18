using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed class SkinFixtureHandler : HttpMessageHandler
{
    internal int Requests { get; private set; }
    internal bool Unavailable { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Requests++;
        if (Unavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var uri = request.RequestUri!;
        if (uri.Host == "api.mojang.com") return Json(new { id = new string('a', 32) });
        if (uri.Host == "sessionserver.mojang.com")
        {
            var texture = new { textures = new { SKIN = new { url = "http://textures.minecraft.net/texture/abcdef123456" } } };
            return Json(new { properties = new[] { new { name = "textures", value = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(texture))) } } });
        }
        if (uri.Scheme != "https" || uri.Host != "textures.minecraft.net") throw new InvalidOperationException("Unexpected skin URL");
        var pixels = new byte[64 * 64 * 4];
        void Pixel(int x, int y, byte r, byte g, byte b)
        { var i = (y * 64 + x) * 4; pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
        for (var y = 8; y < 16; y++) for (var x = 8; x < 16; x++) Pixel(x, y, 200, 80, 30);
        Pixel(40, 8, 10, 20, 220);
        var skin = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(skin));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
    }
    private static Task<HttpResponseMessage> Json<T>(T value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
}
