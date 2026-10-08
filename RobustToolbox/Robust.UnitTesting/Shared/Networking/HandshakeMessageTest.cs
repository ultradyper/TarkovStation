using System;
using Lidgren.Network;
using NUnit.Framework;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages.Handshake;

namespace Robust.UnitTesting.Shared.Networking;

internal sealed class HandshakeMessageTest
{
    [Test]
    public void EncryptionResponseTryReadAcceptsValidMessage()
    {
        var expected = new MsgEncryptionResponse
        {
            UserId = Guid.NewGuid(),
            SealedData = [1, 2, 3, 4],
            LegacyHwid = [5, 6]
        };

        var actual = new MsgEncryptionResponse();

        Assert.That(actual.TryReadFromBuffer(ToIncoming(expected)), Is.True);
        Assert.That(actual.UserId, Is.EqualTo(expected.UserId));
        Assert.That(actual.SealedData, Is.EqualTo(expected.SealedData));
        Assert.That(actual.LegacyHwid, Is.EqualTo(expected.LegacyHwid));
    }

    [Test]
    public void EncryptionResponseTryReadRejectsTruncatedMessage()
    {
        var outgoing = new NetOutgoingMessage();
        outgoing.Write(Guid.NewGuid());
        outgoing.WriteVariableInt32(4);
        outgoing.Write((byte)1);

        var actual = new MsgEncryptionResponse();

        Assert.That(actual.TryReadFromBuffer(ToIncoming(outgoing)), Is.False);
    }

    [Test]
    public void EncryptionResponseTryReadRejectsOversizedFields()
    {
        var outgoing = new NetOutgoingMessage();
        outgoing.Write(Guid.NewGuid());
        outgoing.WriteVariableInt32(1024);

        var actual = new MsgEncryptionResponse();

        Assert.That(actual.TryReadFromBuffer(ToIncoming(outgoing)), Is.False);
    }

    private static NetIncomingMessage ToIncoming(MsgEncryptionResponse response)
    {
        var outgoing = new NetOutgoingMessage();
        response.WriteToBuffer(outgoing, null!);
        return ToIncoming(outgoing);
    }

    private static NetIncomingMessage ToIncoming(NetOutgoingMessage outgoing)
    {
        var data = new byte[outgoing.LengthBytes];
        Array.Copy(outgoing.Data, data, data.Length);

        return new NetIncomingMessage
        {
            m_data = data,
            m_bitLength = outgoing.LengthBits
        };
    }
}
