using ILD.Core.Services.Implementations.RemoteProviders;

namespace ILD.Tests;

/// <summary>
/// The forge web link for a run branch, built from the repository's clone URL.
/// The clone URL often carries credentials, so the link is assembled from the
/// URL's parts rather than by editing the string, and anything the forge type
/// or the clone URL cannot pin exactly yields no link at all.
/// </summary>
public class BranchWebUrlTests
{
    [Theory]
    [InlineData("Forgejo", "https://user:secret@git.example.com/acme/app.git", "ild/wi-1-run-2",
        "https://git.example.com/acme/app/src/branch/ild/wi-1-run-2")]
    [InlineData("GitHub", "https://github.example.com/acme/app.git", "ild/wi-1-run-2",
        "https://github.example.com/acme/app/tree/ild/wi-1-run-2")]
    [InlineData("AzureDevOps", "https://acme@dev.example.com/acme/proj/_git/app", "ild/wi-1-run-2",
        "https://dev.example.com/acme/proj/_git/app?version=GBild%2Fwi-1-run-2")]
    [InlineData("GitHub", "https://x-access-token:ghp_token123@github.example.com/acme/app.git", "main",
        "https://github.example.com/acme/app/tree/main")]
    [InlineData("github", "https://github.example.com/acme/app.git", "main",
        "https://github.example.com/acme/app/tree/main")]
    [InlineData("FORGEJO", "https://git.example.com/acme/app.git", "main",
        "https://git.example.com/acme/app/src/branch/main")]
    [InlineData("azuredevops", "https://dev.example.com/acme/proj/_git/app", "main",
        "https://dev.example.com/acme/proj/_git/app?version=GBmain")]
    [InlineData("Forgejo", "https://git.example.com/acme/app/", "main",
        "https://git.example.com/acme/app/src/branch/main")]
    [InlineData("Forgejo", "https://git.example.com/acme/app.git/", "main",
        "https://git.example.com/acme/app/src/branch/main")]
    [InlineData("GitHub", "https://github.example.com/acme/App.GIT", "main",
        "https://github.example.com/acme/App/tree/main")]
    [InlineData("Forgejo", "http://git.example.com:3000/acme/app.git", "main",
        "http://git.example.com:3000/acme/app/src/branch/main")]
    [InlineData("GitHub", "https://github.example.com/acme/app.git?ref=x#frag", "main",
        "https://github.example.com/acme/app/tree/main")]
    [InlineData("GitHub", "https://github.example.com/acme/app.git", "feat/a b#c?d",
        "https://github.example.com/acme/app/tree/feat/a%20b%23c%3Fd")]
    [InlineData("Forgejo", "https://git.example.com/acme/app.git", "feat/a b#c",
        "https://git.example.com/acme/app/src/branch/feat/a%20b%23c")]
    [InlineData("AzureDevOps", "https://dev.example.com/acme/proj/_git/app", "feat/a b&c#d",
        "https://dev.example.com/acme/proj/_git/app?version=GBfeat%2Fa%20b%26c%23d")]
    public void Builds_the_forge_branch_link(string type, string cloneUrl, string branch, string expected)
    {
        Assert.Equal(expected, BranchWebUrl.For(type, cloneUrl, branch));
    }

    [Theory]
    [InlineData("https://user:s3cr3t-pw@git.example.com/acme/app.git")]
    [InlineData("https://ghp_s3cr3t-pw@git.example.com/acme/app.git")]
    [InlineData("https://oauth2:s3cr3t-pw@git.example.com:8443/acme/app.git")]
    public void Never_carries_credentials_from_the_clone_url(string cloneUrl)
    {
        foreach (var type in new[] { "GitHub", "Forgejo", "AzureDevOps" })
        {
            var url = BranchWebUrl.For(type, cloneUrl, "ild/wi-1-run-2");

            Assert.NotNull(url);
            Assert.DoesNotContain("s3cr3t-pw", url);
            Assert.DoesNotContain("@", url);
            Assert.StartsWith("https://git.example.com", url);
        }
    }

    [Theory]
    [InlineData("Forgejo", "ssh://git@git.example.com/acme/app.git", "main")]
    [InlineData("Forgejo", "git@git.example.com:acme/app.git", "main")]
    [InlineData("GitHub", "file:///srv/git/app.git", "main")]
    [InlineData("GitHub", "acme/app", "main")]
    [InlineData("GitHub", "", "main")]
    [InlineData("GitLab", "https://gitlab.example.com/acme/app.git", "main")]
    [InlineData("", "https://git.example.com/acme/app.git", "main")]
    [InlineData("Forgejo", "https://git.example.com/acme/app.git", "")]
    [InlineData("Forgejo", "https://git.example.com/acme/app.git", "   ")]
    public void Gives_no_link_when_it_cannot_be_exact(string type, string cloneUrl, string branch)
    {
        Assert.Null(BranchWebUrl.For(type, cloneUrl, branch));
    }
}
