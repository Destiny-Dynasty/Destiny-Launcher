using System.Windows.Media;

namespace DestinyLauncher.Models;

public sealed class NewsFeedDocument
{
    public int SchemaVersion { get; set; }
    public List<NewsFeedItem> Items { get; set; } = [];
}

public sealed class NewsFeedItem
{
    public string Id { get; set; } = "";
    public string Caption { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string? LinkUrl { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed record NewsSlide(string Id, string Caption, ImageSource Image, Uri? Link = null);
