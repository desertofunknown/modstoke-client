// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftService
{
    private void ReceiveEva(Message message)
    {
        if (message.Sequence <= _receivedEvaSequence || message.Ship != _shipId || message.Eva == null) return;
        _receivedEvaSequence = message.Sequence;
        _evaState = message.Eva;
        _lastEvaAt = Now;
    }

    private sealed partial class SpacecraftEvent
    {
        internal void Eva(Peer peer, long sequence, Guid ship, SpacecraftEvaSlotState[] suits) =>
            Emit(peer, (Event: this, Peer: peer, Sequence: sequence, Ship: ship, Suits: suits), static (writer, state) =>
        {
            Head(writer, Kind.Eva); state.Event.Envelope(writer, state.Peer);
            writer.Write(state.Sequence); GuidValue(writer, state.Ship);
            foreach (var slot in state.Suits)
            {
                var suit = slot.Suit;
                if (!SpacecraftEvaSuit.ValidState(suit)) throw new InvalidDataException("Invalid EVA telemetry.");
                writer.Write((sbyte)slot.Seat); writer.Write(slot.Actor); GuidValue(writer, slot.Lease);
                writer.Write(suit.Pressure); writer.Write(suit.OxygenFraction); writer.Write(suit.CO2Fraction);
                writer.Write(suit.TemperatureC); writer.Write(suit.OxygenMoles); writer.Write(suit.BatteryKwh);
                writer.Write(suit.FilterMoles); writer.Write(suit.Powered);
            }
        });

        private static SpacecraftEvaSlotState[] ReadEva(BinaryReader reader, Guid ship)
        {
            var slots = new SpacecraftEvaSlotState[SpacecraftEvaSuit.Count];
            int takenSeats = 0;
            for (int index = 0; index < slots.Length; index++)
            {
                int seat = reader.ReadSByte();
                ulong actor = reader.ReadUInt64();
                var lease = GuidValue(reader);
                var suit = new SpacecraftEvaSuitState(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), Boolean(reader));
                if (seat is < -1 or > 2 || !SpacecraftEvaSuit.ValidState(suit)) throw new InvalidDataException();
                if (seat < 0)
                {
                    if (actor != 0 || lease != Guid.Empty) throw new InvalidDataException();
                }
                else
                {
                    if (ship == Guid.Empty || actor == 0 || lease == Guid.Empty || (takenSeats & (1 << seat)) != 0)
                        throw new InvalidDataException();
                    for (int previous = 0; previous < index; previous++)
                        if (slots[previous].Seat >= 0 && (slots[previous].Actor == actor || slots[previous].Lease == lease))
                            throw new InvalidDataException();
                    takenSeats |= 1 << seat;
                }
                slots[index] = new(seat, actor, lease, suit);
            }
            return slots;
        }
    }
}
