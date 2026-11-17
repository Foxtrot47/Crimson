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

/// <summary>
/// Generated IPC state for one account/game launch, with no disk activation cache. Serves both runtime SDKs: R1
/// (<c>uplay_r1</c>) asks for the session once or twice; R2 (<c>uplay_r2</c>) asks about ten times over parallel
/// connections, requests friends, achievements and its product list, and treats a missing answer or a missing
/// requested subsystem in the session as fatal.
/// </summary>
internal sealed class UbisoftGameSession(UbisoftSession login, UbisoftLaunchPlan plan, UbisoftPcSession pc,
    JsonElement profile, IUbisoftTickets tickets)
{
    private const ulong ResultOk = 1, ResultFailed = 2, ResultUnavailable = 8;
    // initReq subsystem flags whose answers the R2 SDK requires once requested.
    private const int SubsystemFriends = 4, SubsystemInstall = 8, SubsystemMultiplayer = 9, SubsystemProducts = 11;

    // Only addons the live PC session also lists for validation are reported to the game.
    private readonly IReadOnlyList<uint> _addons = plan.AddonIds.Intersect(pc.AddonsToValidate).ToList();
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    // Include failed answers so an IPC retry cannot silently consume another activation after a timeout.
    private readonly Dictionary<string, Task<byte[]>> _answers = new(StringComparer.Ordinal);
    internal byte[]? SessionResponse(byte[] request)
    {
        var fields = Read(request);
        if (fields.Has(7)) return FriendsResponse(Read(fields.Data(7)));
        if (fields.Has(8)) return AchievementResponse(Read(fields.Data(8)));
        if (fields.Has(23)) return ProductResponse(Read(fields.Data(23)));
        if (fields.Has(13)) return InstallerResponse(Read(fields.Data(13)));
        return fields.Has(1) ? InitResponse(Read(fields.Data(1))) : null;
    }

    private byte[]? InstallerResponse(IReadOnlyList<Field> installer) => installer.Has(1)
        ? Bytes(10, Bytes(1, Message(Integer(1, 1), Bytes(2, []), Text(3, plan.Language))))
        : null;

    private byte[] InitResponse(IReadOnlyList<Field> init)
    {
        if (init.Number(1) != plan.ProductId) throw new InvalidDataException("Ubisoft session requested another product.");
        if (plan.ApplicationId is null) throw new InvalidOperationException("Game application ID is unavailable.");
        bool Requested(int subsystem) => init.Number(subsystem) != 0;
        var consumables = plan.SdkProducts.Where(product => product.Type == UbisoftSdkProduct.TypeConsumable)
            .Select(product => Bytes(9, Message(Integer(1, product.ProductId), Integer(2, product.Balance))));
        // R2 logs "GetGameChunks failed: chunks list is empty" without one installed chunk.
        var install = Requested(SubsystemInstall)
            ? Bytes(14, Message(Integer(1, 1), Bytes(2, Bytes(1, Message(Integer(1, 0), Integer(2, 1), Text(3, "")))),
                Text(3, plan.Language)))
            : [];
        // "Client must give an initial list of Friends, even if empty"; Crimson has no friends list to give.
        var friends = Requested(SubsystemFriends) ? Bytes(18, Integer(2, 1)) : Bytes(18, []);
        // "Client must give an initial game session, even if empty".
        var multiplayer = Requested(SubsystemMultiplayer) ? Bytes(20, Integer(2, 1)) : [];
        return Bytes(1, Message(Integer(1, 1), Bytes(2, IdentityMessage()), Integer(3, 1), Integer(4, plan.ProductId),
            Message(pc.OwnedProducts.Select(p => Integer(5, p)).ToArray()),
            Bytes(6, Message(Integer(1, plan.ProductId), Text(2, ""))), Integer(7, 0), Bytes(8, pc.PcTicket),
            Bytes(11, Integer(1, 0)), Integer(12, 0), Message(consumables.ToArray()), install,
            Bytes(15, Message(Integer(1, 1), Integer(2, 3))),
            // PoC compatibility metrics, not a claim to have benchmarked the current PC.
            Bytes(16, Message(Integer(1, 6211), Integer(2, 22395), Float(3, 1))),
            Bytes(17, Text(1, plan.SaveDirectory)), friends, Bytes(19, Message(Integer(1, 1), Integer(2, 1))),
            multiplayer, Bytes(21, Integer(2, 1)), Bytes(22, Text(1, plan.ApplicationId)),
            Requested(SubsystemProducts) ? Bytes(23, []) : []));
    }

    private byte[] IdentityMessage() => Message(Text(1, login.UserId), Text(2, ProfileString("username")),
        Text(3, ProfileString("nameOnPlatform")), Text(4, ProfileString("email")), Text(5, ""), Text(6, ProfileString("country")));

    private string ProfileString(string key) =>
        profile.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    /// <summary>Friend requests are acknowledged by request ID; Crimson has no friends list to return.</summary>
    private static byte[]? FriendsResponse(IReadOnlyList<Field> request)
    {
        if (request.FirstOrDefault(field => field.WireType == 2) is not { } operation) return null;
        var operationFields = Read(operation.Data);
        if (!operationFields.Has(1)) return null;
        return Bytes(4, Bytes(12, Message(Integer(1, operationFields.Number(1)), Integer(2, ResultOk))));
    }

    /// <summary>
    /// A get (1) is answered with an empty list in its own reply field; the SDK ignores an earn reply to a get.
    /// Image (2) and earn (3) are reported unavailable rather than claimed.
    /// </summary>
    private static byte[]? AchievementResponse(IReadOnlyList<Field> request)
    {
        if (request.FirstOrDefault(field => field.Number is >= 1 and <= 3 && field.WireType == 2) is not { } operation)
            return null;
        var operationFields = Read(operation.Data);
        if (!operationFields.Has(1)) return null;
        var result = operation.Number == 1 ? ResultOk : ResultUnavailable;
        return Bytes(5, Bytes(operation.Number, Message(Integer(1, operationFields.Number(1)), Integer(2, result))));
    }

    private byte[]? ProductResponse(IReadOnlyList<Field> request)
    {
        if (!request.Has(1)) return null;
        var list = Read(request.Data(1));
        if (!list.Has(1)) return null;
        var id = list.Number(1);
        var account = list.String(3);
        if (list.Number(2) != 0 || (account.Length > 0 && account != login.UserId))
            return Bytes(15, Bytes(1, Message(Integer(1, id), Integer(2, ResultFailed))));
        var products = plan.SdkProducts.Select(product => Bytes(3, Message(Integer(1, product.ProductId),
            Integer(2, product.Type), Integer(3, product.Owned ? 1u : 4u), Integer(4, product.State),
            product.Type == UbisoftSdkProduct.TypeConsumable ? Integer(5, product.Balance) : [],
            Integer(6, product.Activation))));
        return Bytes(15, Bytes(1, Message(Integer(1, id), Integer(2, ResultOk), Message(products.ToArray()))));
    }
    internal async Task<byte[]> AuxResponse(byte[] request, uint launcherPid, CancellationToken token)
    {
        var fields = Read(request);
        var id = fields.Number(1);
        if (fields.Has(2))
        {
            if (Read(fields.Data(2)).Number(1) != plan.ProductId) throw new InvalidDataException("Ubisoft aux requested another product.");
            return Message(Integer(1, id), Bytes(2, Message(Integer(1, 1), Integer(2, launcherPid),
                Message(_addons.Select(p => Integer(3, p)).ToArray()))));
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
                answer = tickets.Activate(login.Ticket, login.SessionId, plan.ProductId, challenge, token);
                _answers.Add(key, answer);
            }
            return await answer;
        }
        finally { _activationGate.Release(); }
    }
    internal byte[] GameStartResponse(byte[] request)
    {
        var launch = Read(Read(Read(request).Data(1)).Data(1));
        if (launch.Number(2) != plan.ProductId) throw new InvalidDataException("Ubisoft starter requested another product.");
        return Bytes(2, Bytes(1, Message(Integer(1, 1), Integer(2, 0),
        Text(3, plan.Executable.Replace('\\', '/')), Text(4, plan.WorkingDirectory.Replace('\\', '/')), Integer(8, 3),
        Bytes(9, Message(
            Bytes(1, Message(Integer(1, 1), Integer(2, 113), Integer(3, 0), Integer(4, 1), Integer(5, 0))),
            Bytes(1, Message(Integer(1, 3), Integer(2, 123), Integer(3, 0), Integer(4, 0), Integer(5, 0))))))));
    }
    internal static byte[] ProcessResponse() => Bytes(13, Message(Integer(1, 1), Integer(3, 1), Integer(4, 3), Integer(6, 0)));
}
