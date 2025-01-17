﻿using System.IO.Ports;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SharedDriver
{
    public class LogicAnalyzerDriver : IDisposable
    {
        Regex regAddressPort = new Regex("([0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+)\\:([0-9]+)");
        StreamReader readResponse;
        BinaryReader readData;
        Stream baseStream;
        SerialPort sp;
        TcpClient tcpClient;
        string devAddr;
        ushort devPort;

        public string? DeviceVersion { get; set; }
        public event EventHandler<CaptureEventArgs>? CaptureCompleted;

        bool capturing = false;
        private int channelCount;
        private int triggerChannel;
        private int preSamples;
        private Action<CaptureEventArgs>? currentCaptureHandler;

        public bool IsNetwork { get; private set; }

        public LogicAnalyzerDriver(string SerialPort, int Bauds)
        {
            sp = new SerialPort(SerialPort, Bauds);
            sp.RtsEnable = true;
            sp.DtrEnable = true;
            sp.NewLine = "\n";
            sp.ReadBufferSize = 1024 * 1024;
            sp.WriteBufferSize = 1024 * 1024;

            sp.Open();
            baseStream = sp.BaseStream;

            readResponse = new StreamReader(baseStream);
            readData = new BinaryReader(baseStream);

            OutputPacket pack = new OutputPacket();
            pack.AddByte(0);

            baseStream.Write(pack.Serialize());

            baseStream.ReadTimeout = 10000;
            DeviceVersion = readResponse.ReadLine();
            baseStream.ReadTimeout = Timeout.Infinite;
        }

        public LogicAnalyzerDriver(string AddressPort)
        {
            var match = regAddressPort.Match(AddressPort);

            if (match == null || !match.Success)
                throw new ArgumentException("Specified address/port is invalid");

            devAddr = match.Groups[1].Value;
            string port = match.Groups[2].Value;

            if(!ushort.TryParse(port, out devPort))
                throw new ArgumentException("Specified address/port is invalid");

            tcpClient = new TcpClient();

            tcpClient.Connect(devAddr, devPort);
            baseStream = tcpClient.GetStream();

            readResponse = new StreamReader(baseStream);
            readData = new BinaryReader(baseStream);

            OutputPacket pack = new OutputPacket();
            pack.AddByte(0);

            baseStream.Write(pack.Serialize());

            baseStream.ReadTimeout = 10000;
            DeviceVersion = readResponse.ReadLine();
            baseStream.ReadTimeout = Timeout.Infinite;

            IsNetwork = true;
        }

        public unsafe bool SendNetworkConfig(string AccesPointName, string Password, string IPAddress, ushort Port)
        {
            if(IsNetwork) 
                return false;

            NetConfig request = new NetConfig { Port = Port };
            byte[] name = Encoding.ASCII.GetBytes(AccesPointName);
            byte[] pass = Encoding.ASCII.GetBytes(Password);
            byte[] addr = Encoding.ASCII.GetBytes(IPAddress);
            
            Marshal.Copy(name, 0, new IntPtr(request.AccessPointName), name.Length);
            Marshal.Copy(pass, 0, new IntPtr(request.Password), pass.Length);
            Marshal.Copy(addr, 0, new IntPtr(request.IPAddress), addr.Length);

            OutputPacket pack = new OutputPacket();
            pack.AddByte(2);
            pack.AddStruct(request);

            baseStream.Write(pack.Serialize());
            baseStream.Flush();

            baseStream.ReadTimeout = 5000;
            var result = readResponse.ReadLine();
            baseStream.ReadTimeout = Timeout.Infinite;

            if (result == "SETTINGS_SAVED")
                return true;

            return false;
        }

        public CaptureError StartCapture(int Frequency, int PreSamples, int PostSamples, int[] Channels, int TriggerChannel, bool TriggerInverted, byte CaptureMode, Action<CaptureEventArgs>? CaptureCompletedHandler = null)
        {

            if (capturing)
                return CaptureError.Busy;

            if (Channels == null || Channels.Length == 0 || PreSamples < 2 || PostSamples < 512 || Frequency < 3100 || Frequency > 100000000)
                return CaptureError.BadParams;

            try
            {
                switch (CaptureMode)
                {
                    case 0:

                        if (PreSamples > 98303 || PostSamples > 131069 || PreSamples + PostSamples > 131071)
                            return CaptureError.BadParams;
                        break;

                    case 1:

                        if (PreSamples > 49151 || PostSamples > 65533 || PreSamples + PostSamples > 65535)
                            return CaptureError.BadParams;
                        break;

                    case 2:

                        if (PreSamples > 24576 || PostSamples > 32765 || PreSamples + PostSamples > 32767)
                            return CaptureError.BadParams;
                        break;
                }

                channelCount = Channels.Length;
                triggerChannel = Array.IndexOf(Channels, TriggerChannel);
                preSamples = PreSamples;
                currentCaptureHandler = CaptureCompletedHandler;

                CaptureRequest request = new CaptureRequest
                {
                    triggerType = 0,
                    trigger = (byte)TriggerChannel,
                    invertedOrCount = TriggerInverted ? (byte)1 : (byte)0,
                    channels = new byte[32],
                    channelCount = (byte)Channels.Length,
                    frequency = (uint)Frequency,
                    preSamples = (uint)PreSamples,
                    postSamples = (uint)PostSamples,
                    captureMode = CaptureMode
                };

                for (int buc = 0; buc < Channels.Length; buc++)
                    request.channels[buc] = (byte)Channels[buc];

                OutputPacket pack = new OutputPacket();
                pack.AddByte(1);
                pack.AddStruct(request);

                baseStream.Write(pack.Serialize());
                baseStream.Flush();

                baseStream.ReadTimeout = 10000;
                var result = readResponse.ReadLine();
                baseStream.ReadTimeout = Timeout.Infinite;

                if (result == "CAPTURE_STARTED")
                {
                    capturing = true;
                    Task.Run(() => ReadCapture(PreSamples + PostSamples, CaptureMode));
                    return CaptureError.None;
                }
                return CaptureError.HardwareError;
            }
            catch { return CaptureError.UnexpectedError; }
        }
        public CaptureError StartPatternCapture(int Frequency, int PreSamples, int PostSamples, int[] Channels, int TriggerChannel, int TriggerBitCount, UInt16 TriggerPattern, bool Fast, byte CaptureMode, Action<CaptureEventArgs>? CaptureCompletedHandler = null)
        {
            try
            {
                if (capturing)
                    return CaptureError.Busy;

                if (Channels == null || Channels.Length == 0 || PreSamples < 2 || PostSamples < 512 || Frequency < 3100 || Frequency > 100000000)
                    return CaptureError.BadParams;

                switch (CaptureMode)
                {
                    case 0:

                        if (PreSamples > 98303 || PostSamples > 131069 || PreSamples + PostSamples > 131071)
                            return CaptureError.BadParams;
                        break;

                    case 1:

                        if (PreSamples > 49151 || PostSamples > 65533 || PreSamples + PostSamples > 65535)
                            return CaptureError.BadParams;
                        break;

                    case 2:

                        if (PreSamples > 24576 || PostSamples > 32765 || PreSamples + PostSamples > 32767)
                            return CaptureError.BadParams;
                        break;
                }

                channelCount = Channels.Length;
                triggerChannel = Array.IndexOf(Channels, TriggerChannel);
                preSamples = PreSamples;
                currentCaptureHandler = CaptureCompletedHandler;

                CaptureRequest request = new CaptureRequest
                {
                    triggerType = (byte)(Fast ? 2 : 1),
                    trigger = (byte)TriggerChannel,
                    invertedOrCount = (byte)TriggerBitCount,
                    triggerValue = (UInt16)TriggerPattern,
                    channels = new byte[32],
                    channelCount = (byte)Channels.Length,
                    frequency = (uint)Frequency,
                    preSamples = (uint)PreSamples,
                    postSamples = (uint)PostSamples,
                    captureMode = CaptureMode
                };

                for (int buc = 0; buc < Channels.Length; buc++)
                    request.channels[buc] = (byte)Channels[buc];

                OutputPacket pack = new OutputPacket();
                pack.AddByte(1);
                pack.AddStruct(request);

                baseStream.Write(pack.Serialize());
                baseStream.Flush();

                baseStream.ReadTimeout = 10000;
                var result = readResponse.ReadLine();
                baseStream.ReadTimeout = Timeout.Infinite;

                if (result == "CAPTURE_STARTED")
                {
                    capturing = true;
                    Task.Run(() => ReadCapture(PreSamples + PostSamples, CaptureMode));
                    return CaptureError.None;
                }
                return CaptureError.HardwareError;
            }
            catch { return CaptureError.UnexpectedError; }
        }

        public bool StopCapture()
        {
            if (!capturing)
                return false;

            capturing = false;

            if (IsNetwork)
            {
                baseStream.WriteByte(0xff);
                baseStream.Flush();
                Thread.Sleep(1);
                tcpClient.Close();
                Thread.Sleep(1);
                tcpClient = new TcpClient();
                tcpClient.Connect(devAddr, devPort);
                baseStream = tcpClient.GetStream();
                readResponse = new StreamReader(baseStream);
                readData = new BinaryReader(baseStream);
            }
            else
            {

                sp.Write(new byte[] { 0xFF }, 0, 1);
                sp.BaseStream.Flush();
                Thread.Sleep(1);
                sp.Close();
                Thread.Sleep(1);
                sp.Open();
                baseStream = sp.BaseStream;
                readResponse = new StreamReader(baseStream);
                readData = new BinaryReader(baseStream);
            }

            return true;
        }

        public void Dispose()
        {
            try
            {
                sp.Close();
                sp.Dispose();
            }
            catch { }

            try
            {
                tcpClient.Close();
                tcpClient.Dispose();

            } catch { }

            try
            {
                baseStream.Close();
                baseStream.Dispose();
            }
            catch { }

            try
            {
                readData.Close();
                readData.Dispose();
            }
            catch { }

            try
            {
                readResponse.Close();
                readResponse.Dispose();
            }
            catch { }

            sp = null;
            baseStream = null;
            readData = null;
            readData = null;

            DeviceVersion = null;
            CaptureCompleted = null;
        }
        void ReadCapture(int Samples, byte Mode)
        {

            try
            {
                uint length = readData.ReadUInt32();
                uint[] samples = new uint[length];

                BinaryReader rdData;

                if (IsNetwork)
                    rdData = readData;
                else
                {
                    byte[] readBuffer = new byte[Samples * (Mode == 0 ? 1 : (Mode == 1 ? 2 : 4))];
                    int left = readBuffer.Length;
                    int pos = 0;

                    while (left > 0 && sp.IsOpen)
                    {
                        pos += sp.Read(readBuffer, pos, left);
                        left = readBuffer.Length - pos;
                    }

                    MemoryStream ms = new MemoryStream(readBuffer);
                    rdData = new BinaryReader(ms);
                }

                switch(Mode)
                {
                    case 0:
                        for (int buc = 0; buc < length; buc++)
                            samples[buc] = rdData.ReadByte();
                        break;
                    case 1:
                        for (int buc = 0; buc < length; buc++)
                            samples[buc] = rdData.ReadUInt16();
                        break;
                    case 2:
                        for (int buc = 0; buc < length; buc++)
                            samples[buc] = rdData.ReadUInt32();
                        break;
                }
                    
                if (currentCaptureHandler != null)
                    currentCaptureHandler(new CaptureEventArgs { Samples = samples, ChannelCount = channelCount, TriggerChannel = triggerChannel, PreSamples = preSamples });
                else if (CaptureCompleted != null)
                    CaptureCompleted(this, new CaptureEventArgs { Samples = samples, ChannelCount = channelCount, TriggerChannel = triggerChannel, PreSamples = preSamples });

                if (!IsNetwork)
                {
                    try
                    {
                        rdData.BaseStream.Close();
                        rdData.BaseStream.Dispose();
                    }
                    catch { }

                    try
                    {
                        rdData.Close();
                        rdData.Dispose();
                    }
                    catch { }
                }
                capturing = false;
            }
            catch(Exception ex) 
            {
                Console.WriteLine(ex.Message + " - " + ex.StackTrace);
            }
        }
        class OutputPacket
        {
            List<byte> dataBuffer = new List<byte>();

            public void AddByte(byte newByte)
            {
                dataBuffer.Add(newByte);
            }

            public void AddBytes(IEnumerable<byte> newBytes)
            {
                dataBuffer.AddRange(newBytes);
            }

            public void AddString(string newString)
            {
                dataBuffer.AddRange(Encoding.ASCII.GetBytes(newString));
            }

            public void AddStruct(object newStruct)
            {
                int rawSize = Marshal.SizeOf(newStruct);
                IntPtr buffer = Marshal.AllocHGlobal(rawSize);
                Marshal.StructureToPtr(newStruct, buffer, false);
                byte[] rawDatas = new byte[rawSize];
                Marshal.Copy(buffer, rawDatas, 0, rawSize);
                Marshal.FreeHGlobal(buffer);
                dataBuffer.AddRange(rawDatas);
            }

            public void Clear()
            {
                dataBuffer.Clear();
            }

            public byte[] Serialize()
            {
                List<byte> finalData = new List<byte>();
                finalData.Add(0x55);
                finalData.Add(0xAA);

                for (int buc = 0; buc < dataBuffer.Count; buc++)
                {
                    if (dataBuffer[buc] == 0xAA || dataBuffer[buc] == 0x55 || dataBuffer[buc] == 0xF0)
                    {
                        finalData.Add(0xF0);
                        finalData.Add((byte)(dataBuffer[buc] ^ 0xF0));
                    }
                    else
                        finalData.Add(dataBuffer[buc]);
                }


                finalData.Add(0xAA);
                finalData.Add(0x55);

                return finalData.ToArray();

            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CaptureRequest
        {
            public byte triggerType;
            public byte trigger;
            public byte invertedOrCount;
            public UInt16 triggerValue;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
            public byte[] channels;
            public byte channelCount;
            public UInt32 frequency;
            public UInt32 preSamples;
            public UInt32 postSamples;
            public byte captureMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        unsafe struct NetConfig
        {
            public fixed byte AccessPointName[33];
            public fixed byte Password[64];
            public fixed byte IPAddress[16];
            public UInt16 Port;
        }     
    }

    public enum CaptureError
    { 
        None,
        Busy,
        BadParams,
        HardwareError,
        UnexpectedError
    }

    public class CaptureEventArgs : EventArgs
    {
        public int TriggerChannel { get; set; }
        public int ChannelCount { get; set; }
        public int PreSamples { get; set; }
        public uint[] Samples { get; set; }
    }
}