using System.Text;
using FluentAssertions;
using MagicRepos.Core;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Tests;

public class RepositoryIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "magicrepos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string RepoDir => _tempDir;

    private static Signature TestSignature => new("Test User", "test@example.com",
        new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Init_creates_magicrepos_directory()
    {
        // Act
        Repository repo = Repository.Init(RepoDir);

        // Assert
        Directory.Exists(Path.Combine(RepoDir, ".magicrepos")).Should().BeTrue();
        Directory.Exists(Path.Combine(RepoDir, ".magicrepos", "objects")).Should().BeTrue();
        Directory.Exists(Path.Combine(RepoDir, ".magicrepos", "refs", "heads")).Should().BeTrue();
        File.Exists(Path.Combine(RepoDir, ".magicrepos", "HEAD")).Should().BeTrue();

        string head = File.ReadAllText(Path.Combine(RepoDir, ".magicrepos", "HEAD")).Trim();
        head.Should().Be("ref: refs/heads/main");
    }

    [Fact]
    public void Init_throws_if_already_initialized()
    {
        // Arrange
        Repository.Init(RepoDir);

        // Act & Assert
        Action act = () => Repository.Init(RepoDir);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Open_finds_existing_repository()
    {
        // Arrange
        Repository.Init(RepoDir);

        // Act
        Repository repo = Repository.Open(RepoDir);

        // Assert
        repo.WorkingDirectory.Should().Be(Path.GetFullPath(RepoDir));
    }

    [Fact]
    public void Open_throws_if_no_repository()
    {
        // Arrange — empty directory with no .magicrepos

        // Act & Assert
        Action act = () => Repository.Open(RepoDir);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Stage_and_commit_creates_objects()
    {
        // Arrange
        Repository repo = Repository.Init(RepoDir);
        string filePath = Path.Combine(RepoDir, "hello.txt");
        File.WriteAllText(filePath, "Hello, World!");

        // Act
        repo.StageFile("hello.txt");
        ObjectId commitId = repo.CreateCommit("first commit", TestSignature);

        // Assert
        commitId.Should().NotBe(ObjectId.Zero);

        // The commit should be retrievable via ReadCommit
        CommitObject commit = repo.ReadCommit(commitId);
        commit.Message.Should().Be("first commit");
        commit.Parents.Should().BeEmpty();
        commit.Author.Name.Should().Be("Test User");
    }

    [Fact]
    public void Status_shows_correct_changes()
    {
        // Arrange
        Repository repo = Repository.Init(RepoDir);
        string filePath = Path.Combine(RepoDir, "file.txt");
        File.WriteAllText(filePath, "initial content");

        // Act — check status before staging
        RepositoryStatus statusBeforeStage = repo.GetStatus();

        // Assert — file should appear as untracked
        statusBeforeStage.UntrackedFiles.Should().Contain("file.txt");

        // Act — stage and commit
        repo.StageFile("file.txt");
        repo.CreateCommit("initial", TestSignature);

        // Modify the file
        File.WriteAllText(filePath, "modified content");
        RepositoryStatus statusAfterModify = repo.GetStatus();

        // Assert — file should appear as modified (unstaged)
        statusAfterModify.UnstagedChanges.Should().Contain(
            f => f.Path == "file.txt" && f.Status == FileStatusType.Modified);
    }

    [Fact]
    public void Create_and_checkout_branch_works()
    {
        // Arrange — create repo with an initial commit
        Repository repo = Repository.Init(RepoDir);
        string filePath = Path.Combine(RepoDir, "main-file.txt");
        File.WriteAllText(filePath, "main content");
        repo.StageFile("main-file.txt");
        repo.CreateCommit("main commit", TestSignature);

        // Act — create a new branch
        repo.CreateBranch("feature");

        // Assert
        repo.ListBranches().Should().Contain("feature");
        repo.ListBranches().Should().Contain("main");

        // Act — checkout the new branch
        repo.CheckoutBranch("feature");

        // Assert — HEAD should point to the new branch
        repo.Refs.GetCurrentBranchName().Should().Be("feature");

        // Working tree file should still exist
        File.Exists(filePath).Should().BeTrue();
        File.ReadAllText(filePath).Should().Be("main content");
    }

    [Fact]
    public void Log_returns_commits_in_order()
    {
        // Arrange
        Repository repo = Repository.Init(RepoDir);

        // Create first commit
        string file1 = Path.Combine(RepoDir, "file1.txt");
        File.WriteAllText(file1, "first");
        repo.StageFile("file1.txt");
        ObjectId firstId = repo.CreateCommit("first commit",
            new Signature("Test", "test@example.com",
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        // Create second commit
        string file2 = Path.Combine(RepoDir, "file2.txt");
        File.WriteAllText(file2, "second");
        repo.StageFile("file2.txt");
        ObjectId secondId = repo.CreateCommit("second commit",
            new Signature("Test", "test@example.com",
                new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero)));

        // Create third commit
        string file3 = Path.Combine(RepoDir, "file3.txt");
        File.WriteAllText(file3, "third");
        repo.StageFile("file3.txt");
        ObjectId thirdId = repo.CreateCommit("third commit",
            new Signature("Test", "test@example.com",
                new DateTimeOffset(2025, 1, 3, 0, 0, 0, TimeSpan.Zero)));

        // Act
        IReadOnlyList<CommitObject> log = repo.GetLog();

        // Assert — most recent first
        log.Should().HaveCount(3);
        log[0].Message.Should().Be("third commit");
        log[1].Message.Should().Be("second commit");
        log[2].Message.Should().Be("first commit");

        // Each commit (except root) should have exactly one parent
        log[0].Parents.Should().ContainSingle().Which.Should().Be(secondId);
        log[1].Parents.Should().ContainSingle().Which.Should().Be(firstId);
        log[2].Parents.Should().BeEmpty();
    }

    [Fact]
    public void GetLog_with_maxCount_limits_results()
    {
        // Arrange
        Repository repo = Repository.Init(RepoDir);

        for (int i = 0; i < 5; i++)
        {
            string file = Path.Combine(RepoDir, $"file{i}.txt");
            File.WriteAllText(file, $"content {i}");
            repo.StageFile($"file{i}.txt");
            repo.CreateCommit($"commit {i}",
                new Signature("Test", "test@example.com",
                    new DateTimeOffset(2025, 1, i + 1, 0, 0, 0, TimeSpan.Zero)));
        }

        // Act
        IReadOnlyList<CommitObject> log = repo.GetLog(maxCount: 2);

        // Assert
        log.Should().HaveCount(2);
        log[0].Message.Should().Be("commit 4");
        log[1].Message.Should().Be("commit 3");
    }

    [Fact]
    public void StageAll_stages_all_non_ignored_files()
    {
        // Arrange
        Repository repo = Repository.Init(RepoDir);
        File.WriteAllText(Path.Combine(RepoDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(RepoDir, "b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(RepoDir, "sub"));
        File.WriteAllText(Path.Combine(RepoDir, "sub", "c.txt"), "c");

        // Act
        repo.StageAll();
        ObjectId commitId = repo.CreateCommit("all staged", TestSignature);

        // Assert
        CommitObject commit = repo.ReadCommit(commitId);
        commit.Message.Should().Be("all staged");

        // After committing, status should show no staged or unstaged changes
        RepositoryStatus status = repo.GetStatus();
        status.StagedChanges.Should().BeEmpty();
        status.UntrackedFiles.Should().BeEmpty();
    }

    [Fact]
    public void IsRepository_returns_correct_result()
    {
        // Before init
        Repository.IsRepository(RepoDir).Should().BeFalse();

        // After init
        Repository.Init(RepoDir);
        Repository.IsRepository(RepoDir).Should().BeTrue();
    }
}
