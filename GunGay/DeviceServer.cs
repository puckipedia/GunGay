using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GunGay
{
    public class DeviceServer
    {
        private TcpListener _listener;

        public DeviceServer(ushort port = 7011)
        {
            _listener = new TcpListener(IPAddress.Any, port);
        }

        private async void _do(TcpClient client)
        {
            var r = new StreamReader(client.GetStream());
            var w = new StreamWriter(client.GetStream());

            while (true)
            {
                var line = await r.ReadLineAsync();
                if (line == null && client.Connected) client.Close();
                if (!client.Connected) return;

                if (line.StartsWith("connect"))
                {
                    await w.WriteAsync("beat.t=c4e9d8\n");
                    await w.FlushAsync();
                }
            }
        }

        public async void Go()
        {
            _listener.Start(5);
            while (true)
            {
                _do(await _listener.AcceptTcpClientAsync());
            }
        }
    }
}
