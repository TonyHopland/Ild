using k8s;
using ILD.Tests.Fakes;
using k8s.Autorest;
using k8s.Models;

namespace ILD.Tests;

/// <summary>The fake cluster keeps objects per namespace, as the API does, so a host that asks the wrong one finds nothing.</summary>
public sealed class FakeKubernetesApiNamespaceTests : IAsyncLifetime
{
    private const string Other = "elsewhere";
    private FakeKubernetesApi _api = null!;

    public async ValueTask InitializeAsync() => _api = await FakeKubernetesApi.StartAsync();

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task An_object_in_another_namespace_is_not_listed_read_or_deleted_through_this_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = Guid.NewGuid();
        _api.Seed("pods", "run-pod", "main", run, ns: Other);
        var client = _api.Client();

        var listed = await client.CoreV1.ListNamespacedPodAsync(FakeKubernetesApi.Namespace, labelSelector: "app.kubernetes.io/managed-by=ild", cancellationToken: ct);
        var elsewhere = await client.CoreV1.ListNamespacedPodAsync(Other, labelSelector: "app.kubernetes.io/managed-by=ild", cancellationToken: ct);
        var read = await Assert.ThrowsAsync<HttpOperationException>(() => client.CoreV1.ReadNamespacedPodAsync("run-pod", FakeKubernetesApi.Namespace, cancellationToken: ct));
        var delete = await Assert.ThrowsAsync<HttpOperationException>(() => client.CoreV1.DeleteNamespacedPodAsync("run-pod", FakeKubernetesApi.Namespace, cancellationToken: ct));

        Assert.Empty(listed.Items);
        Assert.Equal("run-pod", Assert.Single(elsewhere.Items).Metadata.Name);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, read.Response.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, delete.Response.StatusCode);
        Assert.NotNull(_api.Get<V1Pod>("pods", "run-pod", Other));
    }

    [Fact]
    public async Task The_same_name_can_be_created_in_two_namespaces_and_a_body_naming_another_namespace_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = _api.Client();
        V1Pod Pod(string? ns) => new() { Metadata = new V1ObjectMeta { Name = "twin", NamespaceProperty = ns } };

        await client.CoreV1.CreateNamespacedPodAsync(Pod(null), FakeKubernetesApi.Namespace, cancellationToken: ct);
        await client.CoreV1.CreateNamespacedPodAsync(Pod(Other), Other, cancellationToken: ct);
        var mismatch = await Assert.ThrowsAsync<HttpOperationException>(() => client.CoreV1.CreateNamespacedPodAsync(Pod(Other), FakeKubernetesApi.Namespace, cancellationToken: ct));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, mismatch.Response.StatusCode);
        Assert.Equal(["twin"], _api.Names("pods"));
        Assert.Equal(["twin"], _api.Names("pods", Other));
    }
}
