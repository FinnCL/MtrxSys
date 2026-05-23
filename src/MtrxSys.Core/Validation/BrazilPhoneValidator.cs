using MtrxSys.Core.Domain.Common;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Validation;

public sealed class BrazilPhoneValidator
{
    public Result<PhoneNumber> Validate(string raw)
    {
        _ = raw;
        throw new NotImplementedException("Implementação na próxima rodada — normaliza para E.164, valida DDD Anatel, força 9º dígito DDDs 11-28.");
    }
}
