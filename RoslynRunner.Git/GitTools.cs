using LibGit2Sharp;
using System.Diagnostics;

namespace RoslynRunner.Git;

public static class GitTools
{
    public static void CaptureDiff(string repoPath, string patchFile)
    {
        using (var repo = new Repository(repoPath))
        {
            var diff = repo.Diff.Compare<Patch>();
            repo.Reset(ResetMode.Hard);
            if (string.IsNullOrWhiteSpace(patchFile))
                Console.WriteLine(diff.Content);
            else
                File.WriteAllText(patchFile, diff.Content);
        }
    }

    public static void CommitToBranch(string repoPath, string baseBranch, string newBranch, string commitMessage)
    {
        using (var repo = new Repository(repoPath))
        {
            if (repo.Branches.Any(b => b.FriendlyName == newBranch)) repo.Branches.Remove(newBranch);

            var branch = repo.CreateBranch(newBranch, repo.Branches[baseBranch].Tip);
            Commands.Checkout(repo, branch);
            Commands.Stage(repo, "*");
            var config = repo.Config;
            var signature = config.BuildSignature(DateTimeOffset.Now);
            repo.Commit(commitMessage, signature, signature);

            Commands.Checkout(repo, baseBranch);
        }
    }

    public static void PushBranchUnsupported(string repoPath, string branchName, bool forcePush = false,
        string remoteName = "origin")
    {
        using var repo = new Repository(repoPath);
        var remote = repo.Network.Remotes[remoteName];
        var forcePushString = forcePush ? "+" : string.Empty;
        repo.Network.Push(remote, $"{forcePushString}refs/heads/{branchName}");
    }

    public static void PushBranch(string repoPath, string branchName, bool forcePush = false,
        string remoteName = "origin")
    {
        var forcePushString = forcePush ? "-f" : string.Empty;
        var command = $"push {remoteName} {forcePushString} {branchName}";

        var p = new Process();
        p.StartInfo.FileName = "git";
        p.StartInfo.WorkingDirectory = repoPath;
        p.StartInfo.Arguments = command;
        p.Start();
        p.WaitForExit();
    }

    public static bool StopForUnfinishedChanges(string repoPath, bool prompt = false)
    {
        using var repo = new Repository(repoPath);
        if (repo.Diff.Compare<TreeChanges>().Count > 0)
        {
            if (prompt)
                while (true)
                {
                    Console.WriteLine("There are uncommited changes, abort? (y/n)");
                    var response = Console.ReadLine();
                    if (response == "y") return true;
                    if (response == "n") return false;
                }

            return true;
        }

        return false;
    }

    public static void Reset(string repoPath)
    {
        using (var repo = new Repository(repoPath))
        {
            repo.Reset(ResetMode.Hard);
        }
    }
}
