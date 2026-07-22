using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.AddressBook;

/// <summary>Provider inerte: usado quando AddressBookSync está desligado ou sem credenciais. O motor
/// vê <see cref="IsEnabled"/> = false e nem chama o save — comportamento atual intacto.</summary>
public sealed class NoOpContactAddressBookSync : IContactAddressBookSync
{
    public bool IsEnabled => false;
    public int GraceSeconds => 0;

    public Task<AddressBookSaveResult> EnsureSavedAsync(string phoneE164, string? name, CancellationToken ct)
        => Task.FromResult(AddressBookSaveResult.NotSupported);
}
