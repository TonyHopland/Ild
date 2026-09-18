using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ILD.WorkItemServer.Attachments;

/// <summary>
/// Raises the request-body cap of the endpoint it marks to the attachment
/// ceiling, leaving every other route on the host's default.
///
/// A resource filter rather than the first lines of the action: MVC parses a
/// form request for its value providers <em>before</em> the action runs, and the
/// cap is fixed once anything starts reading the body. Set inside the action it
/// would arrive too late and the host default would decide, however the
/// deployment configured <c>ILD_MAX_ATTACHMENT_MB</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RaiseAttachmentBodyLimitAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => true;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new Filter(serviceProvider.GetRequiredService<AttachmentLimits>());

    private sealed class Filter(AttachmentLimits limits) : IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            var bodySize = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
                bodySize.MaxRequestBodySize = limits.MaxRequestBytes;
        }

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }
}
