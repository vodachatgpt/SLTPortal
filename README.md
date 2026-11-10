
using System;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Filters;
using SLTProject.Services;

namespace SLTProject.Filters
{
    /// <summary>
    /// Allows authenticated guests only.
    /// Admin JWT tokens are not accepted.
    /// </summary>
    public sealed class SltGuestAttribute
        : AuthorizationFilterAttribute
    {
        private readonly GuestsService _guestsService =
            new GuestsService();

        public override async Task OnAuthorizationAsync(
            HttpActionContext actionContext,
            CancellationToken cancellationToken)
        {
            if (AllowsAnonymous(actionContext))
                return;

            var authorization =
                actionContext.Request.Headers.Authorization;

            if (authorization == null ||
                !string.Equals(
                    authorization.Scheme,
                    "Bearer",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(
                    authorization.Parameter))
            {
                SetUnauthorized(actionContext);
                return;
            }

            try
            {
                string token = authorization.Parameter;

                // Validate using the existing guest authentication.
                // Do not use the admin JWT validator here.
                var guest = await _guestsService.CurrentAsync(token);

                if (guest == null ||
                    guest.Id <= 0 ||
                    string.IsNullOrWhiteSpace(guest.Email))
                {
                    SetUnauthorized(actionContext);
                    return;
                }

                var principal = new GenericPrincipal(
                    new GenericIdentity(
                        guest.Email,
                        "SLTGuest"),
                    new[] { "Guest" });

                actionContext.RequestContext.Principal =
                    principal;

                Thread.CurrentPrincipal = principal;

                if (HttpContext.Current != null)
                    HttpContext.Current.User = principal;

                actionContext.Request.Properties[
                    "SLTGuestId"] = guest.Id;

                actionContext.Request.Properties[
                    "SLTGuestEmail"] = guest.Email;

                actionContext.Request.Properties[
                    "SLTGuestFullName"] = guest.FullName;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    "SLT guest authorization failed: {0}",
                    ex);

                actionContext.Response =
                    actionContext.Request.CreateResponse(
                        HttpStatusCode.ServiceUnavailable,
                        new
                        {
                            Message =
                                "Guest authentication is temporarily unavailable."
                        });
            }
        }

        private static bool AllowsAnonymous(
            HttpActionContext context)
        {
            return context.ActionDescriptor
                       .GetCustomAttributes<AllowAnonymousAttribute>()
                       .Any()
                   ||
                   context.ControllerContext.ControllerDescriptor
                       .GetCustomAttributes<AllowAnonymousAttribute>()
                       .Any();
        }

        private static void SetUnauthorized(
            HttpActionContext context)
        {
            context.Response =
                context.Request.CreateResponse(
                    HttpStatusCode.Unauthorized,
                    new
                    {
                        Message =
                            "A valid guest session is required."
                    });
        }
    }
}
