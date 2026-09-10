using System.IO;

namespace DirectConnectIP.Network.Packets;

/// <summary>
/// 客户端 → 房主：请求快速SL
/// </summary>
public class QuickSLRequestPacket : IModPacket
{
    public ModPacketType Type => ModPacketType.QuickSLRequest;
    public ulong RequesterId { get; private set; }

    public QuickSLRequestPacket() { }
    public QuickSLRequestPacket(ulong requesterId)
    {
        RequesterId = requesterId;
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(RequesterId);
    }

    public void Deserialize(BinaryReader reader)
    {
        RequesterId = reader.ReadUInt64();
    }
}

/// <summary>
/// 房主 → 客户端：回复快速SL请求结果
/// </summary>
public class QuickSLResponsePacket : IModPacket
{
    public ModPacketType Type => ModPacketType.QuickSLResponse;
    public bool Accepted { get; private set; }

    public QuickSLResponsePacket() { }
    public QuickSLResponsePacket(bool accepted)
    {
        Accepted = accepted;
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Accepted);
    }

    public void Deserialize(BinaryReader reader)
    {
        Accepted = reader.ReadBoolean();
    }
}
