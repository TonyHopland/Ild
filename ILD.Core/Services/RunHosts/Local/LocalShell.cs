using System.Diagnostics;
using System.Text;

namespace ILD.Core.Services.RunHosts.Local;

/// <summary>Starts <c>/bin/sh</c> on this machine for a script; the only place file helpers outside the local host spawn from.</summary>
internal static class LocalShell
{
    /// <summary>Starts <c>/bin/sh -c <paramref name="script"/></c> with <paramref name="arguments"/> as <c>$1…</c>, all three standard streams redirected.</summary>
    public static Process Start(string script, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            WorkingDirectory = "/",
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("ild-agent-files");
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        return Process.Start(psi) ?? throw new IOException("could not start /bin/sh");
    }
}
