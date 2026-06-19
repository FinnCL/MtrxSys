using System.Net;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Safety;

namespace MtrxSys.Api.Endpoints;

// Opt-out por link de 1 clique (público, sem login). O destinatário toca o link da mensagem, cai
// numa página de confirmação e clica "Confirmar saída" — só então marcamos o opt-out.
// IMPORTANTE: o GET NÃO desinscreve. O WhatsApp faz fetch do link pra gerar o preview no envio;
// se o GET já marcasse opt-out, todo mundo "sairia" sozinho na hora do disparo. Só o POST (clique
// real do usuário) executa.
public static class OptOutPublicEndpoints
{
    public static IEndpointRouteBuilder MapOptOutPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/sair").AllowAnonymous();

        group.MapGet("", (string? t, OptOutLinkSigner signer, IClock clock) =>
        {
            if (!signer.TryVerify(t, clock.UtcNow, out _))
            {
                return InvalidPage();
            }
            var body =
                "<p>Deseja parar de receber nossas mensagens?</p>"
                + "<form method=\"post\" action=\"/sair/confirm\">"
                + $"<input type=\"hidden\" name=\"t\" value=\"{WebUtility.HtmlEncode(t)}\" />"
                + "<button type=\"submit\">Confirmar saída</button>"
                + "</form>";
            return Html(Page("Confirmar saída", body));
        });

        group.MapPost("/confirm", async (
            HttpContext http,
            OptOutLinkSigner signer,
            IClock clock,
            MarkContactOptOutUseCase useCase,
            CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
            {
                return InvalidPage(); // POST malformado (não-form) — não derruba o endpoint
            }
            var form = await http.Request.ReadFormAsync(ct);
            var token = form["t"].ToString();
            if (!signer.TryVerify(token, clock.UtcNow, out var contactId))
            {
                return InvalidPage();
            }
            var result = await useCase.MarkAsync(contactId, ct);
            var body = result == OptOutResult.NotFound
                ? "<p>Não localizamos seu cadastro — mas, por segurança, você não receberá mais mensagens.</p>"
                : "<p>Pronto! Você não vai mais receber nossas mensagens. Obrigado.</p>";
            return Html(Page("Você saiu", body));
        });

        return app;
    }

    private static IResult InvalidPage() =>
        Html(Page("Link inválido",
            "<p>Este link não é mais válido ou expirou. Você também pode responder <b>SAIR</b> na conversa.</p>"));

    private static IResult Html(string html) => Results.Content(html, "text/html; charset=utf-8");

    private static string Page(string title, string bodyHtml)
    {
        var t = WebUtility.HtmlEncode(title);
        return "<!doctype html><html lang=\"pt-br\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + $"<title>{t}</title><style>"
            + "body{font-family:system-ui,Arial,sans-serif;max-width:480px;margin:48px auto;padding:0 16px;color:#1d2330;line-height:1.5}"
            + "h1{font-size:20px}button{font-size:16px;padding:10px 18px;border:0;border-radius:6px;background:#2c6cd4;color:#fff;cursor:pointer}"
            + $"</style></head><body><h1>{t}</h1>{bodyHtml}</body></html>";
    }
}
