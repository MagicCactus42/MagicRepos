using System.Text;
using FluentAssertions;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Refs;

namespace MagicRepos.Core.Tests.Refs;

public class RefStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RefStore _refs;

    public RefStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "magicrepos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "refs", "heads"));
        _refs = new RefStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static ObjectId MakeId(string seed) =>
        ObjectId.Hash(Encoding.UTF8.GetBytes(seed));

    [Fact]
    public void WriteHead_and_ReadHead_roundtrip_symbolic_ref()
    {
        // Arrange
        string symbolicRef = "ref: refs/heads/main";

        // Act
        _refs.WriteHead(symbolicRef);
        string result = _refs.ReadHead();

        // Assert
        result.Should().Be(symbolicRef);
    }

    [Fact]
    public void WriteHead_and_ReadHead_roundtrip_detached_hash()
    {
        // Arrange
        ObjectId id = MakeId("commit-hash");
        string hex = id.ToHexString();

        // Act
        _refs.WriteHead(hex);
        string result = _refs.ReadHead();

        // Assert
        result.Should().Be(hex);
    }

    [Fact]
    public void IsDetachedHead_returns_true_for_raw_hash()
    {
        // Arrange
        _refs.WriteHead(MakeId("detached").ToHexString());

        // Act & Assert
        _refs.IsDetachedHead().Should().BeTrue();
    }

    [Fact]
    public void IsDetachedHead_returns_false_for_symbolic_ref()
    {
        // Arrange
        _refs.WriteHead("ref: refs/heads/main");

        // Act & Assert
        _refs.IsDetachedHead().Should().BeFalse();
    }

    [Fact]
    public void GetCurrentBranchName_returns_branch_name()
    {
        // Arrange
        _refs.WriteHead("ref: refs/heads/feature-x");

        // Act
        string? branch = _refs.GetCurrentBranchName();

        // Assert
        branch.Should().Be("feature-x");
    }

    [Fact]
    public void GetCurrentBranchName_returns_null_when_detached()
    {
        // Arrange
        _refs.WriteHead(MakeId("detached").ToHexString());

        // Act
        string? branch = _refs.GetCurrentBranchName();

        // Assert
        branch.Should().BeNull();
    }

    [Fact]
    public void CreateBranch_and_ResolveBranch_roundtrip()
    {
        // Arrange
        ObjectId commitId = MakeId("branch-commit");

        // Act
        _refs.CreateBranch("feature", commitId);
        ObjectId? resolved = _refs.ResolveBranch("feature");

        // Assert
        resolved.Should().NotBeNull();
        resolved!.Value.Should().Be(commitId);
    }

    [Fact]
    public void ResolveBranch_returns_null_for_nonexistent_branch()
    {
        // Act & Assert
        _refs.ResolveBranch("nonexistent").Should().BeNull();
    }

    [Fact]
    public void DeleteBranch_removes_branch()
    {
        // Arrange
        ObjectId commitId = MakeId("to-delete");
        _refs.CreateBranch("temp", commitId);
        _refs.ResolveBranch("temp").Should().NotBeNull();

        // Act
        _refs.DeleteBranch("temp");

        // Assert
        _refs.ResolveBranch("temp").Should().BeNull();
    }

    [Fact]
    public void DeleteBranch_nonexistent_is_noop()
    {
        // Act — should not throw
        Action act = () => _refs.DeleteBranch("nonexistent");
        act.Should().NotThrow();
    }

    [Fact]
    public void ListBranches_returns_all_branches()
    {
        // Arrange
        _refs.CreateBranch("alpha", MakeId("a"));
        _refs.CreateBranch("beta", MakeId("b"));
        _refs.CreateBranch("gamma", MakeId("g"));

        // Act
        IReadOnlyList<string> branches = _refs.ListBranches();

        // Assert
        branches.Should().BeEquivalentTo(["alpha", "beta", "gamma"]);
        branches.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void ListBranches_returns_empty_when_no_branches()
    {
        // Act & Assert
        _refs.ListBranches().Should().BeEmpty();
    }

    [Fact]
    public void ResolveHead_follows_symbolic_ref()
    {
        // Arrange
        ObjectId commitId = MakeId("head-commit");
        _refs.CreateBranch("main", commitId);
        _refs.WriteHead("ref: refs/heads/main");

        // Act
        ObjectId? resolved = _refs.ResolveHead();

        // Assert
        resolved.Should().NotBeNull();
        resolved!.Value.Should().Be(commitId);
    }

    [Fact]
    public void ResolveHead_returns_null_for_unborn_branch()
    {
        // Arrange — HEAD points to a branch that has no commits yet
        _refs.WriteHead("ref: refs/heads/main");

        // Act
        ObjectId? resolved = _refs.ResolveHead();

        // Assert
        resolved.Should().BeNull();
    }

    [Fact]
    public void ResolveHead_returns_id_for_detached_head()
    {
        // Arrange
        ObjectId commitId = MakeId("detached-commit");
        _refs.WriteHead(commitId.ToHexString());

        // Act
        ObjectId? resolved = _refs.ResolveHead();

        // Assert
        resolved.Should().NotBeNull();
        resolved!.Value.Should().Be(commitId);
    }

    [Fact]
    public void Resolve_accepts_HEAD_keyword()
    {
        // Arrange
        ObjectId commitId = MakeId("resolve-head");
        _refs.CreateBranch("main", commitId);
        _refs.WriteHead("ref: refs/heads/main");

        // Act
        ObjectId? resolved = _refs.Resolve("HEAD");

        // Assert
        resolved.Should().Be(commitId);
    }

    [Fact]
    public void Resolve_accepts_branch_name()
    {
        // Arrange
        ObjectId commitId = MakeId("resolve-branch");
        _refs.CreateBranch("develop", commitId);

        // Act
        ObjectId? resolved = _refs.Resolve("develop");

        // Assert
        resolved.Should().Be(commitId);
    }

    [Fact]
    public void Resolve_accepts_raw_hex_hash()
    {
        // Arrange
        ObjectId commitId = MakeId("resolve-hash");
        string hex = commitId.ToHexString();

        // Act
        ObjectId? resolved = _refs.Resolve(hex);

        // Assert
        resolved.Should().Be(commitId);
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_ref()
    {
        // Act & Assert
        _refs.Resolve("nonexistent-branch").Should().BeNull();
    }

    [Fact]
    public void WriteRef_and_ReadRef_roundtrip()
    {
        // Arrange
        ObjectId id = MakeId("ref-data");

        // Act
        _refs.WriteRef("tags/v1.0", id);
        ObjectId? resolved = _refs.ReadRef("tags/v1.0");

        // Assert
        resolved.Should().NotBeNull();
        resolved!.Value.Should().Be(id);
    }
}
