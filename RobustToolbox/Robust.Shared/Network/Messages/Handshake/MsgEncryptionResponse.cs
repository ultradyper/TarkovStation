using System;
using Lidgren.Network;
using Robust.Shared.Serialization;

#nullable disable

namespace Robust.Shared.Network.Messages.Handshake
{
    internal sealed class MsgEncryptionResponse : NetMessage
    {
        private const int GuidLength = 16;
        private const int MaxSealedDataLength = 512;
        private const int MaxLegacyHwidLength = 256;

        public override string MsgName => string.Empty;

        public override MsgGroups MsgGroup => MsgGroups.Core;

        public Guid UserId;
        public byte[] SealedData;
        public byte[] LegacyHwid;

        public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
        {
            if (!TryReadFromBuffer(buffer))
                throw new InvalidOperationException("Malformed encryption response.");
        }

        public bool TryReadFromBuffer(NetIncomingMessage buffer)
        {
            if (!buffer.ReadBytes(GuidLength, out var guidBytes))
                return false;

            UserId = new Guid(guidBytes);
            if (!TryReadByteArray(buffer, MaxSealedDataLength, out SealedData))
                return false;

            if (!TryReadByteArray(buffer, MaxLegacyHwidLength, out LegacyHwid))
                return false;

            return true;
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
        {
            buffer.Write(UserId);
            buffer.WriteVariableInt32(SealedData.Length);
            buffer.Write(SealedData);
            buffer.WriteVariableInt32(LegacyHwid.Length);
            buffer.Write(LegacyHwid);
        }

        private static bool TryReadByteArray(NetIncomingMessage buffer, int maxLength, out byte[] result)
        {
            result = [];

            if (!TryReadVariableInt32(buffer, out var length))
                return false;

            if (length < 0 || length > maxLength)
                return false;

            return buffer.ReadBytes(length, out result);
        }

        private static bool TryReadVariableInt32(NetIncomingMessage buffer, out int result)
        {
            result = 0;

            if (!buffer.ReadVariableUInt32(out var encoded))
                return false;

            result = (int)(encoded >> 1) ^ -(int)(encoded & 1);
            return true;
        }
    }
}
