using System.Text;
using FluentAssertions;
using MagicRepos.Core.Storage;

namespace MagicRepos.Core.Tests.Storage;

public class AtomicFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "magicrepos-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void WriteAllBytes_creates_file_and_leaves_no_temp_files()
    {
        string path = Path.Combine(_tempDir, "obj.bin");
        byte[] data = { 1, 2, 3, 4, 5 };

        AtomicFile.WriteAllBytes(path, data);

        File.ReadAllBytes(path).Should().Equal(data);
        Directory.GetFiles(_tempDir).Should().ContainSingle();
    }

    [Fact]
    public void WriteAllText_overwrites_existing_file_atomically()
    {
        string path = Path.Combine(_tempDir, "config");
        AtomicFile.WriteAllText(path, "old contents");
        AtomicFile.WriteAllText(path, "new contents");

        File.ReadAllText(path).Should().Be("new contents");
        // Written as UTF-8 without a BOM.
        File.ReadAllBytes(path).Take(3).Should().NotEqual(Encoding.UTF8.GetPreamble());
    }

    [Fact]
    public void WriteAllBytes_creates_missing_directories()
    {
        string path = Path.Combine(_tempDir, "a", "b", "c.bin");
        AtomicFile.WriteAllBytes(path, new byte[] { 42 });

        File.Exists(path).Should().BeTrue();
    }
}
