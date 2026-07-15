using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Contacts;

/// <summary>
/// Importação de participantes de grupo → contatos. É o caminho que enche o público de disparo, e
/// não tinha teste nenhum.
///
/// Os casos aqui são os que já falharam DE VERDADE em produção ou que causariam falha silenciosa:
/// número BR no formato antigo (o WhatsApp guarda assim), e a marca de qual chip importou — que o
/// motor usa pra decidir se pode enviar.
/// </summary>
public sealed class ImportGroupMembersUseCaseTests
{
    private const string Session = "default";
    private const string GroupId = "120363411066030182";
    private const string OwnPhone = "+557191072835";

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    }

    private readonly IWahaClient _waha = Substitute.For<IWahaClient>();
    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();
    private readonly List<Contact> _added = [];

    private ImportGroupMembersUseCase Build()
    {
        _contacts.GetByPhonesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Contact>(StringComparer.Ordinal));
        _contacts.AddAsync(Arg.Do<Contact>(_added.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return new ImportGroupMembersUseCase(
            _waha, _contacts, Substitute.For<IUnitOfWork>(), new FixedClock(),
            new BrazilPhoneValidator(),
            Options.Create(new DispatchOptions { SessionId = Session, OnlyBrazilianContacts = true }));
    }

    private void SetMembers(params string[] phones) =>
        _waha.ListGroupParticipantsAsync(Session, GroupId, Arg.Any<CancellationToken>())
            .Returns([.. phones.Select(p => new WahaParticipant(p.TrimStart('+'), p, null, false))]);

    private void SetOwnPhone(string? phone) =>
        _waha.GetOwnPhoneE164Async(Session, Arg.Any<CancellationToken>()).Returns(phone);

    // Números REAIS de um grupo de produção: BR no formato ANTIGO (8 dígitos após o DDD, sem o 9º).
    // A libphonenumber os REJEITA como inválidos, e o import chegou a rotulá-los "não brasileiro" e
    // descartar 6 de 6 — um grupo inteiro de conhecidos importando ZERO contatos.
    [Fact]
    public async Task Importa_numero_br_legado_sem_o_9o_digito()
    {
        SetMembers("+557182368724", "+557184731714", "+557193836443");
        SetOwnPhone(OwnPhone);

        var result = await Build().ExecuteAsync(GroupId, "Amigos", CancellationToken.None);

        result.Imported.Should().Be(3);
        result.Failed.Should().Be(0, "são brasileiros — só são antigos");
        _added.Select(c => c.Phone.E164).Should()
            .BeEquivalentTo(["+557182368724", "+557184731714", "+557193836443"],
                "preserva a forma que o WhatsApp usa; inserir o 9º dígito daria 463 = gatilho de ban");
    }

    [Fact]
    public async Task Nao_importa_o_proprio_numero_do_chip()
    {
        SetMembers("+557182368724", OwnPhone);
        SetOwnPhone(OwnPhone);

        var result = await Build().ExecuteAsync(GroupId, "Amigos", CancellationToken.None);

        result.Imported.Should().Be(1);
        _added.Select(c => c.Phone.E164).Should().NotContain(OwnPhone, "seria auto-envio");
    }

    [Fact]
    public async Task Marca_o_chip_que_importou_para_o_gate_anti_463_deixar_passar()
    {
        SetMembers("+557182368724");
        SetOwnPhone(OwnPhone);

        await Build().ExecuteAsync(GroupId, "Amigos", CancellationToken.None);

        _added.Should().ContainSingle().Which.ImportedByPhone.Should().Be(OwnPhone,
            "o motor só envia pra contato do chip conectado; sem esta marca o disparo pula todos");
    }

    // Sem o número do chip, o contato nasceria com ImportedByPhone nulo — que o motor lê como
    // "legado, de chip desconhecido" e PULA. A tela diria "importados" e o disparo não enviaria nada,
    // sem pista do porquê. Falhar é recuperável (clicar de novo); o silêncio não é.
    [Fact]
    public async Task Sem_o_numero_do_chip_falha_alto_em_vez_de_criar_contato_orfao()
    {
        SetMembers("+557182368724");
        SetOwnPhone(null);

        var act = async () => await Build().ExecuteAsync(GroupId, "Amigos", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _added.Should().BeEmpty("nada pode ser gravado sem dono");
    }

    [Fact]
    public async Task Ignora_estrangeiro_quando_so_brasileiros()
    {
        SetMembers("+557182368724", "+13475551234");
        SetOwnPhone(OwnPhone);

        var result = await Build().ExecuteAsync(GroupId, "Amigos", CancellationToken.None);

        result.Imported.Should().Be(1);
        result.Failed.Should().Be(1);
        _added.Select(c => c.Phone.E164).Should().NotContain("+13475551234");
    }
}

