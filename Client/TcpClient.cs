﻿/*
 * ToDo: Add default configuration for properties (not on class constructor level)
 */

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Boerman.Core;
using Boerman.Core.Serialization;
using Boerman.TcpLib.Shared;

namespace Boerman.TcpLib.Client
{
    public partial class TcpClient<TSend, TReceive> 
        where TSend : class
        where TReceive : class
    {
        private StateObject _state;

        private readonly ClientSettings _clientSettings;

        private readonly ManualResetEvent _isConnected = new ManualResetEvent(false);
        private readonly ManualResetEvent _isSending = new ManualResetEvent(false);
        
        private bool _isRunning;
        private bool _isShuttingDown;

        public TcpClient()
        {
            _clientSettings = new ClientSettings
            {
                EndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 36700),
                Listening  = false,
                Splitter   = "\r\n",
                Timeout    = 1020000
            };
        }

        // EVENTS //
        public event OnReceiveEventHandler ReceiveEvent;
        public event OnSendEventHandler SendEvent;
        public event OnConnectEventHandler ConnectEvent;
        public event OnDisconnectEventHandler DisconnectEvent;

        public delegate void OnReceiveEventHandler(TReceive data);
        public delegate void OnSendEventHandler();
        public delegate void OnConnectEventHandler();
        public delegate void OnDisconnectEventHandler();

        public TcpClient(ClientSettings settings)
        {
            _clientSettings = settings;
        }

        public void Start()
        {
            try
            {
                // Reset the variables which are set earlier
                _isConnected.Reset();
                _isSending.Reset();

                // Continue trying until there's a connection.
                bool success;

                _state = new StateObject
                {
                    Guid = Guid.NewGuid(),
                    LastConnection = DateTime.UtcNow,
                    ReceiveBuffer = new byte[65536],
                    ReceiveBufferSize = 65536
                };

                do
                {
                    _state.WorkSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    _state.WorkSocket.BeginConnect(_clientSettings.EndPoint, ConnectCallback, _state);

                    success = _isConnected.WaitOne(10000);
                } while (!success);

                // We are connected!

                _isRunning = true;
                _isShuttingDown = false;

                ConnectEvent?.Invoke();
                
                _state.WorkSocket.BeginReceive(_state.ReceiveBuffer, 0, _state.ReceiveBufferSize, 0, ReceiveCallback,
                    _state);
            }
            catch (SocketException ex)
            {
                switch (ex.NativeErrorCode)
                {
                    case 10054: // An existing connection was forcibly closed by the remote host
                        Stop();
                        Start();
                        break;
                    default:
                        throw;
                }
            }
        }

        public void Stop()
        {
            try
            {
                _isShuttingDown = true;

                // There's no specific reason to set a timeout as this operation
                // should be completed pretty fast anyway.
                _isSending.WaitOne();
                _isSending.Reset();

                _isRunning = false;
                _isConnected.Reset();

                _state.WorkSocket.Shutdown(SocketShutdown.Both);
                _state.WorkSocket.Disconnect(false);
                _state.WorkSocket.Dispose();
            }
            catch (ObjectDisposedException)
            {

            }
            catch (SocketException ex)
            {
                if (ex.ErrorCode == 10038)
                {
                    // Something is done on not a socket... 
                }
                else if (ex.ErrorCode == 10004)
                {
                    // Some blocking call was interrupted.
                }
                else
                {
                    throw;
                }
            }
        }

        public void Send(string message)
        {
            Send(Encoding.GetEncoding(Constants.Encoding).GetBytes(message));
        }

        public void Send(TSend data)
        {
            var splitter = Encoding.GetEncoding(Constants.Encoding).GetBytes(_clientSettings.Splitter);
            var array = ObjectSerializer.Serialize(data).Concat(splitter).ToArray();

            Send(array);
        }

        private void Send(byte[] data)
        {
            // Wait with the send process until we're connected. (ToDo: Check whether we have to add some timeout)
            _isConnected.WaitOne();

            _state.OutboundMessages.Enqueue(data);
            
            if (!_isSending.WaitOne(0))
                EmptyOutboundQueue();   // We have to initialize the sending process.
        }

        private void EmptyOutboundQueue()
        {
            while (_state.OutboundMessages.Any())
            {
                if (_isShuttingDown)
                {
                    // Empty queue and return
                    while (_state.OutboundMessages.Any())
                    {
                        byte[] removedData;
                        _state.OutboundMessages.TryDequeue(out removedData);
                    }

                    return;
                }

                // Dropping connections shouldn't be a really big issue...
                // _isConnected.WaitOne();
                
                _state.OutboundMessages.TryDequeue(out _state.OutboundBuffer);

                try
                {
                    // We can only send one message at a time.
                    _state.WorkSocket.BeginSend(_state.OutboundBuffer, 0, _state.OutboundBuffer.Length, 0, SendCallback,
                        _state);
                }
                catch (SocketException ex)
                {
                    switch (ex.NativeErrorCode)
                    {
                        case 10054: // An existing connection was forcibly closed by the remote host
                            _isSending.Set(); // Otherwise the program will wait indefinitely.
                            Stop();
                            Start();
                            break;
                        default:
                            throw;
                    }
                }

                // Wait until we're cleared to send another message
                _isSending.WaitOne();
            }
        }
    }

    public class ClientSettings
    {
        public EndPoint EndPoint { get; set; }
        public string Splitter { get; set; }
        public bool Listening { get; set; }
        public int Timeout { get; set; }
        public bool ReconnectOnDisconnect { get; set; }
    }
}
