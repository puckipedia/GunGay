using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace GunGay
{
    internal class OpcodeData
    {
        public MemoryStream Data { get; set; }
        private static ushort _packetSequence;

        public OpcodeData(byte opc, ushort x, ushort y, ushort w, ushort h)
        {
            Data = new MemoryStream();
            _w8(opc);
            _w8(0);
            _w16(++_packetSequence);
            _w16(x);
            _w16(y);
            _w16(w);
            _w16(h);
        }

        public void _write(byte[] data) { Data.Write(data, 0, data.Length); }
        public void _w8(byte b) => _write(new byte[] { b });
        public void _w16(ushort b) => _write(new byte[] { (byte)(b >> 8), (byte)(b & 0xFF) });
        public void _w32(uint b) => _write(new byte[] { (byte)(b >> 24), (byte)(b >> 16), (byte)(b >> 8), (byte)(b & 0xFF) });
        public void _wColor(uint b) => _write(new byte[] { 0, (byte)(b & 0xFF), (byte)(b >> 8), (byte)(b >> 16) });
        public void _wPad(uint off) { while (Data.Length % off != 0) _w8(0); }
    }

    public class RayConnection
    {
        private UdpClient _client;
        private string _host;
        private ushort _sequenceNumber;

        public delegate void RayEventHandler(Event @event);
        public event RayEventHandler OnEvent;

        public abstract class Event
        {

        }

        public class MouseEvent : Event
        {
            public ushort Buttons { get; set; }
            public ushort X { get; set; }
            public ushort Y { get; set; }
            public ushort Unknown { get; set; }
        }

        public class KeyboardEvent : Event
        {
            public ushort Shift { get; set; }
            public byte[] Keys { get; set; }
            public ushort Unknown1 { get; set; }
            public ushort Unknown2 { get; set; }
        }

        private MemoryStream _packetCache = new MemoryStream();
        private const int MAX_PACKET_SIZE = 1458;

        private async Task _write(byte[] data)
        {
            if (_packetCache.Length + data.Length > MAX_PACKET_SIZE)
            {
                await _endPacket();
                await _beginPacket(true);
            }

            await _packetCache.WriteAsync(data, 0, data.Length);
        }

        private Queue<Event> _events = new Queue<Event>();

        private async void _readLoop()
        {
            while (true)
            {
                var data = await _client.ReceiveAsync();

                var i = 16;
                var b = data.Buffer;
                while (i < b.Length)
                {
                    var opcode = b[i];
                    i += 1 + 4;

                    switch (opcode)
                    {
                        case 0xC2:
                            OnEvent?.Invoke(new MouseEvent
                            {
                                Buttons = (ushort)((b[i + 0] << 8) | b[i + 1]),
                                X = (ushort)((b[i + 2] << 8) | b[i + 3]),
                                Y = (ushort)((b[i + 4] << 8) | b[i + 5]),
                                Unknown = (ushort)((b[i + 0] << 6) | b[i + 7]),
                            });
                            i += 8;
                            break;
                        case 0xC1: // keyboard
                            OnEvent?.Invoke(new KeyboardEvent
                            {
                                Unknown1 = (ushort)((b[i + 0] << 8) | b[i + 1]),
                                Shift = (ushort)((b[i + 2] << 8) | b[i + 3]),
                                Keys = b.Skip(i + 4).Take(6).ToArray(),
                                Unknown2 = (ushort)((b[i + 10] << 8) | b[i + 11])
                            });
                            i += 12;
                            break;
                        case 0xC4:
                            i += 12;
                            // apparently, a NACK. first value, no idea. second and third are bounds that have to be ACKed
                            break;
                        case 0xC5:
                            i += 4;
                            break;
                        case 0xC6:
                            var len = (b[i + 0] << 8) | b[i + 1];
                            i += len + 2;
                            break;
                        case 0xC7:
                            i += 3 * 8;
                            break;
                        default:
                            i = b.Length;
                            break;
                    }
                }
            }
        }
        
        /// <summary>
        /// Changes the port that the server sends data to
        /// </summary>
        public void ChangePort(ushort port)
        {
            _client.Dispose();
            _client = new UdpClient(_host, port);
        }

        private async Task _w8(byte b) => await _write(new byte[] { b });
        private async Task _w16(ushort b) => await _write(new byte[] { (byte)(b >> 8), (byte)(b & 0xFF) });
        private async Task _w32(uint b) => await _write(new byte[] { (byte)(b >> 24), (byte)(b >> 16), (byte)(b >> 8), (byte)(b & 0xFF) });
        private async Task _wColor(uint b) => await _write(new byte[] { 0, (byte) (b & 0xFF), (byte)(b >> 8), (byte)(b >> 16) });

        private async Task _beginPacket(bool flagSet)
        {
            _packetCache.SetLength(0);

            await _w16(++_sequenceNumber);
            await _w16((ushort)(flagSet ? 1 : 0));
            await _w16(1);
            await _w16(0);
            await _w16(0xf);
            await _w16(0xa);
            await _w16(0x10);
            await _w16(0);
        }

        private async Task _send(OpcodeData opcode)
        {
            int toGo = (int) opcode.Data.Length;
            var data = opcode.Data.ToArray();
            int offset = 0;
            await _beginPacket(false);
            while (toGo > 0)
            {
                int nextBlob = toGo < (MAX_PACKET_SIZE - 16) ? toGo : (MAX_PACKET_SIZE - 16);
                await _packetCache.WriteAsync(data, offset, nextBlob);
                toGo -= nextBlob;
                offset += nextBlob;
                if (toGo != 0)
                    await _beginPacket(true);
            }
            await _endPacket();
        }

        public async Task Pad()
        {
            var opcode = new OpcodeData(0xAF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF);
            opcode._w32(0xFFFFFFFF);
            await _send(opcode);
        }

        /// <summary>
        /// fills the chosen rectangle with a color in 0x00RRGGBB format
        /// </summary>
        public async Task FillRect(ushort x, ushort y, ushort w, ushort h, uint color)
        {
            var opcode = new OpcodeData(0xA2, x, y, w, h);
            opcode._wColor(color);
            await _send(opcode);
        }

        /// <summary>
        /// Writes a 1-color bitmap, background transparent. Format is horizontal, then vertical, with rows padded out to full bytes.
        /// </summary>
        public async Task MaskedFill(ushort x, ushort y, ushort w, ushort h, uint color, byte[] bitmap)
        {
            var opcode = new OpcodeData(0xA3, x, y, w, h);
            opcode._wColor(color);
            opcode._write(bitmap);
            opcode._wPad(4);
            await _send(opcode);
        }

        /// <summary>
        /// Copies a specific rectangle from the screen to somewhere else on the screen.
        /// </summary>
        public async Task CopyRect(ushort x, ushort y, ushort w, ushort h, ushort from_x, ushort from_y)
        {
            var opcode = new OpcodeData(0xA4, x, y, w, h);
            opcode._w16(from_x);
            opcode._w16(from_y);
            await _send(opcode);
        }

        /// <summary>
        /// Writes a 2-color bitmap. Format is horizontal, then vertical, with rows padded out to full bytes.
        /// </summary>
        public async Task ExpandBitmap(ushort x, ushort y, ushort w, ushort h, uint bg, uint fg, byte[] bitmap)
        {
            Debug.Assert(bitmap.Length == (w + 7) / 8 * h);

            var opcode = new OpcodeData(0xA5, x, y, w, h);
            opcode._wColor(bg);
            opcode._wColor(fg);
            opcode._write(bitmap);
            opcode._wPad(4);
            await _send(opcode);
        }

        /// <summary>
        /// Blits a bitmap. Pixel order is BGR here!
        /// </summary>
        public async Task BlitBitmap(ushort x, ushort y, ushort w, ushort h, byte[] data)
        {
            var opcode = new OpcodeData(0xA6, x, y, w, h);
            
            opcode._write(data);
            opcode._wPad(4);

            await _send(opcode);
        }

        /// <summary>
        /// Does nothing for me.
        /// </summary>
        public async Task SetMouseBounds(ushort w, ushort h)
        {
            await _send(new OpcodeData(0xA8, 0, 0, w, h));
        }

        /// <summary>
        /// Sets the mouse cursor. Probably cbitmap switches between bg and fg, mbitmap is transparency?
        /// </summary>
        public async Task SetMouseCursor(ushort x, ushort y, ushort w, ushort h, uint bg, uint fg, byte[] cbitmap, byte[] mbitmap)
        {
            Debug.Assert(cbitmap.Length == (w * h) >> 4);
            Debug.Assert(mbitmap.Length == (w * h) >> 4);

            var opcode = new OpcodeData(0xA9, x, y, w, h);
            opcode._wColor(bg);
            opcode._wColor(fg);
            opcode._write(cbitmap);
            opcode._write(mbitmap);
            opcode._wPad(4);
            await _send(opcode);
        }

        /// <summary>
        /// Sets the position of the mouse on screen
        /// </summary>
        public async Task SetMousePosition(ushort x, ushort y)
        {
            await _send(new OpcodeData(0xAA, x, y, 0, 0));
        }

        private async Task _endPacket()
        {
            await _client.SendAsync(_packetCache.GetBuffer(), (int) _packetCache.Length);
            _packetCache.SetLength(0);
        }

        public RayConnection(string host, ushort port)
        {
            _host = host;
            _client = new UdpClient(host, port);
            _readLoop();
        }
    }
}
