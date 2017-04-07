using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace netmsg
{
    class Program
    {
        const int port = 34512;
        static void Main( string[] args )
        {
            if ( args.Length != 1 || args[0].Trim() == "" )
            {
                Console.WriteLine("Usage: netmsg <username>");
                return;
            }
            Console.WriteLine("send command syntax: \"send username message contents\"");
            Console.WriteLine("you do not need to put quotes around your message");
            string hostName = Dns.GetHostName();
            IPHostEntry hostEntry = Dns.GetHostEntry(hostName);
            IPAddress ipv4 = null;
            foreach ( IPAddress addr in hostEntry.AddressList )
            {
                if ( addr.AddressFamily == AddressFamily.InterNetwork )
                {
                    ipv4 = addr;
                    break;
                }
            }
            if ( ipv4 == null )
            {
                Console.WriteLine("Problem obtaining ip address");
                return;
            }
            Listener listener = new Listener(ipv4, port, args[0]);
            listener.Start();
            bool run = true;
            while ( run )
            {
                string input = Console.ReadLine();
                string[] splitInput = input.Split(' ');
                string command = splitInput[0];
                if ( command.ToLower() == "exit" )
                {
                    listener.Close();
                    run = false;
                }
                else if ( command.ToLower() == "send" )
                {
                    if ( splitInput.Length < 3 )
                    {
                        Console.WriteLine("Invalid send command");
                        continue;
                    }
                    string username = splitInput[1];
                    string[] messageArray = new string[splitInput.Length - 2];
                    for ( int i = 2; i < splitInput.Length; i++ )
                    {
                        messageArray[i - 2] = splitInput[i];
                    }
                    string message = string.Join(" ", messageArray);
                    Broadcaster broadcaster = new Broadcaster(ipv4, port);
                    Sender sender = broadcaster.Broadcast(username);
                    if ( sender == null )
                    {
                        continue;
                    }
                    sender.Send(string.Format("{0}: {1}", username, message));
                }
                else
                {
                    Console.WriteLine("Invalid command");
                }
            }
        }
    }

    class Listener
    {
        private IPAddress _address;
        private int _port;
        private string _name;
        private UdpClient _udplistener;
        private TcpListener _tcplistener;
        private bool _run = true;
        private byte[] _response;

        public Listener( IPAddress address, int port, string name )
        {
            _address = address;
            _port = port;
            _name = name;
            _udplistener = new UdpClient(_port);
            _tcplistener = new TcpListener(_address, _port);
            _response = _address.GetAddressBytes();
        }

        public void Start()
        {
            ListenForUdp();
            _tcplistener.Start();
            ListenForTcp();
            Console.WriteLine("Listening for messages");
        }

        private async void ListenForUdp()
        {
            while ( _run )
            {
                try
                {
                    UdpReceiveResult incomingBroadcast = await _udplistener.ReceiveAsync();
                    string broadcastMessage = Encoding.UTF8.GetString(incomingBroadcast.Buffer);
                    if ( broadcastMessage == _name )
                    {
                        _udplistener.Send(_response, _response.Length, incomingBroadcast.RemoteEndPoint);
                    }
                }
                catch ( Exception e )
                {
                    Console.WriteLine(e);
                }
            }
        }

        private async void ListenForTcp()
        {
            while ( _run )
            {
                try
                {
                    TcpClient client = await _tcplistener.AcceptTcpClientAsync();
                    NetworkStream stream = client.GetStream();
                    byte[] sizebuffer = new byte[4];
                    await stream.ReadAsync(sizebuffer, 0, sizebuffer.Length);
                    byte[] messagebuffer = new byte[BitConverter.ToInt32(sizebuffer, 0)];
                    await stream.ReadAsync(messagebuffer, 0, messagebuffer.Length);
                    Console.WriteLine(Encoding.UTF8.GetString(messagebuffer));
                    stream.Close();
                    client.Close();
                }
                catch ( Exception e )
                {
                    Console.WriteLine(e);
                }
            }
        }

        public void Close()
        {
            _udplistener.Close();
            _tcplistener.Stop();
            _run = false;
        }
    }

    class Broadcaster
    {
        private IPAddress _hostaddr;
        private IPAddress _broadcastaddr;
        private int _port;

        public Broadcaster( IPAddress hostaddr, int port )
        {
            _hostaddr = hostaddr;
            _port = port;
        }

        public Sender Broadcast( string username )
        {
            GetBroadcastAddress();
            Socket udpSender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPEndPoint broadcastEndpoint = new IPEndPoint(_broadcastaddr, _port);
            udpSender.SendTo(Encoding.UTF8.GetBytes(username), broadcastEndpoint);
            udpSender.ReceiveTimeout = 1000; // wait 1000 milliseconds before timeout
            try
            {
                byte[] response = new byte[4]; // should only be receiving 4 bytes for ip address
                udpSender.Receive(response);
                udpSender.Close();
                IPAddress userAddr = new IPAddress(response);
                return new Sender(userAddr, _port);
            }
            catch
            {
                Console.WriteLine("No response received");
                return null;
            }
        }

        private void GetBroadcastAddress()
        {
            try
            {
                List<IPAddress> addresses = new List<IPAddress>(GetAddresses());
                _broadcastaddr = CalculateBroadcast(addresses[0], addresses[1]);
            }
            catch ( Exception e )
            {
                Console.WriteLine(e);
            }
        }

        private IEnumerable<IPAddress> GetAddresses()
        {
            List<NetworkInterface> netInterfaceList = new List<NetworkInterface>(GetValidInterfaces());
            foreach ( NetworkInterface i in netInterfaceList )
            {
                IPInterfaceProperties properties = i.GetIPProperties();
                foreach ( UnicastIPAddressInformation unicast in properties.UnicastAddresses )
                {
                    if ( unicast.Address.AddressFamily == AddressFamily.InterNetwork )
                    {
                        if ( unicast.Address.Equals(_hostaddr) )
                        {
                            yield return unicast.Address;
                            yield return unicast.IPv4Mask;
                        }
                    }
                }
            }
        }

        private IEnumerable<NetworkInterface> GetValidInterfaces()
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach ( NetworkInterface i in interfaces )
            {
                if ( i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback && i.NetworkInterfaceType != NetworkInterfaceType.Tunnel )
                {
                    yield return i;
                }
            }
        }

        private IPAddress CalculateBroadcast( IPAddress address, IPAddress subnet )
        {
            byte[] addressBytes = address.GetAddressBytes();
            byte[] subnetBytes = subnet.GetAddressBytes();
            if ( addressBytes.Length != subnetBytes.Length )
            {
                throw new Exception("Address and Mask lengths are not equal");
            }
            byte[] broadcastBytes = new byte[addressBytes.Length];
            for ( int i = 0; i < addressBytes.Length; i++ )
            {
                broadcastBytes[i] = (byte)(addressBytes[i] | (subnetBytes[i] ^ 255)); // here's where the magic happens.
            }
            return new IPAddress(broadcastBytes);
        }
    }

    class Sender
    {
        IPAddress _targetaddr;
        int _port;
        TcpClient _client;

        public Sender( IPAddress address, int port )
        {
            _targetaddr = address;
            _port = port;
            _client = new TcpClient();
        }

        public void Send( string message )
        {
            _client.Connect(_targetaddr, _port);
            NetworkStream stream = _client.GetStream();
            byte[] sizebuffer = BitConverter.GetBytes(message.Length);
            byte[] messagebuffer = Encoding.UTF8.GetBytes(message);
            byte[] buffer = new byte[sizebuffer.Length + messagebuffer.Length];
            sizebuffer.CopyTo(buffer, 0);
            messagebuffer.CopyTo(buffer, 4);
            stream.Write(buffer, 0, buffer.Length);
            stream.Close();
            _client.Close();
        }
    }
}
