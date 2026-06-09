using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;

namespace XivMediaPlayer.Networking
{
    public class ControllerState
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public sbyte LeftStickX;
        public sbyte LeftStickY;
        public sbyte RightStickX;
        public sbyte RightStickY;

        public void Reset()
        {
            Buttons = 0;
            LeftTrigger = 0;
            RightTrigger = 0;
            LeftStickX = 0;
            LeftStickY = 0;
            RightStickX = 0;
            RightStickY = 0;
        }

        public void Merge(ControllerState other)
        {
            this.Buttons |= other.Buttons;
            this.LeftTrigger = Math.Max(this.LeftTrigger, other.LeftTrigger);
            this.RightTrigger = Math.Max(this.RightTrigger, other.RightTrigger);
            
            this.LeftStickX = MergeAxis(this.LeftStickX, other.LeftStickX);
            this.LeftStickY = MergeAxis(this.LeftStickY, other.LeftStickY);
            this.RightStickX = MergeAxis(this.RightStickX, other.RightStickX);
            this.RightStickY = MergeAxis(this.RightStickY, other.RightStickY);
        }

        private sbyte MergeAxis(sbyte a, sbyte b)
        {
            if (Math.Abs((int)a) > Math.Abs((int)b)) return a;
            return b;
        }

        public byte[] ToPacket(byte ctrlIdx, byte[] sessionBytes)
        {
            byte[] packet = new byte[17];
            packet[0] = (byte)'E';
            packet[1] = (byte)'M';
            packet[2] = (byte)'U';
            packet[3] = (byte)'L';
            
            Array.Copy(sessionBytes, 0, packet, 4, 4);
            packet[8] = ctrlIdx;

            byte[] btnBytes = BitConverter.GetBytes(Buttons);
            packet[9] = btnBytes[0];
            packet[10] = btnBytes[1];
            packet[11] = LeftTrigger;
            packet[12] = RightTrigger;
            packet[13] = unchecked((byte)LeftStickX);
            packet[14] = unchecked((byte)LeftStickY);
            packet[15] = unchecked((byte)RightStickX);
            packet[16] = unchecked((byte)RightStickY);
            return packet;
        }
    }

    public class ControllerService : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll")]
        public static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

        private bool _running = true;
        private byte _remoteCtrlIdx = 0;
        private byte[] _sessionBytes;
        private UdpClient _udpClient;
        private IPEndPoint _remoteEndPoint;

        private ControllerState[] _xinputStates = new ControllerState[4];
        private Dictionary<string, ControllerState> _dsStates = new Dictionary<string, ControllerState>();
        private object _stateLock = new object();

        public ControllerService(string ip, string sessionId)
        {
            try { _sessionBytes = Convert.FromHexString(sessionId); }
            catch { _sessionBytes = new byte[4]; }

            _remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), 50051);
            _udpClient = new UdpClient();

            for (int i = 0; i < 4; i++) _xinputStates[i] = new ControllerState();
        }

        public void Start()
        {
            Thread mainLoop = new Thread(SenderLoop);
            mainLoop.IsBackground = true;
            mainLoop.Start();

            Thread xinputThread = new Thread(XInputLoop);
            xinputThread.IsBackground = true;
            xinputThread.Start();

            Thread dsThread = new Thread(DualSenseLoop);
            dsThread.IsBackground = true;
            dsThread.Start();
        }

        public void Stop()
        {
            _running = false;
        }

        private void SenderLoop()
        {
            while (_running)
            {
                var merged = new ControllerState();
                lock (_stateLock)
                {
                    foreach (var s in _xinputStates) merged.Merge(s);
                    foreach (var s in _dsStates.Values) merged.Merge(s);
                }

                byte[] packet = merged.ToPacket(_remoteCtrlIdx, _sessionBytes);
                try { _udpClient.Send(packet, packet.Length, _remoteEndPoint); }
                catch { }

                Thread.Sleep(8); // ~120Hz transmit rate
            }
        }

        private void XInputLoop()
        {
            while (_running)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (XInputGetState(i, out XINPUT_STATE state) == 0)
                    {
                        var cs = new ControllerState();
                        cs.Buttons = state.Gamepad.wButtons;
                        cs.LeftTrigger = state.Gamepad.bLeftTrigger;
                        cs.RightTrigger = state.Gamepad.bRightTrigger;
                        cs.LeftStickX = (sbyte)(state.Gamepad.sThumbLX / 256);
                        cs.LeftStickY = (sbyte)(state.Gamepad.sThumbLY / 256);
                        cs.RightStickX = (sbyte)(state.Gamepad.sThumbRX / 256);
                        cs.RightStickY = (sbyte)(state.Gamepad.sThumbRY / 256);

                        lock (_stateLock) { _xinputStates[i] = cs; }
                    }
                    else
                    {
                        lock (_stateLock) { _xinputStates[i].Reset(); }
                    }
                }
                Thread.Sleep(10);
            }
        }

        private void DualSenseLoop()
        {
            while (_running)
            {
                try
                {
                    var loader = DeviceList.Local;
                    // Standard: 0x0CE6, Edge: 0x0DF2
                    var devices = loader.GetHidDevices(0x054C).Where(d => d.ProductID == 0x0CE6 || d.ProductID == 0x0DF2).ToList();
                    
                    foreach (var device in devices)
                    {
                        string path = device.DevicePath;
                        if (!_dsStates.ContainsKey(path))
                        {
                            Thread deviceThread = new Thread(() => PollDualSense(device));
                            deviceThread.IsBackground = true;
                            deviceThread.Start();
                        }
                    }
                }
                catch { }

                Thread.Sleep(2000); // Check for new devices occasionally
            }
        }

        private void PollDualSense(HidDevice device)
        {
            string path = device.DevicePath;
            lock (_stateLock) { _dsStates[path] = new ControllerState(); }

            try
            {
                using (var stream = device.Open())
                {
                    byte[] buffer = new byte[64];
                    while (_running)
                    {
                        int length = stream.Read(buffer);
                        if (length > 0 && buffer[0] == 0x01)
                        {
                            var cs = new ControllerState();
                            cs.LeftStickX = (sbyte)(buffer[1] - 128);
                            cs.LeftStickY = (sbyte)Math.Clamp(128 - buffer[2], -128, 127);
                            cs.RightStickX = (sbyte)(buffer[3] - 128);
                            cs.RightStickY = (sbyte)Math.Clamp(128 - buffer[4], -128, 127);
                            cs.LeftTrigger = buffer[5];
                            cs.RightTrigger = buffer[6];

                            byte dpad = (byte)(buffer[8] & 0x0F);
                            if (dpad == 0 || dpad == 1 || dpad == 7) cs.Buttons |= 0x0400; // Up
                            if (dpad == 3 || dpad == 4 || dpad == 5) cs.Buttons |= 0x0800; // Down
                            if (dpad == 5 || dpad == 6 || dpad == 7) cs.Buttons |= 0x1000; // Left
                            if (dpad == 1 || dpad == 2 || dpad == 3) cs.Buttons |= 0x2000; // Right

                            if ((buffer[8] & 0x10) != 0) cs.Buttons |= 0x0004; // Square/X
                            if ((buffer[8] & 0x20) != 0) cs.Buttons |= 0x0001; // Cross/A
                            if ((buffer[8] & 0x40) != 0) cs.Buttons |= 0x0002; // Circle/B
                            if ((buffer[8] & 0x80) != 0) cs.Buttons |= 0x0008; // Triangle/Y

                            if ((buffer[9] & 0x01) != 0) cs.Buttons |= 0x0010; // L1/LB
                            if ((buffer[9] & 0x02) != 0) cs.Buttons |= 0x0020; // R1/RB
                            if ((buffer[9] & 0x10) != 0) cs.Buttons |= 0x0200; // Share/Back
                            if ((buffer[9] & 0x20) != 0) cs.Buttons |= 0x0100; // Options/Start
                            if ((buffer[9] & 0x40) != 0) cs.Buttons |= 0x0040; // L3/LS
                            if ((buffer[9] & 0x80) != 0) cs.Buttons |= 0x0080; // R3/RS

                            lock (_stateLock) { _dsStates[path] = cs; }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                lock (_stateLock) { _dsStates.Remove(path); }
            }
        }

        public void Dispose()
        {
            Stop();
            _udpClient?.Dispose();
        }
    }
}
