using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace ILD.Tests;

/// <summary>Multipart bodies shaped the way the attachment endpoints accept them.</summary>
internal static class AttachmentUpload
{
    /// <summary>The form field every attachment upload arrives under.</summary>
    public const string FieldName = "files";

    public static MultipartFormDataContent Of(params (string FileName, string? ContentType, byte[] Bytes)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var (fileName, contentType, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            // TryAddWithoutValidation so a test can send a content type the
            // parser rejects, which is exactly what the normalisation is for.
            if (contentType != null) part.Headers.TryAddWithoutValidation("Content-Type", contentType);
            content.Add(part, FieldName, fileName);
        }
        return content;
    }

    public static MultipartFormDataContent Of(string fileName, string? contentType, byte[] bytes)
        => Of((fileName, contentType, bytes));

    /// <summary>Deterministic, non-compressible-looking bytes of a given length.</summary>
    public static byte[] Bytes(int count, byte seed = 7)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) bytes[i] = (byte)((i * 31 + seed) % 251);
        return bytes;
    }
}

/// <summary>
/// Stands in for the request-body size limit a real server (Kestrel, IIS) hands
/// each request. <see cref="TestServer"/> supplies none, so a test that wants to
/// see an endpoint raise its own cap has to bring one.
/// </summary>
internal sealed class RecordingMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
{
    /// <summary>Stands for whatever the host's default is: any value the endpoint did not choose.</summary>
    public const long HostDefault = 30_000_000;

    public bool IsReadOnly => false;
    public long? MaxRequestBodySize { get; set; } = HostDefault;
}

internal static class BodySizeObservation
{
    /// <summary>
    /// Drives one request through the pipeline with a
    /// <see cref="RecordingMaxRequestBodySizeFeature"/> installed, and hands back
    /// both the finished context and the feature, so a test can see which
    /// endpoints raise the cap and which leave it alone.
    /// </summary>
    public static async Task<(HttpContext Context, RecordingMaxRequestBodySizeFeature BodySize)> ObserveBodySizeLimitAsync(
        this TestServer server,
        string method,
        string path,
        string? bearerToken = null,
        HttpContent? body = null)
    {
        var feature = new RecordingMaxRequestBodySizeFeature();

        MemoryStream? payload = null;
        string? contentType = null;
        if (body != null)
        {
            payload = new MemoryStream();
            await body.CopyToAsync(payload);
            payload.Position = 0;
            contentType = body.Headers.ContentType?.ToString();
        }

        var context = await server.SendAsync(ctx =>
        {
            ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
            ctx.Request.Method = method;
            ctx.Request.Path = path;
            if (bearerToken != null) ctx.Request.Headers["Authorization"] = "Bearer " + bearerToken;
            if (payload != null)
            {
                ctx.Request.Body = payload;
                ctx.Request.ContentLength = payload.Length;
                ctx.Request.ContentType = contentType!;
            }
        });

        return (context, feature);
    }
}

/// <summary>Files in the working tree, for the tests that assert on shipped docs and migrations.</summary>
internal static class RepositoryFiles
{
    public static string Root
    {
        get
        {
            var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var root = tfm.Parent!.Parent!.Parent!.Parent!;
            Assert.True(File.Exists(Path.Combine(root.FullName, "ILD.sln")), $"Not a repository root: {root.FullName}");
            return root.FullName;
        }
    }

    public static string ReadAllText(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public static string[] ReadAllLines(string relativePath) => File.ReadAllLines(Path.Combine(Root, relativePath));
}
