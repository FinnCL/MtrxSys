using System.Text.Json;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Waha;

/// <summary>Agenda do aparelho (WAHA /contacts). Serve a Fase Humana: as pessoas reais salvas no
/// celular, de onde o operador escolhe o círculo de aquecimento.</summary>
internal sealed class WahaContactsClient(WahaHttp http)
{
    public async Task<IReadOnlyList<WahaContact>> ListContactsAsync(string sessionId, CancellationToken ct)
    {
        // ATENÇÃO: aqui a sessão vai como QUERY PARAM, não como segmento. É o contrato da WAHA pros
        // contatos (`/api/contacts/all?session=X`) e destoa de todo o resto do cliente, que usa
        // `/api/{session}/...`. Não "consertar" pra ficar igual aos outros — dá 404.
        using var req = http.NewRequest(HttpMethod.Get, $"api/contacts/all?session={WahaHttp.Esc(sessionId)}");
        using var resp = await http.SendAsync(req, ct);
        // Mesma degradação do ListGroupsAsync: sessão fora do ar dá 422, e o NOWEB sem store dá 400.
        // Lista vazia (a UI mostra "nenhum contato") em vez de 500 — que no browser ainda apareceria
        // como erro de CORS, porque o header some na resposta de erro.
        if (!resp.IsSuccessStatusCode)
        {
            return [];
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        // Tolerante às duas formas, como nos grupos: ARRAY (documentado) ou OBJETO indexado por JID
        // (o jeito que a NOWEB devolve os grupos — barato prevenir).
        IEnumerable<JsonElement> items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object => root.EnumerateObject().Select(p => p.Value),
            _ => [],
        };

        var list = new List<WahaContact>();
        foreach (var el in items)
        {
            if (WahaParsing.MapContact(el) is { } contact)
            {
                list.Add(contact);
            }
        }
        return list;
    }
}
