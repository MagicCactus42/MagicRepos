using System.CommandLine;
using MagicRepos.Core;
using MagicRepos.Core.Diff;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Remote;
using Spectre.Console;

// ─── Root command ──────────────────────────────────────────────────────────────

var rootCommand = new RootCommand("MagicRepos - A modern Git-like version control system");

// ─── init ──────────────────────────────────────────────────────────────────────

var initCommand = new Command("init") { Description = "Initialize a new MagicRepos repository" };
var initPathArg = new Argument<string>("path") { DefaultValueFactory = _ => "." };
initCommand.Arguments.Add(initPathArg);
initCommand.SetAction(parseResult =>
{
    try
    {
        var path = parseResult.GetValue(initPathArg);
        var fullPath = Path.GetFullPath(path!);
        Repository.Init(fullPath);
        AnsiConsole.MarkupLine($"[green]Initialized empty MagicRepos repository in {Markup.Escape(fullPath)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(initCommand);

// ─── add ───────────────────────────────────────────────────────────────────────

var addCommand = new Command("add") { Description = "Stage files for the next commit" };
var addPathArg = new Argument<string?>("pathspec") { DefaultValueFactory = _ => null };
var addAllOption = new Option<bool>("-A") { Description = "Stage all changes" };
addAllOption.Aliases.Add("--all");
addCommand.Arguments.Add(addPathArg);
addCommand.Options.Add(addAllOption);
addCommand.SetAction(parseResult =>
{
    try
    {
        var pathspec = parseResult.GetValue(addPathArg);
        var all = parseResult.GetValue(addAllOption);

        var repo = Repository.Open(Directory.GetCurrentDirectory());
        if (all)
        {
            repo.StageAll();
            AnsiConsole.MarkupLine("[green]Staged all files[/]");
        }
        else if (pathspec is not null)
        {
            repo.StageFile(pathspec);
            AnsiConsole.MarkupLine($"[green]Staged: {Markup.Escape(pathspec)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Error: Please specify a file path or use -A/--all to stage all changes[/]");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(addCommand);

// ─── commit ────────────────────────────────────────────────────────────────────

var commitCommand = new Command("commit") { Description = "Record changes to the repository" };
var commitMessageOption = new Option<string>("-m") { Description = "Commit message", Required = true };
commitMessageOption.Aliases.Add("--message");
var commitAuthorOption = new Option<string?>("--author") { Description = "Override author (format: Name <email>)" };
commitCommand.Options.Add(commitMessageOption);
commitCommand.Options.Add(commitAuthorOption);
commitCommand.SetAction(parseResult =>
{
    try
    {
        var message = parseResult.GetValue(commitMessageOption)!;
        var author = parseResult.GetValue(commitAuthorOption);

        var repo = Repository.Open(Directory.GetCurrentDirectory());

        Signature? authorSig = null;
        if (author is not null)
        {
            authorSig = ParseSignature(author);
        }

        var commitId = repo.CreateCommit(message, authorSig);
        var shortHash = commitId.ToHexString()[..7];
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(shortHash)}[/] {Markup.Escape(message)}");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(commitCommand);

// ─── status ────────────────────────────────────────────────────────────────────

var statusCommand = new Command("status") { Description = "Show the working tree status" };
statusCommand.SetAction(parseResult =>
{
    try
    {
        var repo = Repository.Open(Directory.GetCurrentDirectory());
        var branchName = repo.Refs.GetCurrentBranchName() ?? "HEAD (detached)";
        AnsiConsole.MarkupLine($"On branch [cyan]{Markup.Escape(branchName)}[/]");
        AnsiConsole.WriteLine();

        var status = repo.GetStatus();

        if (status.StagedChanges.Count > 0)
        {
            AnsiConsole.MarkupLine("[green bold]Changes to be committed:[/]");
            foreach (var change in status.StagedChanges)
            {
                var label = change.Status switch
                {
                    FileStatusType.Added => "new file",
                    FileStatusType.Modified => "modified",
                    FileStatusType.Deleted => "deleted",
                    _ => change.Status.ToString().ToLowerInvariant()
                };
                AnsiConsole.MarkupLine($"[green]        {label}:   {Markup.Escape(change.Path)}[/]");
            }
            AnsiConsole.WriteLine();
        }

        if (status.UnstagedChanges.Count > 0)
        {
            AnsiConsole.MarkupLine("[red bold]Changes not staged for commit:[/]");
            foreach (var change in status.UnstagedChanges)
            {
                var label = change.Status switch
                {
                    FileStatusType.Modified => "modified",
                    FileStatusType.Deleted => "deleted",
                    _ => change.Status.ToString().ToLowerInvariant()
                };
                AnsiConsole.MarkupLine($"[red]        {label}:   {Markup.Escape(change.Path)}[/]");
            }
            AnsiConsole.WriteLine();
        }

        if (status.UntrackedFiles.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey bold]Untracked files:[/]");
            foreach (var file in status.UntrackedFiles)
            {
                AnsiConsole.MarkupLine($"[grey]        {Markup.Escape(file)}[/]");
            }
            AnsiConsole.WriteLine();
        }

        if (status.StagedChanges.Count == 0 && status.UnstagedChanges.Count == 0 && status.UntrackedFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("nothing to commit, working tree clean");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(statusCommand);

// ─── log ───────────────────────────────────────────────────────────────────────

var logCommand = new Command("log") { Description = "Show commit logs" };
var logMaxCountOption = new Option<int>("-n") { Description = "Maximum number of commits to show", DefaultValueFactory = _ => 10 };
logMaxCountOption.Aliases.Add("--max-count");
logCommand.Options.Add(logMaxCountOption);
logCommand.SetAction(parseResult =>
{
    try
    {
        var maxCount = parseResult.GetValue(logMaxCountOption);

        var repo = Repository.Open(Directory.GetCurrentDirectory());
        var commits = repo.GetLog(maxCount);

        foreach (var commit in commits)
        {
            var shortHash = commit.Id.ToString()[..7];
            var date = commit.Author.When.ToString("yyyy-MM-dd HH:mm:ss");
            var authorDisplay = $"{commit.Author.Name} <{commit.Author.Email}>";
            var firstLine = commit.Message.Split('\n', StringSplitOptions.None)[0];

            AnsiConsole.MarkupLine(
                $"[yellow]{Markup.Escape(shortHash)}[/] - {Markup.Escape(firstLine)} " +
                $"[grey]({Markup.Escape(date)})[/] [blue]<{Markup.Escape(commit.Author.Name)}>[/]");
        }

        if (!commits.Any())
        {
            AnsiConsole.MarkupLine("[grey]No commits yet.[/]");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(logCommand);

// ─── diff ──────────────────────────────────────────────────────────────────────

var diffCommand = new Command("diff") { Description = "Show changes between commits, commit and working tree, etc." };
var diffStagedOption = new Option<bool>("--staged") { Description = "Show staged changes (index vs HEAD)" };
diffCommand.Options.Add(diffStagedOption);
diffCommand.SetAction(parseResult =>
{
    try
    {
        var staged = parseResult.GetValue(diffStagedOption);

        var repo = Repository.Open(Directory.GetCurrentDirectory());
        var results = staged ? repo.DiffIndex() : repo.DiffWorkingTree();

        var hasAnyChanges = false;
        foreach (var result in results)
        {
            if (!result.HasChanges)
                continue;

            hasAnyChanges = true;

            AnsiConsole.MarkupLine($"[bold]diff --magicrepos a/{Markup.Escape(result.OldPath)} b/{Markup.Escape(result.NewPath)}[/]");
            AnsiConsole.MarkupLine($"[bold]--- a/{Markup.Escape(result.OldPath)}[/]");
            AnsiConsole.MarkupLine($"[bold]+++ b/{Markup.Escape(result.NewPath)}[/]");

            foreach (var hunk in result.Hunks)
            {
                AnsiConsole.MarkupLine(
                    $"[cyan]@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@[/]");

                foreach (var line in hunk.Lines)
                {
                    switch (line.Type)
                    {
                        case DiffLineType.Added:
                            AnsiConsole.MarkupLine($"[green]+{Markup.Escape(line.Content)}[/]");
                            break;
                        case DiffLineType.Removed:
                            AnsiConsole.MarkupLine($"[red]-{Markup.Escape(line.Content)}[/]");
                            break;
                        case DiffLineType.Context:
                            AnsiConsole.MarkupLine($" {Markup.Escape(line.Content)}");
                            break;
                    }
                }
            }

            AnsiConsole.WriteLine();
        }

        if (!hasAnyChanges)
        {
            AnsiConsole.MarkupLine("[grey]No changes.[/]");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(diffCommand);

// ─── branch ────────────────────────────────────────────────────────────────────

var branchCommand = new Command("branch") { Description = "List, create, or delete branches" };
var branchNameArg = new Argument<string?>("name") { DefaultValueFactory = _ => null };
var branchListOption = new Option<bool>("-l") { Description = "List all branches" };
branchListOption.Aliases.Add("--list");
var branchDeleteOption = new Option<string?>("-d") { Description = "Delete a branch" };
branchDeleteOption.Aliases.Add("--delete");
branchCommand.Arguments.Add(branchNameArg);
branchCommand.Options.Add(branchListOption);
branchCommand.Options.Add(branchDeleteOption);
branchCommand.SetAction(parseResult =>
{
    try
    {
        var name = parseResult.GetValue(branchNameArg);
        var list = parseResult.GetValue(branchListOption);
        var delete = parseResult.GetValue(branchDeleteOption);

        var repo = Repository.Open(Directory.GetCurrentDirectory());

        if (delete is not null)
        {
            repo.DeleteBranch(delete);
            AnsiConsole.MarkupLine($"[green]Deleted branch {Markup.Escape(delete)}[/]");
        }
        else if (name is not null && !list)
        {
            repo.CreateBranch(name);
            AnsiConsole.MarkupLine($"[green]Created branch {Markup.Escape(name)}[/]");
        }
        else
        {
            // Default behavior: list branches
            var currentBranch = repo.Refs.GetCurrentBranchName();
            var branches = repo.ListBranches();

            foreach (var branch in branches)
            {
                if (branch == currentBranch)
                {
                    AnsiConsole.MarkupLine($"[green]* {Markup.Escape(branch)}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  {Markup.Escape(branch)}");
                }
            }

            if (!branches.Any())
            {
                AnsiConsole.MarkupLine("[grey]No branches found.[/]");
            }
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(branchCommand);

// ─── checkout ──────────────────────────────────────────────────────────────────

var checkoutCommand = new Command("checkout") { Description = "Switch branches" };
var checkoutBranchArg = new Argument<string>("branch");
checkoutCommand.Arguments.Add(checkoutBranchArg);
checkoutCommand.SetAction(parseResult =>
{
    try
    {
        var branch = parseResult.GetValue(checkoutBranchArg)!;

        var repo = Repository.Open(Directory.GetCurrentDirectory());
        repo.CheckoutBranch(branch);
        AnsiConsole.MarkupLine($"Switched to branch [cyan]{Markup.Escape(branch)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(checkoutCommand);

// ─── reset ─────────────────────────────────────────────────────────────────────

var resetCommand = new Command("reset") { Description = "Reset current HEAD to the specified state" };
var resetTargetArg = new Argument<string>("target");
var resetHardOption = new Option<bool>("--hard") { Description = "Reset working tree and index" };
var resetSoftOption = new Option<bool>("--soft") { Description = "Keep changes staged" };
var resetMixedOption = new Option<bool>("--mixed") { Description = "Reset index but keep working tree (default)" };
resetCommand.Arguments.Add(resetTargetArg);
resetCommand.Options.Add(resetHardOption);
resetCommand.Options.Add(resetSoftOption);
resetCommand.Options.Add(resetMixedOption);
resetCommand.SetAction(parseResult =>
{
    try
    {
        var target = parseResult.GetValue(resetTargetArg)!;
        var hard = parseResult.GetValue(resetHardOption);
        var soft = parseResult.GetValue(resetSoftOption);

        var repo = Repository.Open(Directory.GetCurrentDirectory());

        var mode = ResetMode.Mixed; // default
        if (hard) mode = ResetMode.Hard;
        else if (soft) mode = ResetMode.Soft;

        repo.Reset(target, mode);
        AnsiConsole.MarkupLine($"[green]HEAD is now at {Markup.Escape(target)} (mode: {mode})[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(resetCommand);

// ─── remote ────────────────────────────────────────────────────────────────────

var remoteCommand = new Command("remote") { Description = "Manage set of tracked repositories" };

var remoteAddCommand = new Command("add") { Description = "Add a new remote" };
var remoteAddNameArg = new Argument<string>("name");
var remoteAddUrlArg = new Argument<string>("url");
remoteAddCommand.Arguments.Add(remoteAddNameArg);
remoteAddCommand.Arguments.Add(remoteAddUrlArg);
remoteAddCommand.SetAction(parseResult =>
{
    try
    {
        var name = parseResult.GetValue(remoteAddNameArg)!;
        var url = parseResult.GetValue(remoteAddUrlArg)!;

        var repo = Repository.Open(Directory.GetCurrentDirectory());
        repo.Config.AddRemote(name, url);
        repo.Config.Save();
        AnsiConsole.MarkupLine($"[green]Added remote {Markup.Escape(name)} -> {Markup.Escape(url)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
remoteCommand.Subcommands.Add(remoteAddCommand);

var remoteListCommand = new Command("list") { Description = "List all remotes" };
remoteListCommand.SetAction(parseResult =>
{
    try
    {
        var repo = Repository.Open(Directory.GetCurrentDirectory());
        var remotes = repo.Config.ListRemotes();

        foreach (var remote in remotes)
        {
            var url = repo.Config.GetRemoteUrl(remote);
            AnsiConsole.MarkupLine($"{Markup.Escape(remote)}\t{Markup.Escape(url ?? "(no url)")}");
        }

        if (!remotes.Any())
        {
            AnsiConsole.MarkupLine("[grey]No remotes configured.[/]");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
remoteCommand.Subcommands.Add(remoteListCommand);

rootCommand.Subcommands.Add(remoteCommand);

// ─── push ──────────────────────────────────────────────────────────────────────

var pushCommand = new Command("push") { Description = "Update remote refs along with associated objects" };
var pushRemoteArg = new Argument<string>("remote") { DefaultValueFactory = _ => "origin" };
pushCommand.Arguments.Add(pushRemoteArg);
pushCommand.SetAction(parseResult =>
{
    try
    {
        var remoteName = parseResult.GetValue(pushRemoteArg)!;
        var repo = Repository.Open(Directory.GetCurrentDirectory());
        var remoteUrlStr = repo.Config.GetRemoteUrl(remoteName);
        if (remoteUrlStr is null)
            throw new InvalidOperationException($"Remote '{remoteName}' not configured. Use 'magicrepos remote add {remoteName} <url>'.");

        var remoteUrl = RemoteUrl.Parse(remoteUrlStr);
        AnsiConsole.MarkupLine($"Pushing to [cyan]{Markup.Escape(remoteName)}[/] ({Markup.Escape(remoteUrlStr)})...");

        using var client = new RemoteClient();
        client.Connect(remoteUrl);
        client.PushAsync(repo.ObjectStore, repo.Refs, remoteUrl).GetAwaiter().GetResult();

        AnsiConsole.MarkupLine("[green]Push completed successfully.[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(pushCommand);

// ─── pull ──────────────────────────────────────────────────────────────────────

var pullCommand = new Command("pull") { Description = "Fetch from and integrate with another repository" };
var pullRemoteArg = new Argument<string>("remote") { DefaultValueFactory = _ => "origin" };
pullCommand.Arguments.Add(pullRemoteArg);
pullCommand.SetAction(parseResult =>
{
    try
    {
        var remoteName = parseResult.GetValue(pullRemoteArg)!;
        var repo = Repository.Open(Directory.GetCurrentDirectory());
        var remoteUrlStr = repo.Config.GetRemoteUrl(remoteName);
        if (remoteUrlStr is null)
            throw new InvalidOperationException($"Remote '{remoteName}' not configured. Use 'magicrepos remote add {remoteName} <url>'.");

        var remoteUrl = RemoteUrl.Parse(remoteUrlStr);
        AnsiConsole.MarkupLine($"Pulling from [cyan]{Markup.Escape(remoteName)}[/] ({Markup.Escape(remoteUrlStr)})...");

        using var client = new RemoteClient();
        client.Connect(remoteUrl);
        var remoteRefs = client.PullAsync(repo.ObjectStore, repo.Refs, remoteUrl, remoteName).GetAwaiter().GetResult();

        // Fast-forward local branches to match remote-tracking refs
        var currentBranch = repo.Refs.GetCurrentBranchName();
        foreach (var (refName, objectId) in remoteRefs)
        {
            if (!refName.StartsWith("refs/heads/")) continue;
            var branch = refName["refs/heads/".Length..];
            if (branch == currentBranch)
            {
                // Fast-forward current branch and update working tree
                repo.Refs.CreateBranch(branch, objectId);
                repo.Reset("HEAD", ResetMode.Hard);
                AnsiConsole.MarkupLine($"[green]Updated branch {Markup.Escape(branch)} (fast-forward)[/]");
            }
            else
            {
                // Update non-current local branch if it exists
                var localId = repo.Refs.ResolveBranch(branch);
                if (localId is null)
                {
                    repo.Refs.CreateBranch(branch, objectId);
                    AnsiConsole.MarkupLine($"[green]Created branch {Markup.Escape(branch)}[/]");
                }
            }
        }

        AnsiConsole.MarkupLine("[green]Pull completed successfully.[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(pullCommand);

// ─── clone ─────────────────────────────────────────────────────────────────────

var cloneCommand = new Command("clone") { Description = "Clone a repository into a new directory" };
var cloneUrlArg = new Argument<string>("url");
var cloneDirArg = new Argument<string?>("directory") { DefaultValueFactory = _ => null };
cloneCommand.Arguments.Add(cloneUrlArg);
cloneCommand.Arguments.Add(cloneDirArg);
cloneCommand.SetAction(parseResult =>
{
    try
    {
        var url = parseResult.GetValue(cloneUrlArg)!;
        var dir = parseResult.GetValue(cloneDirArg);

        AnsiConsole.MarkupLine($"Cloning from [cyan]{Markup.Escape(url)}[/]...");
        var repo = RemoteClient.CloneAsync(url, dir).GetAwaiter().GetResult();
        AnsiConsole.MarkupLine($"[green]Cloned into {Markup.Escape(repo.WorkingDirectory)}[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    }
});
rootCommand.Subcommands.Add(cloneCommand);

// ─── pr ────────────────────────────────────────────────────────────────────────

var prCommand = new Command("pr") { Description = "Manage pull requests" };

var prCreateCommand = new Command("create") { Description = "Create a new pull request" };
var prCreateTitleOption = new Option<string>("-t") { Description = "Title of the pull request", Required = true };
prCreateTitleOption.Aliases.Add("--title");
var prCreateBodyOption = new Option<string?>("-b") { Description = "Body of the pull request" };
prCreateBodyOption.Aliases.Add("--body");
prCreateCommand.Options.Add(prCreateTitleOption);
prCreateCommand.Options.Add(prCreateBodyOption);
prCreateCommand.SetAction(parseResult =>
{
    var title = parseResult.GetValue(prCreateTitleOption)!;
    var body = parseResult.GetValue(prCreateBodyOption);
    AnsiConsole.MarkupLine($"[yellow]PR create not yet implemented. Title: {Markup.Escape(title)}[/]");
});
prCommand.Subcommands.Add(prCreateCommand);

var prListCommand = new Command("list") { Description = "List pull requests" };
prListCommand.SetAction(parseResult =>
{
    AnsiConsole.MarkupLine("[yellow]PR list not yet implemented.[/]");
});
prCommand.Subcommands.Add(prListCommand);

var prReviewCommand = new Command("review") { Description = "Review a pull request" };
var prReviewIdArg = new Argument<string>("id");
prReviewCommand.Arguments.Add(prReviewIdArg);
prReviewCommand.SetAction(parseResult =>
{
    var id = parseResult.GetValue(prReviewIdArg)!;
    AnsiConsole.MarkupLine($"[yellow]PR review not yet implemented. PR: {Markup.Escape(id)}[/]");
});
prCommand.Subcommands.Add(prReviewCommand);

var prMergeCommand = new Command("merge") { Description = "Merge a pull request" };
var prMergeIdArg = new Argument<string>("id");
prMergeCommand.Arguments.Add(prMergeIdArg);
prMergeCommand.SetAction(parseResult =>
{
    var id = parseResult.GetValue(prMergeIdArg)!;
    AnsiConsole.MarkupLine($"[yellow]PR merge not yet implemented. PR: {Markup.Escape(id)}[/]");
});
prCommand.Subcommands.Add(prMergeCommand);

rootCommand.Subcommands.Add(prCommand);

// ─── Run ───────────────────────────────────────────────────────────────────────

return rootCommand.Parse(args).Invoke();

// ─── Helpers ───────────────────────────────────────────────────────────────────

static Signature ParseSignature(string authorString)
{
    // Expected format: "Name <email>"
    var angleBracketStart = authorString.IndexOf('<');
    var angleBracketEnd = authorString.IndexOf('>');

    if (angleBracketStart < 0 || angleBracketEnd < 0 || angleBracketEnd <= angleBracketStart)
    {
        throw new FormatException("Author must be in the format: Name <email>");
    }

    var name = authorString[..angleBracketStart].Trim();
    var email = authorString[(angleBracketStart + 1)..angleBracketEnd].Trim();

    return new Signature(name, email, DateTimeOffset.Now);
}
