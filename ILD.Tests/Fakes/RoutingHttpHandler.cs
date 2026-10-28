using System.Net;
using System.Text;

namespace ILD.Tests.Fakes;

/// <summary>
/// Answers forge calls with canned JSON by URL. <see cref="MapUrl"/> rules are checked first, then
/// <see cref="MapMethod"/> rules (latest first), then <see cref="Map"/> rules in declared order, so a
/// specific path matches before a broader one. Anything unmatched is a 404.
/// </summary>
internal sealed class RoutingHttpHandler : HttpMessageHandler
{
    private readonly List<(Func<string, bool> Match, Func<string, HttpResponseMessage> Respond)> _urlRules = new();
    private readonly List<(HttpMethod? Method, Func<string, bool> Match, Func<string> Body)> _rules = new();

    public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = new();

    public IEnumerable<string> Urls => Calls.Select(c => c.Url);

    public IEnumerable<(HttpMethod Method, string Url, string Body)> Bodies => Calls.Where(c => c.Body.Length > 0);

    public IEnumerable<string> GraphQlBodies
        => Bodies.Where(c => c.Url.EndsWith("/graphql", StringComparison.Ordinal)).Select(c => c.Body);

    public RoutingHttpHandler Map(Func<string, bool> match, Func<string> body)
    {
        _rules.Add((null, match, body));
        return this;
    }

    /// <summary>A list GET and a create POST share a URL on both forges, so the method has to be part of the match.</summary>
    public RoutingHttpHandler MapMethod(HttpMethod method, Func<string, bool> match, Func<string> body)
    {
        _rules.Insert(0, (method, match, body));
        return this;
    }

    /// <summary>A rule that can see the URL, so a test can answer page 1 and page 2 differently.</summary>
    public RoutingHttpHandler MapUrl(Func<string, bool> match, Func<string, HttpResponseMessage> respond)
    {
        _urlRules.Add((match, respond));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var sent = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.Method, url, sent));

        foreach (var (match, respond) in _urlRules)
            if (match(url))
                return respond(url);

        foreach (var (method, match, body) in _rules)
            if ((method is null || method == request.Method) && match(url))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body(), Encoding.UTF8, "application/json"),
                };

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
