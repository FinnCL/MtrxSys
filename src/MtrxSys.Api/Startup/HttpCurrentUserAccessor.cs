using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Api.Startup;

internal sealed class HttpCurrentUserAccessor(IHttpContextAccessor http) : ICurrentUserAccessor
{
    public Guid? UserId
    {
        get
        {
            var ctx = http.HttpContext;
            if (ctx?.User is null)
            {
                return null;
            }
            var raw = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }
}
