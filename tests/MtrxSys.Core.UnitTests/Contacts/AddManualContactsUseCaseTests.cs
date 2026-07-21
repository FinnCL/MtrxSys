using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Contacts;

public sealed class AddManualContactsUseCaseTests
{
    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private AddManualContactsUseCase BuildUseCase(params Contact[] existing)
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var byPhone = existing.ToDictionary(c => c.Phone.E164, c => c, StringComparer.Ordinal);
        _contacts.GetByPhonesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, Contact>)byPhone);
        return new AddManualContactsUseCase(_contacts, _uow, _clock, new BrazilPhoneValidator(),
            new WarmupSeedEnroller(
                Substitute.For<ISystemStateRepository>(), Substitute.For<IWarmupCircleRepository>(),
                Options.Create(new DispatchOptions()), _clock));
    }

    [Fact]
    public async Task Adds_valid_number_in_canonical_e164()
    {
        var useCase = BuildUseCase();
        var input = new[] { "11987654321" };

        var result = await useCase.ExecuteAsync(input, null, CancellationToken.None);

        result.Added.Should().Be(1);
        result.Lines[0].Status.Should().Be(ManualLineStatus.Ok);
        result.Lines[0].Phone.Should().Be("+5511987654321");
        await _contacts.Received(1).AddAsync(
            Arg.Is<Contact>(c => c.Phone.E164 == "+5511987654321" && c.GroupTag == "Avulsos"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inserts_ninth_digit_for_old_mobile_and_flags_correction()
    {
        var useCase = BuildUseCase();
        var input = new[] { "1187654321" };

        // Celular antigo sem o 9 (DDD + 8 dígitos): deve virar +55 11 9 8765-4321 e marcar a correção.
        var result = await useCase.ExecuteAsync(input, null, CancellationToken.None);

        result.Added.Should().Be(1);
        result.Corrected.Should().Be(1);
        result.Lines[0].Status.Should().Be(ManualLineStatus.Corrected);
        result.Lines[0].Phone.Should().Be("+5511987654321");
        result.Lines[0].Correction.Should().Be("9º dígito inserido");
    }

    [Fact]
    public async Task Rejects_number_without_ddd()
    {
        var useCase = BuildUseCase();
        var input = new[] { "987654321" };

        var result = await useCase.ExecuteAsync(input, null, CancellationToken.None);

        result.Added.Should().Be(0);
        result.Invalid.Should().Be(1);
        result.Lines[0].Status.Should().Be(ManualLineStatus.Invalid);
        result.Lines[0].Reason.Should().NotBeNullOrWhiteSpace();
        await _contacts.DidNotReceive().AddAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marks_existing_number_as_duplicate_without_creating()
    {
        var already = Contact.Create(
            Guid.NewGuid(), PhoneNumber.FromValidatedE164("+5511987654321"),
            name: null, groupTag: "Avulsos", theme: null, optInAt: null);
        var useCase = BuildUseCase(already);

        // Mesmo número em dois formatos diferentes: dedup pelo E.164 final, conta uma vez só.
        var input = new[] { "+5511987654321", "(11) 98765-4321" };
        var result = await useCase.ExecuteAsync(input, null, CancellationToken.None);

        result.Added.Should().Be(0);
        result.Duplicated.Should().Be(2);
        result.Lines.Should().AllSatisfy(l => l.Status.Should().Be(ManualLineStatus.Duplicate));
        await _contacts.DidNotReceive().AddAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Matches_legacy_no_ninth_digit_contact_without_creating_duplicate()
    {
        // Contato importado de grupo salvo SEM o 9 (forma crua da WAHA que a lib rejeita).
        var legacy = Contact.Create(
            Guid.NewGuid(), PhoneNumber.FromValidatedE164("+551187654321"),
            name: null, groupTag: "Vendas", theme: null, optInAt: null);
        var useCase = BuildUseCase(legacy);
        var input = new[] { "+5511987654321" }; // mesma pessoa, forma moderna com o 9

        var result = await useCase.ExecuteAsync(input, null, CancellationToken.None);

        result.Added.Should().Be(0);
        result.Duplicated.Should().Be(1);
        result.Lines[0].Status.Should().Be(ManualLineStatus.Duplicate);
        // Mostra a forma que está salva (sem o 9) — é como o WhatsApp roteia esse número legado.
        result.Lines[0].Phone.Should().Be("+551187654321");
        await _contacts.DidNotReceive().AddAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_foreign_number_even_if_valid_elsewhere()
    {
        var useCase = BuildUseCase();
        var input = new[] { "+12125551234" }; // número válido dos EUA

        var result = await useCase.ExecuteAsync(input, null, CancellationToken.None);

        result.Added.Should().Be(0);
        result.Invalid.Should().Be(1);
        result.Lines[0].Status.Should().Be(ManualLineStatus.Invalid);
        result.Lines[0].Reason.Should().Contain("Brasil");
        await _contacts.DidNotReceive().AddAsync(Arg.Any<Contact>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Collapses_all_55_forms_into_single_contact()
    {
        var useCase = BuildUseCase();
        // Com +55, com 55 sem +, e sem 55 — tudo a mesma pessoa: um contato só, sem duplicar.
        var input = new[] { "+5511987654321", "5511987654321", "11987654321" };

        var result = await useCase.ExecuteAsync(input, null, CancellationToken.None);

        result.Added.Should().Be(1);
        result.Duplicated.Should().Be(2);
        await _contacts.Received(1).AddAsync(
            Arg.Is<Contact>(c => c.Phone.E164 == "+5511987654321"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preserves_input_order_in_results()
    {
        var useCase = BuildUseCase();
        var input = new[] { "987654321", "11987654321" };

        var result = await useCase.ExecuteAsync(input, null, CancellationToken.None);

        result.Lines[0].Input.Should().Be("987654321");
        result.Lines[0].Status.Should().Be(ManualLineStatus.Invalid);
        result.Lines[1].Input.Should().Be("11987654321");
        result.Lines[1].Status.Should().Be(ManualLineStatus.Ok);
    }
}
