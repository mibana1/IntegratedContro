using IntegratedContro.Infrastructure;

namespace IntegratedContro.Tests;

public sealed class InitializationTests
{
    [Fact]
    public void Existing_setup_marker_with_missing_db_cannot_be_reinitialized()
    {
        using var r = new Rig();
        r.Store.Dispose();
        File.WriteAllText(Path.Combine(r.DirectoryPath, "host.json"), "{}");
        File.Delete(Path.Combine(r.DirectoryPath, "control.sqlite"));
        Assert.Throws<InvalidOperationException>(() => new SqliteStateStore(r.DirectoryPath, true));
        Assert.False(File.Exists(Path.Combine(r.DirectoryPath, "control.sqlite")));
    }
}
