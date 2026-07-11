using FluentAssertions;
using MagicRepos.Core;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Tests;

/// <summary>
/// Regression tests for correctness bugs found during the production-readiness review.
/// </summary>
public class RepositoryBugFixTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryBugFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "magicrepos-bugfix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static Signature Sig => new("Test", "test@example.com",
        new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private string Path_(string rel) => Path.Combine(_tempDir, rel);

    private void Write(string rel, string content) => File.WriteAllText(Path_(rel), content);

    [Fact]
    public void Checkout_refuses_to_overwrite_uncommitted_changes()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("f.txt", "committed");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);
        repo.CreateBranch("feature");

        // Uncommitted modification on the current branch.
        Write("f.txt", "PRECIOUS UNCOMMITTED");

        Action act = () => repo.CheckoutBranch("feature");

        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path_("f.txt")).Should().Be("PRECIOUS UNCOMMITTED");
    }

    [Fact]
    public void Checkout_force_discards_uncommitted_changes()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("f.txt", "committed");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);
        repo.CreateBranch("feature");
        Write("f.txt", "uncommitted");

        repo.CheckoutBranch("feature", force: true);

        File.ReadAllText(Path_("f.txt")).Should().Be("committed");
    }

    [Fact]
    public void Checkout_does_not_clobber_untracked_file_with_different_content()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("base.txt", "base");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);

        // 'feat.txt' is committed only on the feature branch.
        repo.CreateBranch("feature");
        repo.CheckoutBranch("feature");
        Write("feat.txt", "from feature");
        repo.StageAll();
        repo.CreateCommit("c2", Sig);

        // Back on main, an untracked feat.txt with different content must not be clobbered.
        repo.CheckoutBranch("main");
        Write("feat.txt", "MY UNTRACKED WORK");

        Action act = () => repo.CheckoutBranch("feature");
        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path_("feat.txt")).Should().Be("MY UNTRACKED WORK");
    }

    [Fact]
    public void Checkout_refuses_to_discard_staged_changes()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("f.txt", "committed");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);
        repo.CreateBranch("feature");

        // Modification staged but not committed on the current branch.
        Write("f.txt", "STAGED UNCOMMITTED");
        repo.StageAll();

        Action act = () => repo.CheckoutBranch("feature");

        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path_("f.txt")).Should().Be("STAGED UNCOMMITTED");
    }

    [Fact]
    public void Checkout_refuses_to_delete_staged_new_file()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("base.txt", "base");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);
        repo.CreateBranch("feature");

        // A brand-new file staged but not committed; it exists in neither branch tip.
        Write("new.txt", "STAGED NEW FILE");
        repo.StageAll();

        Action act = () => repo.CheckoutBranch("feature");

        act.Should().Throw<InvalidOperationException>();
        File.ReadAllText(Path_("new.txt")).Should().Be("STAGED NEW FILE");
    }

    [Fact]
    public void Checkout_force_discards_staged_changes()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("f.txt", "committed");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);
        repo.CreateBranch("feature");
        Write("f.txt", "staged");
        repo.StageAll();

        repo.CheckoutBranch("feature", force: true);

        File.ReadAllText(Path_("f.txt")).Should().Be("committed");
    }

    [Fact]
    public void ResetHard_removes_files_tracked_only_before_the_reset()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("keep.txt", "keep");
        repo.StageAll();
        ObjectId c1 = repo.CreateCommit("c1", Sig);

        Write("extra.txt", "extra");
        repo.StageAll();
        repo.CreateCommit("c2", Sig);

        repo.Reset(c1.ToHexString(), ResetMode.Hard);

        File.Exists(Path_("extra.txt")).Should().BeFalse("extra.txt existed only after c1");
        File.Exists(Path_("keep.txt")).Should().BeTrue();
        repo.IsWorkingTreeClean().Should().BeTrue();
    }

    [Fact]
    public void StageAll_does_not_delete_tracked_file_that_becomes_ignored()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("app.log", "log data");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);

        // Now ignore *.log; the tracked file still exists on disk.
        Write(".magicreposignore", "*.log\n");
        repo.StageAll();

        RepositoryStatus status = repo.GetStatus();
        status.StagedChanges.Should().NotContain(c => c.Path == "app.log" && c.Status == FileStatusType.Deleted);
        status.UnstagedChanges.Should().NotContain(c => c.Path == "app.log" && c.Status == FileStatusType.Deleted);
    }

    [Fact]
    public void GetStatus_does_not_report_tracked_ignored_file_as_deleted()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("app.log", "log data");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);
        Write(".magicreposignore", "*.log\n");

        RepositoryStatus status = repo.GetStatus();

        status.UnstagedChanges.Should().NotContain(c => c.Path == "app.log");
    }

    [Fact]
    public void CreateCommit_can_commit_deletion_of_the_last_file()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("only.txt", "only");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);

        File.Delete(Path_("only.txt"));
        repo.StageAll();

        Action act = () => repo.CreateCommit("delete only", Sig);
        act.Should().NotThrow();

        RepositoryStatus status = repo.GetStatus();
        status.StagedChanges.Should().BeEmpty();
    }

    [Fact]
    public void CreateCommit_rejects_a_no_op_commit()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("a.txt", "a");
        repo.StageAll();
        repo.CreateCommit("c1", Sig);

        // Nothing changed since c1.
        repo.StageAll();
        Action act = () => repo.CreateCommit("noop", Sig);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void IsAncestor_detects_linear_history()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("a.txt", "1");
        repo.StageAll();
        ObjectId c1 = repo.CreateCommit("c1", Sig);
        Write("a.txt", "2");
        repo.StageAll();
        ObjectId c2 = repo.CreateCommit("c2", Sig);

        repo.IsAncestor(c1, c2).Should().BeTrue();
        repo.IsAncestor(c2, c1).Should().BeFalse();
        repo.IsAncestor(c1, c1).Should().BeTrue();
    }

    [Fact]
    public void StageFile_replaces_stale_file_entry_when_a_directory_takes_its_place()
    {
        Repository repo = Repository.Init(_tempDir);
        Write("a", "file a");
        repo.StageFile("a");

        // Replace file 'a' with directory 'a/' containing a file.
        File.Delete(Path_("a"));
        Directory.CreateDirectory(Path_("a"));
        Write(Path.Combine("a", "b"), "nested");
        repo.StageFile("a/b");

        // Committing must not produce an ambiguous tree with both 'a' (file) and 'a' (dir).
        Action act = () => repo.CreateCommit("dir replaces file", Sig);
        act.Should().NotThrow();
    }
}
