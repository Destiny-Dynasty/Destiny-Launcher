using DestinyLauncher.Models;
using DestinyLauncher.Services;
using System.Text.Json;

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}
const long GiB = 1024L * 1024 * 1024;
foreach (var total in new[] { 2d, 4, 7.75, 8, 12, 15.75, 16, 24, 32, 64, 128 })
{
    var policy = new MemoryPolicy((long)(total * GiB));
    Check(policy.DefaultGb == Math.Max(1, (int)(total * .4)), $"40% default on {total} GiB");
    Check(policy.MaximumGb == (int)(total * .8), $"80% cap on {total} GiB");
    Check(policy.DefaultGb <= policy.MaximumGb, "default is within the cap");
    policy.Validate(policy.MaximumGb);
    try { policy.Validate(policy.MaximumGb + 1); throw new Exception("over-limit accepted"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("80 %"), "over-limit explains the cap"); }
    Check(policy.Normalize(1024) == policy.MaximumGb, "unsafe legacy allocation is reduced");
    Check(policy.Normalize(-1) == 1, "invalid legacy allocation is bounded");
}
var root = Path.Combine(Path.GetTempPath(), "destiny-memory-" + Guid.NewGuid().ToString("N"));
try
{
    var paths = new LauncherPaths(root); var store = new ConfigService(paths);
    Check(store.Load().RamGb == MemoryPolicy.System.DefaultGb, "fresh configuration uses detected system RAM");
    File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(new { RamGb = MemoryPolicy.System.MaximumGb + 1 }));
    Check(store.Load().RamGb == MemoryPolicy.System.MaximumGb, "old configuration cannot allocate beyond detected RAM");
    var config = new LauncherConfig { RamGb = MemoryPolicy.System.MaximumGb + 1 };
    try { store.Save(config); throw new Exception("unsafe save accepted"); }
    catch (InvalidOperationException) { Check(true, "unsafe configuration save is blocked"); }
    config.RamGb = MemoryPolicy.System.DefaultGb; store.Save(config);
    Check(store.Load().RamGb == config.RamGb, "valid choice survives reload");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
Console.WriteLine(JsonSerializer.Serialize(new { assertions, systemBytes = MemoryPolicy.System.TotalBytes, defaultGb = MemoryPolicy.System.DefaultGb, maximumGb = MemoryPolicy.System.MaximumGb }));
