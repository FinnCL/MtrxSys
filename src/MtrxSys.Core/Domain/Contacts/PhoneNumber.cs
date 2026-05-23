using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Contacts;

public sealed class PhoneNumber : ValueObject
{
    public string E164 { get; }

    private PhoneNumber(string e164) => E164 = e164;

    internal static PhoneNumber FromValidatedE164(string e164) => new(e164);

    public override string ToString() => E164;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return E164;
    }
}
