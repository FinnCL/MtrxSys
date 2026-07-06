using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.Warmup;

/// <summary>Cria um <see cref="IWahaClient"/> apontado pro WAHA de um membro do pool (baseUrl + apiKey
/// próprios). Reusa o <see cref="WahaClient"/> interno — só varia o <c>BaseAddress</c> do HttpClient e
/// a <c>ApiKey</c> das options (o <c>WahaHttp</c> injeta o header X-Api-Key a partir delas). Usa o
/// named HttpClient "warmup-pool" (handler pooled pelo factory) pra não vazar sockets.</summary>
internal sealed class PoolWahaClientFactory(IHttpClientFactory httpFactory) : IPoolWahaClientFactory
{
    public IWahaClient CreateFor(WarmupMemberOptions member)
    {
        var baseUrl = member.WahaBaseUrl.EndsWith('/') ? member.WahaBaseUrl : member.WahaBaseUrl + "/";
        var client = httpFactory.CreateClient("warmup-pool");
        client.BaseAddress = new Uri(baseUrl);
        // WahaOptions construído direto (sem validação do binder): o WahaHttp só lê ApiKey (header) e o
        // proxy (não usado aqui — o proxy de cada chip é config da própria sessão). BaseUrl não é lido
        // pelo WahaHttp (a base vem do HttpClient acima), mas preenchemos por consistência.
        var opts = Options.Create(new WahaOptions { ApiKey = member.ApiKey, BaseUrl = baseUrl });
        return new WahaClient(client, opts);
    }
}
