﻿using Intersect.Enums;
using MessagePack;

namespace Intersect.Network.Packets.Server
{
    [MessagePackObject]
    public partial class NpcEntityPacket : EntityPacket
    {
        //Parameterless Constructor for MessagePack
        public NpcEntityPacket()
        {
        }


        [Key(24)]
        public NpcBehavior Aggression { get; set; }
    }

}
