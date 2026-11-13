using ILD.Core.Services.RunHosts.Kubernetes;
using k8s;
using k8s.Models;

namespace ILD.Tests;

/// <summary>The manifests shipped in <c>docs/kubernetes</c>, and the startup checks of the Kubernetes run host's settings.</summary>
public sealed class KubernetesManifestsDocsTests
{
    private static string Docs(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ILD.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, "docs", "kubernetes", file));
        throw new InvalidOperationException($"no ILD.sln above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void The_default_deny_policy_selects_every_pod_ild_makes_and_allows_nothing()
    {
        var policy = Assert.IsType<V1NetworkPolicy>(Assert.Single(KubernetesYaml.LoadAllFromString(Docs("default-deny-networkpolicy.yaml"))));

        Assert.Equal("ild-default-deny", policy.Metadata.Name);
        Assert.Equal("ild-runs", policy.Metadata.NamespaceProperty);
        Assert.Equal(new Dictionary<string, string> { ["app.kubernetes.io/managed-by"] = "ild" }, policy.Spec.PodSelector.MatchLabels);
        Assert.Empty(policy.Spec.PodSelector.MatchExpressions ?? []);
        Assert.Equal(["Ingress", "Egress"], policy.Spec.PolicyTypes);
        Assert.Empty(policy.Spec.Ingress ?? []);
        Assert.Empty(policy.Spec.Egress ?? []);
    }

    [Fact]
    public void The_run_namespace_enforces_the_restricted_pod_security_level()
    {
        var ns = Assert.Single(KubernetesYaml.LoadAllFromString(Docs("role.yaml")).OfType<V1Namespace>());

        Assert.Equal("ild-runs", ns.Metadata.Name);
        Assert.Equal("restricted", ns.Metadata.Labels["pod-security.kubernetes.io/enforce"]);
    }

    [Fact]
    public void The_role_can_use_config_maps_and_has_no_access_to_secrets()
    {
        var role = Assert.Single(KubernetesYaml.LoadAllFromString(Docs("role.yaml")).OfType<V1Role>());
        var resources = role.Rules.SelectMany(r => r.Resources).ToList();

        Assert.Contains("configmaps", resources);
        Assert.DoesNotContain("secrets", resources);
    }

    [Fact]
    public void The_optional_endpoints_reader_can_read_only_the_kubernetes_service_endpoints()
    {
        var role = Assert.Single(KubernetesYaml.LoadAllFromString(Docs("kubernetes-endpoints-reader-optional.yaml")).OfType<V1Role>());
        var rule = Assert.Single(role.Rules);

        Assert.Equal("default", role.Metadata.NamespaceProperty);
        Assert.Equal(["endpoints"], rule.Resources);
        Assert.Equal(["kubernetes"], rule.ResourceNames);
        Assert.Equal(["get"], rule.Verbs);
    }

    private static KubernetesRunHostOptions From(string? selector = null, string? ildNamespace = null) => KubernetesRunHostOptions.From(name => name switch
    {
        "ILD_API_URL" => "http://ild.example:8080",
        "ILD_K8S_RUNNER_IMAGE" => "registry.example/ild-runner:1",
        "ILD_K8S_ILD_POD_SELECTOR" => selector,
        "ILD_K8S_ILD_NAMESPACE" => ildNamespace,
        _ => null,
    });

    [Theory]
    [InlineData(",")]
    [InlineData(" , ")]
    [InlineData("=x")]
    [InlineData("a=")]
    [InlineData("a")]
    [InlineData("a=b,")]
    [InlineData("a=b,,c=d")]
    [InlineData("-a=b")]
    [InlineData("a=b c")]
    [InlineData("a/b/c=d")]
    public void A_pod_selector_that_is_empty_or_malformed_stops_startup(string selector)
        => Assert.Throws<InvalidOperationException>(() => From(selector));

    [Fact]
    public void A_pod_selector_of_labels_is_taken_and_the_ild_namespace_is_read()
    {
        var options = From("app.kubernetes.io/name=ild, tier=web", "ild-system");

        Assert.Equal(new Dictionary<string, string> { ["app.kubernetes.io/name"] = "ild", ["tier"] = "web" }, options.IldPodSelector);
        Assert.Equal("ild-system", options.IldNamespace);
    }

    [Fact]
    public void A_kubeconfig_without_an_ild_namespace_stops_startup_and_one_with_it_gets_past_the_check()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ild-kubeconfig-{Guid.NewGuid():N}");
        File.WriteAllText(file, "apiVersion: v1\nkind: Config\n");
        try
        {
            string? Setting(string name, string? ildNamespace) => name switch
            {
                "ILD_API_URL" => "http://ild.example:8080",
                "ILD_K8S_RUNNER_IMAGE" => "i",
                "ILD_K8S_KUBECONFIG" => file,
                "ILD_K8S_ILD_NAMESPACE" => ildNamespace,
                _ => null,
            };
            var logs = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
            var runner = new ILD.Core.Services.RunHosts.Local.ProcessRunner();

            var missing = Assert.Throws<InvalidOperationException>(() => KubernetesRunHostFactory.Create(new ILD.Core.Services.RunHosts.Local.LocalRunHost(), n => Setting(n, null), runner, logs));
            Assert.Contains("ILD_K8S_ILD_NAMESPACE", missing.Message);

            // With the namespace the check passes; the empty kubeconfig then fails on something else.
            var other = Record.Exception(() => KubernetesRunHostFactory.Create(new ILD.Core.Services.RunHosts.Local.LocalRunHost(), n => Setting(n, "ild"), runner, logs));
            Assert.DoesNotContain("ILD_K8S_ILD_NAMESPACE", other?.Message ?? "");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
