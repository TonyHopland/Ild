using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class RedactUserInfoTests
{
    [Theory]
    [InlineData("fatal: unable to access 'https://u:secret@host/r.git/': 403", "fatal: unable to access 'https://host/r.git/': 403")]
    [InlineData("https://tok@host/a and ssh://git:pw@h2/b", "https://host/a and ssh://h2/b")]
    [InlineData("nothing to hide at user@host", "nothing to hide at user@host")]
    [InlineData("https://host/path@thing", "https://host/path@thing")]
    public void Removes_credentials_from_every_url_in_text(string input, string expected)
        => Assert.Equal(expected, RepositoryManager.RedactUserInfo(input));
}
