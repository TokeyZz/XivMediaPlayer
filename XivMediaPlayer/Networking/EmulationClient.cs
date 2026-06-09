using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace XivMediaPlayer.Networking
{
    public class EmulationClient : IDisposable
    {
        private readonly UdpClient _udpClient;
        private readonly IPEndPoint _remoteEndPoint;
        private readonly byte[] _sessionBytes;
        private bool _disposed;
        private ControllerService _controllerService;

        public string IpAddress { get; }
        public string SessionId { get; }

        public EmulationClient(string ip, string sessionId)
        {
            IpAddress = ip;
            SessionId = sessionId;

            try { _sessionBytes = Convert.FromHexString(sessionId); }
            catch { _sessionBytes = new byte[4]; }

            _udpClient = new UdpClient();
            _remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), 50051);

            _controllerService = new ControllerService(ip, sessionId);
            _controllerService.Start();
        }

        public static async Task<string> GetRtspUrlAsync(string ip, string sessionId)
        {
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var response = await http.GetStringAsync($"http://{ip}:8080/streaminfo/");
                var json = JsonDocument.Parse(response);
                if (json.RootElement.TryGetProperty("rtspUrl", out var urlElement))
                {
                    return urlElement.GetString() ?? "";
                }
            }
            catch { }
            return "";
        }

        public void SendGamepadState(byte playerIndex, ushort buttons, byte lt, byte rt, sbyte lx, sbyte ly, sbyte rx, sbyte ry)
        {
            if (_disposed) return;

            byte[] packet = new byte[17];
            packet[0] = (byte)'E';
            packet[1] = (byte)'M';
            packet[2] = (byte)'U';
            packet[3] = (byte)'L';
            
            packet[4] = _sessionBytes[0];
            packet[5] = _sessionBytes[1];
            packet[6] = _sessionBytes[2];
            packet[7] = _sessionBytes[3];
            
            packet[8] = playerIndex; // 0-3 for gamepad

            // Gamepad payload
            byte[] btnBytes = BitConverter.GetBytes(buttons);
            packet[9] = btnBytes[0];
            packet[10] = btnBytes[1];
            packet[11] = lt;
            packet[12] = rt;
            packet[13] = unchecked((byte)lx);
            packet[14] = unchecked((byte)ly);
            packet[15] = unchecked((byte)rx);
            packet[16] = unchecked((byte)ry);

            try
            {
                _udpClient.Send(packet, packet.Length, _remoteEndPoint);
            }
            catch { }
        }

        public void SendMouseState(byte x, byte y, bool lmb, bool rmb)
        {
            if (_disposed) return;

            byte[] packet = new byte[17];
            packet[0] = (byte)'E';
            packet[1] = (byte)'M';
            packet[2] = (byte)'U';
            packet[3] = (byte)'L';
            
            packet[4] = _sessionBytes[0];
            packet[5] = _sessionBytes[1];
            packet[6] = _sessionBytes[2];
            packet[7] = _sessionBytes[3];
            
            packet[8] = 4; // 4 is Mouse

            // Payload: data[4] = X, data[5] = Y, data[7] = buttons
            // Inside packet: bytes 9-16 correspond to data[0-7]
            packet[9 + 4] = x;
            packet[9 + 5] = y;

            byte btnMask = 0;
            if (lmb) btnMask |= 0x01;
            if (rmb) btnMask |= 0x02;
            
            packet[9 + 7] = btnMask;

            try
            {
                _udpClient.Send(packet, packet.Length, _remoteEndPoint);
            }
            catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            _controllerService?.Dispose();
            _udpClient?.Dispose();
        }
    }
}
