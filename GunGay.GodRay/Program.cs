using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace GunGay.GodRay
{
    class Program
    {
        static void Main(string[] args)
        {
            (new DeviceServer()).Go();

            var cs = new ConnectionServer(7009);
            cs.OnClientConnect += Cs_OnClientConnect;
            cs.Start();
            while (true)
            {
                Thread.Sleep(-1);
            }
        }

        private static void HsvToRgb(double h, double S, double v, byte[] outArr, int off)
        {
            h %= 360;
            double hf = h / 60.0;
            int i = (int)Math.Floor(hf);
            double f = hf - i;
            byte V = (byte)(v * 256);
            byte pv = (byte)(v * (1 - S) * 256);
            byte qv = (byte)(v * (1 - S * f) * 256);
            byte tv = (byte)(v * (1 - S * (1 - f)));

            byte R, G, B;
            B = G = R = 0;
            switch (i)
            {
                case 0:
                    R = V;
                    G = tv;
                    B = pv;
                    break;
                case 1:
                    R = qv;
                    G = V;
                    B = pv;
                    break;
                case 2:
                    R = pv;
                    G = V;
                    B = tv;
                    break;
                case 3:
                    R = pv;
                    G = qv;
                    B = V;
                    break;
                case 4:
                    R = tv;
                    G = pv;
                    B = V;
                    break;
                case 5:
                    R = V;
                    G = pv;
                    B = qv;
                    break;
            }

            outArr[off] = B;
            outArr[off + 1] = G;
            outArr[off + 2] = R;
        }

        private static float t = 0;

        private static void _pixel(int x, int y, byte[] arr, int off)
        {
            x -= (int) (Math.Sin(t / 100) * 640) + 640;
            y -= (int) (Math.Cos(t / 100) * 512) + 512;

            var distance = Math.Sqrt(x * x + y * y) + t;

            HsvToRgb(distance, 0.5, 1, arr, off);
        }

        private static async Task _chunk(ushort x, ushort y, RayConnection conn)
        {
            byte[] buf = new byte[16 * 16 * 3];
            for (int i = 0; i < 16; i++)
            {
                for (int j = 0; j < 16; j++)
                {
                    _pixel(x + i, y + j, buf, ((j * 16) + i) * 3);
                }
            }

            await conn.BlitBitmap(x, y, 16, 16, buf);
        }

        private static async void Cs_OnClientConnect(ConnectionServer.ConnectionServerClient client)
        {
            Console.WriteLine($"Got a connection from {client.Hardware} (card/device ID) {client.CardId} at {client.StartResolution}");
            ushort port = await client.AllowAccess(); // this is needed to stop future notifications
            if (client.CardType == "T0unknown")
            {
                Console.WriteLine("have ATR: " + client.ATR);
            }


            var conn = new RayConnection(client.ActualIP.ToString(), port);
            var pdr = await client.SendAPDU(0x00, 0x10, new byte[] { 0x00, 0x80 }, new byte[] { }, 0);

            await conn.FillRect(0, 0, 1280, 1024, 0xFFFFFF);
            await conn.ExpandBitmap(0, 0, 8, 255, 0x00FF00, 0x0000FF, Enumerable.Range(0, 0xFF).Select(a => (byte)a).ToArray());

            conn.OnEvent += async (e) =>
            {
                if (e is RayConnection.KeyboardEvent ke)
                {
                    if (ke.Keys.Any(a => a != 0))
                    Console.WriteLine(new string(ke.Keys.Where(a => a != 0).Select(a => (char)a).ToArray()));
                }
                else if (e is RayConnection.MouseEvent me)
                {
                    await conn.FillRect(me.X, me.Y, 4, 4, 0xFF00000);
                }
            };

            client.OnCardInsert += async () =>
            {
                if (client.CardType == "pseudo")
                {
                    // no card inserted!
                    await conn.FillRect(0, 0, 1280, 1024, 0xFF0000);
                }
                else if (client.CardType == "T0unknown")
                {
                    // non-sun ray smartcard (who has those???) inserted
                    if (client.ATR == "3b6700002920006f789000")
                        await conn.FillRect(0, 0, 1280, 1024, 0x00FF00);
                    else if (client.ATR == "3b6800000073c84000009000")
                        await conn.FillRect(0, 0, 1280, 1024, 0x0000FF);
                }
            };

            var rng = new Random();
            while (true)
            {
                for (int x = 0; x < 2000; x++)
                {
                    await _chunk((ushort)rng.Next(1280), (ushort)rng.Next(1024), conn);
                }

                await Task.Delay(1000 / 30);
                t += 5;
            }
        }
    }
}
