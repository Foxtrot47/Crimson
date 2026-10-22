using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static Crimson.Providers.Ubisoft.Protocol.UbisoftWire;

namespace Crimson.Providers.Ubisoft.Protocol;

/// <summary>Generated IPC state for one account/game launch, with no disk activation cache.</summary>
internal sealed class UbisoftGameSession(UbisoftSession login, UbisoftEntitlement entitlement,
    UbisoftInstalledGame installation, UbisoftPcSession pc, JsonElement profile, string saveDirectory, IUbisoftTickets tickets)
{
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    // Include failed answers so an IPC retry cannot silently consume another activation after a timeout.
    private readonly Dictionary<string, Task<byte[]>> _answers = new(StringComparer.Ordinal);
    internal byte[]? SessionResponse(byte[] request)
    {
        var fields = Read(request);
        if (fields.Has(13))
        {
            var installer = Read(fields.Data(13));
            return installer.Has(1) ? Bytes(10, Bytes(1,
                Message(Integer(1, 1), Bytes(2, []), Text(3, installation.Language)))) : null;
        }
        if (!fields.Has(1)) return null;
        var init = Read(fields.Data(1));
        if (init.Number(1) != entitlement.ProductId) throw new InvalidDataException("Ubisoft session requested another product.");
        if (entitlement.ApplicationId is null) throw new InvalidOperationException("Game application ID is unavailable.");
        string Identity(string key) => profile.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var identity = Message(Text(1, login.UserId), Text(2, Identity("username")), Text(3, Identity("nameOnPlatform")),
            Text(4, Identity("email")), Text(5, ""), Text(6, Identity("country")));
        return Bytes(1, Message(Integer(1, 1), Bytes(2, identity), Integer(3, 1), Integer(4, entitlement.ProductId),
            Message(pc.OwnedProducts.Select(p => Integer(5, p)).ToArray()),
            Bytes(6, Message(Integer(1, entitlement.ProductId), Text(2, ""))), Integer(7, 0), Bytes(8, pc.PcTicket),
            Bytes(11, Integer(1, 0)), Integer(12, 0), Bytes(15, Message(Integer(1, 1), Integer(2, 3))),
            // PoC compatibility metrics, not a claim to have benchmarked the current PC.
            Bytes(16, Message(Integer(1, 6211), Integer(2, 22395), Float(3, 1))),
            Bytes(17, Text(1, saveDirectory)), Bytes(18, []), Bytes(19, Message(Integer(1, 1), Integer(2, 1))),
            Bytes(21, Integer(2, 1)), Bytes(22, Text(1, entitlement.ApplicationId)), Bytes(23, [])));
    }
    internal async Task<byte[]> AuxResponse(byte[] request, uint launcherPid, CancellationToken token)
    {
        var fields = Read(request);
        var id = fields.Number(1);
        if (fields.Has(2))
        {
            if (Read(fields.Data(2)).Number(1) != entitlement.ProductId) throw new InvalidDataException("Ubisoft aux requested another product.");
            // Avoid the PoC's hardcoded WD2 addon IDs; select owned addons from the live ownership catalog.
            return Message(Integer(1, id), Bytes(2, Message(Integer(1, 1), Integer(2, launcherPid),
                Message(pc.AddonsToValidate.Select(p => Integer(3, p)).ToArray()))));
        }
        var activation = Read(fields.Data(3));
        if (activation.Has(1)) throw new NotSupportedException("Activation cache invalidation is not supported in this initial integration.");
        var challenge = Read(activation.Data(2)).Data(2);
        if (challenge.Length == 0) throw new InvalidDataException("Ubisoft aux activation has no challenge.");
        var answer = await Answer(challenge, token);
        return Message(Integer(1, id), Bytes(3, Bytes(2, Message(Integer(2, 1), Integer(3, 0), Bytes(4, answer), Bytes(5, [])))));
    }
    private async Task<byte[]> Answer(byte[] challenge, CancellationToken token)
    {
        var key = Convert.ToHexString(SHA256.HashData(challenge));
        await _activationGate.WaitAsync(token);
        try
        {
            if (!_answers.TryGetValue(key, out var answer))
            {
                if (_answers.Count >= 8) throw new InvalidOperationException("Too many distinct activation challenges in one launch.");
                answer = tickets.Activate(login.Ticket, login.SessionId, entitlement.ProductId, challenge, token);
                _answers.Add(key, answer);
            }
            return await answer;
        }
        finally { _activationGate.Release(); }
    }
    internal byte[] GameStartResponse() => Bytes(2, Bytes(1, Message(Integer(1, 1), Integer(2, 0),
        Text(3, installation.Executable.Replace('\\', '/')), Text(4, installation.InstallDirectory.Replace('\\', '/')), Integer(8, 3),
        Bytes(9, Message(
            Bytes(1, Message(Integer(1, 1), Integer(2, 113), Integer(3, 0), Integer(4, 1), Integer(5, 0))),
            Bytes(1, Message(Integer(1, 3), Integer(2, 123), Integer(3, 0), Integer(4, 0), Integer(5, 0))))))));
    internal static byte[] ProcessResponse() => Bytes(13, Message(Integer(1, 1), Integer(3, 1), Integer(4, 3), Integer(6, 0)));
}
