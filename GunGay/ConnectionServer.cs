using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace GunGay
{
    /// <summary>
    /// A server replacing the work that would normally be done by utauthd.
    /// </summary>
    public class ConnectionServer
    {
        private struct Message
        {
            public string Type { get; set; }
            public Dictionary<string,string> Data { get; set; }
        }

        /// <summary>
        /// Event that gets called when a new client connects
        /// </summary>
        public delegate void ClientConnectEvent(ConnectionServerClient client);

        /// <summary>
        /// Event that gets called when a smartcard is inserted/removed
        /// </summary>
        public delegate Task ClientSmartcardEvent();

        public event ClientConnectEvent OnClientConnect;

        public class ConnectionServerClient
        {
            private TcpClient _client;
            private StreamReader _reader;
            private StreamWriter _writer;
            private ConnectionServer _server;

            public IPAddress ActualIP { get; private set; }
            public string Firmware { get; private set; }
            public string Hardware { get; private set; }
            public string StartResolution { get; set; }
            public int Port { get; set; }
            public string SerialNumber { get; set; }

            public string CardType { get; set; }
            public string CardId { get; set; }
            public string ATR { get; set; }
            public int ATRHistoricLength { get; set; }

            /// <summary>
            /// First historic byte
            /// </summary>
            public string ATRHS { get; set; }

            public event ClientSmartcardEvent OnCardInsert;
            public event ClientSmartcardEvent OnCardRemove;

            private int TokenSequence { get; set; }

            private bool _isFirst = true;

            private void _parse(Message msg)
            {
                if (msg.Data.ContainsKey("fw")) Firmware = msg.Data["fw"];
                if (msg.Data.ContainsKey("hw")) Hardware = msg.Data["hw"];
                if (msg.Data.ContainsKey("id")) CardId = msg.Data["id"];
                if (msg.Data.ContainsKey("sn")) SerialNumber = msg.Data["sn"];

                if (msg.Data.ContainsKey("startRes")) StartResolution = msg.Data["startRes"];
                if (msg.Data.ContainsKey("type")) CardType = msg.Data["type"];
                if (msg.Data.ContainsKey("pn")) Port = int.Parse(msg.Data["pn"]);
                if (msg.Data.ContainsKey("tokenSeq")) TokenSequence = int.Parse(msg.Data["tokenSeq"]);
                if (msg.Data.ContainsKey("realIP"))
                {
                    ActualIP = IPAddress.Parse("0x" + msg.Data["realIP"]);
                }

                if (msg.Data.ContainsKey("atr"))
                {
                    ATR = msg.Data["atr"];
                    ATRHistoricLength = int.Parse(msg.Data["atr.hist_len"]);
                    ATRHS = msg.Data["atr.hs"];
                }
            }

            internal ConnectionServerClient(ConnectionServer server, TcpClient client)
            {
                _server = server;
                _client = client;
                var stream = client.GetStream();

                _reader = new StreamReader(stream);
                _writer = new StreamWriter(stream);
                _writer.AutoFlush = true;
            }

            private async Task<Message> _readMessage()
            {
                var data = await _reader.ReadLineAsync();
                Console.WriteLine(data);
                var line = data.Split(' ');

                var msg = new Message()
                {
                    Type = line[0],
                    Data = new Dictionary<string, string>()
                };

                foreach (var token in line.Skip(1))
                {
                    var eq = token.IndexOf('=');
                    var key = token.Substring(0, eq);
                    var value = token.Substring(eq + 1);
                    msg.Data[key] = value;
                }

                return msg;
            }

            private async Task _writeMessage(Message msg)
            {
                await _writer.WriteLineAsync($"{((msg.Type ?? "") + " ").TrimStart()}{(msg.Data != null ? string.Join(" ", msg.Data.Select(a => $"{a.Key}={a.Value}")) : "")}");
            }

            private TaskCompletionSource<Message> _connectionResponse;
            private TaskCompletionSource<Message> _disconnectionResponse;

            private Dictionary<string, TaskCompletionSource<Message>> _smartcardInfo = new Dictionary<string, TaskCompletionSource<Message>>();
            private int _tagCounter;

            public struct APDUResponse
            {
                public byte[] Data { get; set; }
                public byte SW1 { get; set; }
                public byte SW2 { get; set; }
                public int ReceivedLength { get; set; }
            }

            /// <summary>
            /// Sends an APDU to the smartcard.
            /// </summary>
            public async Task<APDUResponse?> SendAPDU(byte cla, byte ins, byte[] p1p2, byte[] data, int expectedLength)
            {
                var tag = "gungay:" + (++_tagCounter).ToString();
                var msg = new Message
                {
                    Type = "controlSmartCard",
                    Data = new Dictionary<string, string>
                    {
                        ["tag"] = tag,
                        ["command"] = "apduInf",
                        ["head"] = $"{cla:X2}{ins:X2}{p1p2[0]:X2}{p1p2[1]:X2}",
                        ["tdata"] = string.Join("", data.Select(a => a.ToString("X2"))),
                        ["rlen"] = expectedLength.ToString()
                    }
                };
                _smartcardInfo[tag] = new TaskCompletionSource<Message>();

                await _writeMessage(msg);
                var result = await _smartcardInfo[tag].Task;
                _smartcardInfo.Remove(tag);

                if (result.Data["apdu_result"] != "1") return null;

                var apduData = new List<byte>();
                for (int i = 0; i < result.Data["rec_data"].Length; i += 2)
                    apduData.Add(Convert.ToByte(result.Data["rec_data"].Substring(i, 2), 16));

                return new APDUResponse {
                    Data = apduData.ToArray(),
                    SW1 = Convert.ToByte(result.Data["status"].Substring(0, 2), 16),
                    SW2 = Convert.ToByte(result.Data["status"].Substring(2, 2), 16),
                    ReceivedLength = int.Parse(result.Data["rec_len"])
                };
            }

            private async void _keepalive()
            {
                while (true)
                {
                    await Task.Delay(1000);

                    if (!_client.Connected) break;
                    await _writeMessage(new Message { Type = "keepAliveInf" });
                }
            }

            internal async void Do()
            {
                try
                {
                    await Task.Yield();
                    while (true)
                    {
                        if (!_client.Connected) break;

                        var msg = await _readMessage();
                        _parse(msg);

                        if (msg.Type == "infoReq")
                        {
                            if (_isFirst)
                            {
                                _server.OnClientConnect?.Invoke(this);
                                _keepalive();
                            }
                            _isFirst = false;

                            if (msg.Data["event"] == "insert" && OnCardInsert != null)
                                await OnCardInsert.Invoke();
                            else if (msg.Data["event"] == "remove" && OnCardRemove != null)
                                await OnCardRemove.Invoke();
                        }
                        else if (msg.Type == "keepAliveReq")
                            await _writeMessage(new Message { Type = "keepAliveInf", Data = new Dictionary<string, string>() });
                        else if (msg.Type == "connRsp")
                        {
                            if (_connectionResponse != null) _connectionResponse.SetResult(msg);
                        }
                        else if (msg.Type == "discRsp")
                        {
                            if (_disconnectionResponse != null) _disconnectionResponse.SetResult(msg);
                        }
                        else if (msg.Type == "infoSmartCard")
                        {
                            if (msg.Data.ContainsKey("tag") && _smartcardInfo.TryGetValue(msg.Data["tag"], out var msgs))
                                msgs.SetResult(msg);
                        }
                    }
                } catch (IOException)
                {
                    // meh
                }
            }

            /// <summary>
            /// Tells the Sun Ray that it may connect.
            /// </summary>
            /// <returns>
            /// The UDP port the Ray is listening on.
            /// </returns>
            public async Task<ushort> AllowAccess(string module = "GunGay")
            {
                _connectionResponse = new TaskCompletionSource<Message>();

                await _writeMessage(new Message
                {
                    Type = "connInf",
                    Data = new Dictionary<string, string>
                    {
                        ["tokenSeq"] = TokenSequence.ToString(),
                        ["module"] = module,
                        ["access"] = "allowed"
                    }
                });

                var msg = await _connectionResponse.Task;
                return ushort.Parse(msg.Data["pn"]);
            }

            /// <summary>
            /// Redirects the Sun Ray to another authentication server.
            /// </summary>
            public async Task Redirect(IPAddress authip, ushort authport)
            {
                var parsed = string.Join("", authip.MapToIPv4().GetAddressBytes().Select(a => a.ToString("X2")));
                await _writeMessage(new Message
                {
                    Type = "redirectInf",
                    Data = new Dictionary<string, string>
                    {
                        ["authipa"] = parsed,
                        ["authport"] = authport.ToString()
                    }
                });

                _client.Close();
            }

            /// <summary>
            /// Tells the Sun Ray that it may not be connected anymore.
            /// </summary>
            public async Task<ushort> Disconnect(string reason = "shrug")
            {
                _disconnectionResponse = new TaskCompletionSource<Message>();

                await _writeMessage(new Message
                {
                    Type = "discInf",
                    Data = new Dictionary<string, string>
                    {
                        ["tokenSeq"] = TokenSequence.ToString(),
                        ["cause"] = reason,
                        ["access"] = "denied"
                    }
                });
                var msg = await _disconnectionResponse.Task;
                return ushort.Parse(msg.Data["pn"]);
            }
        }

        private TcpListener _listener;
        public ConnectionServer(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
        }

        public async void Start()
        {
            _listener.Start(5);
            await Task.Yield();

            while (true)
            {
                var sock = await _listener.AcceptTcpClientAsync();
                var client = new ConnectionServerClient(this, sock);
                client.Do();
            }
        }
    }
}
