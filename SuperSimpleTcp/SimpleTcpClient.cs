﻿using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SuperSimpleTcp
{
    /// <summary>
    /// SimpleTcp client with SSL support.  
    /// Set the Connected, Disconnected, and DataReceived events.  
    /// Once set, use Connect() to connect to the server.
    /// </summary>
    public class SimpleTcpClient : IDisposable
    {
        #region Public-Members
         
        /// <summary>
        /// Indicates whether or not the client is connected to the server.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return _isConnected;
            }
            private set
            {
                _isConnected = value;
            }
        }

        /// <summary>
        /// Client IPEndPoint if connected.
        /// </summary>
        public IPEndPoint LocalEndpoint
        {
            get
            {
                if (_client != null && _isConnected)
                {
                    return (IPEndPoint)_client.Client.LocalEndPoint;
                }

                return null;
            }
        }

        /// <summary>
        /// SimpleTcp client settings.
        /// </summary>
        public SimpleTcpClientSettings Settings
        {
            get
            {
                return _settings;
            }
            set
            {
                if (value == null) _settings = new SimpleTcpClientSettings();
                else _settings = value;
            }
        }

        /// <summary>
        /// SimpleTcp client events.
        /// </summary>
        public SimpleTcpClientEvents Events
        {
            get
            {
                return _events;
            }
            set
            {
                if (value == null) _events = new SimpleTcpClientEvents();
                else _events = value;
            }
        }

        /// <summary>
        /// SimpleTcp statistics.
        /// </summary>
        public SimpleTcpStatistics Statistics
        {
            get
            {
                return _statistics;
            }
        }

        /// <summary>
        /// SimpleTcp keepalive settings.
        /// </summary>
        public SimpleTcpKeepaliveSettings Keepalive
        {
            get
            {
                return _keepalive;
            }
            set
            {
                if (value == null) _keepalive = new SimpleTcpKeepaliveSettings();
                else _keepalive = value;
            }
        }

        /// <summary>
        /// Method to invoke to send a log message.
        /// </summary>
        public Action<string> Logger = null;

        /// <summary>
        /// The IP:port of the server to which this client is mapped.
        /// </summary>
        public string ServerIpPort
        {
            get
            {
                return $"{_serverIp}:{_serverPort}";
            }
        }

        #endregion

        #region Private-Members

        private readonly string _header = "[SimpleTcp.Client] ";
        private SimpleTcpClientSettings _settings = new SimpleTcpClientSettings();
        private SimpleTcpClientEvents _events = new SimpleTcpClientEvents();
        private SimpleTcpKeepaliveSettings _keepalive = new SimpleTcpKeepaliveSettings();
        private SimpleTcpStatistics _statistics = new SimpleTcpStatistics();

        private string _serverIp = null;
        private int _serverPort = 0;
        private readonly IPAddress _ipAddress = null;
        private TcpClient _client = null;
        private NetworkStream _networkStream = null;

        private bool _ssl = false;
        private string _pfxCertFilename = null;
        private string _pfxPassword = null;
        private SslStream _sslStream = null;
        private X509Certificate2 _sslCert = null;
        private X509Certificate2Collection _sslCertCollection = null;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private bool _isConnected = false;

        private Task _dataReceiver = null;
        private Task _idleServerMonitor = null;
        private CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private CancellationToken _token;

        private DateTime _lastActivity = DateTime.Now;
        private bool _isTimeout = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiates the TCP client without SSL. Set the Connected, Disconnected, and DataReceived callbacks. Once set, use Connect() to connect to the server.
        /// </summary>
        /// <param name="ipPort">The IP:port of the server.</param> 
        public SimpleTcpClient(string ipPort)
        {
            if (string.IsNullOrEmpty(ipPort)) throw new ArgumentNullException(nameof(ipPort));

            Common.ParseIpPort(ipPort, out _serverIp, out _serverPort);
            if (_serverPort < 0) throw new ArgumentException("Port must be zero or greater.");
            if (string.IsNullOrEmpty(_serverIp)) throw new ArgumentNullException("Server IP or hostname must not be null.");

            if (!IPAddress.TryParse(_serverIp, out _ipAddress))
            {
                _ipAddress = Dns.GetHostEntry(_serverIp).AddressList[0];
                _serverIp = _ipAddress.ToString();
            }
        }

        /// <summary>
        /// Instantiates the TCP client. Set the Connected, Disconnected, and DataReceived callbacks. Once set, use Connect() to connect to the server.
        /// </summary>
        /// <param name="ipPort">The IP:port of the server.</param> 
        /// <param name="ssl">Enable or disable SSL.</param>
        /// <param name="pfxCertFilename">The filename of the PFX certificate file.</param>
        /// <param name="pfxPassword">The password to the PFX certificate file.</param>
        public SimpleTcpClient(string ipPort, bool ssl, string pfxCertFilename, string pfxPassword) : this(ipPort)
        {
            _ssl = ssl;
            _pfxCertFilename = pfxCertFilename;
            _pfxPassword = pfxPassword;
        }

        /// <summary>
        /// Instantiates the TCP client without SSL. Set the Connected, Disconnected, and DataReceived callbacks. Once set, use Connect() to connect to the server.
        /// </summary>
        /// <param name="serverIpOrHostname">The server IP address or hostname.</param>
        /// <param name="port">The TCP port on which to connect.</param>
        public SimpleTcpClient(string serverIpOrHostname, int port)
        {
            if (string.IsNullOrEmpty(serverIpOrHostname)) throw new ArgumentNullException(nameof(serverIpOrHostname));
            if (port < 0) throw new ArgumentException("Port must be zero or greater.");

            _serverIp = serverIpOrHostname;
            _serverPort = port;

            if (!IPAddress.TryParse(_serverIp, out _ipAddress))
            {
                _ipAddress = Dns.GetHostEntry(serverIpOrHostname).AddressList[0];
                _serverIp = _ipAddress.ToString();
            } 
        }

        /// <summary>
        /// Instantiates the TCP client.  Set the Connected, Disconnected, and DataReceived callbacks.  Once set, use Connect() to connect to the server.
        /// </summary>
        /// <param name="serverIpOrHostname">The server IP address or hostname.</param>
        /// <param name="port">The TCP port on which to connect.</param>
        /// <param name="ssl">Enable or disable SSL.</param>
        /// <param name="pfxCertFilename">The filename of the PFX certificate file.</param>
        /// <param name="pfxPassword">The password to the PFX certificate file.</param>
        public SimpleTcpClient(string serverIpOrHostname, int port, bool ssl, string pfxCertFilename, string pfxPassword) : this(serverIpOrHostname, port)
        {
            _ssl = ssl;
            _pfxCertFilename = pfxCertFilename;
            _pfxPassword = pfxPassword;
        }
         
        #endregion

        #region Public-Methods

        /// <summary>
        /// Dispose of the TCP client.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Establish a connection to the server.
        /// </summary>
        public void Connect()
        {
            if (IsConnected)
            {
                Logger?.Invoke($"{_header}already connected");
                return;
            }
            else
            {
                Logger?.Invoke($"{_header}initializing client");
                InitializeClient(_ssl, _pfxCertFilename, _pfxPassword);
                Logger?.Invoke($"{_header}connecting to {ServerIpPort}");
            }

            _tokenSource = new CancellationTokenSource();
            _token = _tokenSource.Token;

            IAsyncResult ar = _client.BeginConnect(_serverIp, _serverPort, null, null);
            WaitHandle wh = ar.AsyncWaitHandle;

            try
            {
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(_settings.ConnectTimeoutMs), false))
                {
                    _client.Close();
                    throw new TimeoutException($"Timeout connecting to {ServerIpPort}");
                }

                _client.EndConnect(ar);
                _networkStream = _client.GetStream();

                if (_ssl)
                {
                    if (_settings.AcceptInvalidCertificates)
                        _sslStream = new SslStream(_networkStream, false, new RemoteCertificateValidationCallback(AcceptCertificate));
                    else
                        _sslStream = new SslStream(_networkStream, false);

                    _sslStream.AuthenticateAsClient(_serverIp, _sslCertCollection, SslProtocols.Tls12, !_settings.AcceptInvalidCertificates);

                    if (!_sslStream.IsEncrypted) throw new AuthenticationException("Stream is not encrypted");
                    if (!_sslStream.IsAuthenticated) throw new AuthenticationException("Stream is not authenticated");
                    if (_settings.MutuallyAuthenticate && !_sslStream.IsMutuallyAuthenticated) throw new AuthenticationException("Mutual authentication failed");
                }

                if (_keepalive.EnableTcpKeepAlives) EnableKeepalives();
            }
            catch (Exception)
            {
                throw;
            }

            _isConnected = true;
            _lastActivity = DateTime.Now;
            _isTimeout = false;
            _events.HandleConnected(this, new ConnectionEventArgs(ServerIpPort));
            _dataReceiver = Task.Run(() => DataReceiver(_token), _token);
            _idleServerMonitor = Task.Run(() => IdleServerMonitor(), _token);
        }

        /// <summary>
        /// Establish the connection to the server with retries up to either the timeout specified or the value in Settings.ConnectTimeoutMs.
        /// </summary>
        /// <param name="timeoutMs">The amount of time in milliseconds to continue attempting connections.</param>
        public void ConnectWithRetries(int? timeoutMs = null)
        {
            if (timeoutMs != null && timeoutMs < 1) throw new ArgumentException("Timeout milliseconds must be greater than zero.");
            if (timeoutMs != null) _settings.ConnectTimeoutMs = timeoutMs.Value;

            if (IsConnected)
            {
                Logger?.Invoke($"{_header}already connected");
                return;
            }
            else
            {
                Logger?.Invoke($"{_header}initializing client");

                InitializeClient(_ssl, _pfxCertFilename, _pfxPassword);

                Logger?.Invoke($"{_header}connecting to {ServerIpPort}");
            }

            _tokenSource = new CancellationTokenSource();
            _token = _tokenSource.Token;

            using (CancellationTokenSource connectTokenSource = new CancellationTokenSource())
            {
                CancellationToken connectToken = connectTokenSource.Token;

                Task cancelTask = Task.Delay(_settings.ConnectTimeoutMs, _token);
                Task connectTask = Task.Run(() =>
                {
                    int retryCount = 0;

                    while (true)
                    {
                        try
                        {
                            string msg = $"{_header}attempting connection to {_serverIp}:{_serverPort}";
                            if (retryCount > 0) msg += $" ({retryCount} retries)";
                            Logger?.Invoke(msg);

                            _client.Dispose();
                            _client = new TcpClient();
                            _client.ConnectAsync(_serverIp, _serverPort).Wait(1000, connectToken);

                            if (_client.Connected)
                            {
                                Logger?.Invoke($"{_header}connected to {_serverIp}:{_serverPort}");
                                break;
                            }
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception e)
                        {
                            Logger?.Invoke($"{_header}failed connecting to {_serverIp}:{_serverPort}: {e.Message}");
                        }
                        finally
                        {
                            retryCount++;
                        }
                    }
                }, connectToken);

                Task.WhenAny(cancelTask, connectTask).Wait();

                if (cancelTask.IsCompleted)
                {
                    connectTokenSource.Cancel();
                    _client.Close();
                    throw new TimeoutException($"Timeout connecting to {ServerIpPort}");
                }

                try
                {
                    _networkStream = _client.GetStream();

                    if (_ssl)
                    {
                        if (_settings.AcceptInvalidCertificates)
                            _sslStream = new SslStream(_networkStream, false, new RemoteCertificateValidationCallback(AcceptCertificate));
                        else
                            _sslStream = new SslStream(_networkStream, false);

                        _sslStream.AuthenticateAsClient(_serverIp, _sslCertCollection, SslProtocols.Tls12, !_settings.AcceptInvalidCertificates);

                        if (!_sslStream.IsEncrypted) throw new AuthenticationException("Stream is not encrypted");
                        if (!_sslStream.IsAuthenticated) throw new AuthenticationException("Stream is not authenticated");
                        if (_settings.MutuallyAuthenticate && !_sslStream.IsMutuallyAuthenticated) throw new AuthenticationException("Mutual authentication failed");
                    }

                    if (_keepalive.EnableTcpKeepAlives) EnableKeepalives();
                }
                catch (Exception)
                {
                    throw;
                }

            }

            _isConnected = true;
            _lastActivity = DateTime.Now;
            _isTimeout = false;
            _events.HandleConnected(this, new ConnectionEventArgs(ServerIpPort));
            _dataReceiver = Task.Run(() => DataReceiver(_token), _token);
            _idleServerMonitor = Task.Run(() => IdleServerMonitor(), _token);
        }

        /// <summary>
        /// Disconnect from the server.
        /// </summary>
        public void Disconnect()
        {
            if (!IsConnected)
            {
                Logger?.Invoke($"{_header}already disconnected");
                return;
            }
            
            Logger?.Invoke($"{_header}disconnecting from {ServerIpPort}");

            _tokenSource.Cancel();
            _dataReceiver.Wait();
            _client.Close();
            _isConnected = false;
        }

        /// <summary>
        /// Send data to the server.
        /// </summary>
        /// <param name="data">String containing data to send.</param>
        public void Send(string data)
        {
            if (string.IsNullOrEmpty(data)) throw new ArgumentNullException(nameof(data));
            if (!_isConnected) throw new IOException("Not connected to the server; use Connect() first.");

            byte[] bytes = Encoding.UTF8.GetBytes(data);
            this.Send(bytes);
        }

        /// <summary>
        /// Send data to the server.
        /// </summary> 
        /// <param name="data">Byte array containing data to send.</param>
        public void Send(byte[] data)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            if (!_isConnected) throw new IOException("Not connected to the server; use Connect() first.");

            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(data, 0, data.Length);
                ms.Seek(0, SeekOrigin.Begin);
                SendInternal(data.Length, ms);
            }
        }

        /// <summary>
        /// Send data to the server.
        /// </summary>
        /// <param name="contentLength">The number of bytes to read from the source stream to send.</param>
        /// <param name="stream">Stream containing the data to send.</param>
        public void Send(long contentLength, Stream stream)
        {
            if (contentLength < 1) return;
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new InvalidOperationException("Cannot read from supplied stream.");
            if (!_isConnected) throw new IOException("Not connected to the server; use Connect() first.");

            SendInternal(contentLength, stream);
        }

        /// <summary>
        /// Send data to the server asynchronously.
        /// </summary>
        /// <param name="data">String containing data to send.</param>
        /// <param name="token">Cancellation token for canceling the request.</param>
        public async Task SendAsync(string data, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(data)) throw new ArgumentNullException(nameof(data));
            if (!_isConnected) throw new IOException("Not connected to the server; use Connect() first.");
            if (token == default(CancellationToken)) token = _token;

            byte[] bytes = Encoding.UTF8.GetBytes(data);

            using (MemoryStream ms = new MemoryStream())
            {
                await ms.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                ms.Seek(0, SeekOrigin.Begin);
                await SendInternalAsync(bytes.Length, ms, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Send data to the server asynchronously.
        /// </summary> 
        /// <param name="data">Byte array containing data to send.</param>
        /// <param name="token">Cancellation token for canceling the request.</param>
        public async Task SendAsync(byte[] data, CancellationToken token = default)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            if (!_isConnected) throw new IOException("Not connected to the server; use Connect() first.");
            if (token == default(CancellationToken)) token = _token;

            using (MemoryStream ms = new MemoryStream())
            {
                await ms.WriteAsync(data, 0, data.Length, token).ConfigureAwait(false);
                ms.Seek(0, SeekOrigin.Begin);
                await SendInternalAsync(data.Length, ms, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Send data to the server asynchronously.
        /// </summary>
        /// <param name="contentLength">The number of bytes to read from the source stream to send.</param>
        /// <param name="stream">Stream containing the data to send.</param>
        /// <param name="token">Cancellation token for canceling the request.</param>
        public async Task SendAsync(long contentLength, Stream stream, CancellationToken token = default)
        { 
            if (contentLength < 1) return;
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new InvalidOperationException("Cannot read from supplied stream.");
            if (!_isConnected) throw new IOException("Not connected to the server; use Connect() first.");
            if (token == default(CancellationToken)) token = _token;

            await SendInternalAsync(contentLength, stream, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Dispose of the TCP client.
        /// </summary>
        /// <param name="disposing">Dispose of resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isConnected = false;

                if (_tokenSource != null)
                {
                    if (!_tokenSource.IsCancellationRequested)
                    {
                        _tokenSource.Cancel();
                        _tokenSource.Dispose();
                    }
                }

                if (_sslStream != null)
                {
                    _sslStream.Close();
                    _sslStream.Dispose(); 
                }

                if (_networkStream != null)
                {
                    _networkStream.Close();
                    _networkStream.Dispose(); 
                }

                if (_client != null)
                {
                    _client.Close();
                    _client.Dispose(); 
                }

                Logger?.Invoke($"{_header}dispose complete");
            }
        }

        private void InitializeClient(bool ssl, string pfxCertFilename, string pfxPassword)
        {
            _ssl = ssl;
            _pfxCertFilename = pfxCertFilename;
            _pfxPassword = pfxPassword;
            _client = new TcpClient();
            _sslStream = null;
            _sslCert = null;
            _sslCertCollection = null;

            if (_ssl)
            {
                if (string.IsNullOrEmpty(pfxPassword))
                {
                    _sslCert = new X509Certificate2(pfxCertFilename);
                }
                else
                {
                    _sslCert = new X509Certificate2(pfxCertFilename, pfxPassword);
                }

                _sslCertCollection = new X509Certificate2Collection
                {
                    _sslCert
                };
            }
        }

        private bool AcceptCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        { 
            return _settings.AcceptInvalidCertificates;
        }

        private async Task DataReceiver(CancellationToken token)
        { 
            while (!token.IsCancellationRequested && _client != null && _client.Connected)
            {
                try
                {
                    byte[] data = await DataReadAsync(token).ConfigureAwait(false);
                    if (data == null)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        continue;
                    }

                    _lastActivity = DateTime.Now;
                    _events.HandleDataReceived(this, new DataReceivedEventArgs(ServerIpPort, data));
                    _statistics.ReceivedBytes += data.Length;
                }
                catch (AggregateException)
                {
                    Logger?.Invoke($"{_header}data receiver canceled, disconnected");
                    break;
                }
                catch (IOException)
                {
                    Logger?.Invoke($"{_header}data receiver canceled, disconnected");
                    break;
                }
                catch (SocketException)
                {
                    Logger?.Invoke($"{_header}data receiver canceled, disconnected");
                    break;
                }
                catch (TaskCanceledException)
                {
                    Logger?.Invoke($"{_header}data receiver task canceled, disconnected");
                    break;
                }
                catch (OperationCanceledException)
                {
                    Logger?.Invoke($"{_header}data receiver operation canceled, disconnected");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    Logger?.Invoke($"{_header}data receiver canceled due to disposal, disconnected");
                    break;
                }
                catch (Exception e)
                {
                    Logger?.Invoke($"{_header}data receiver exception:{Environment.NewLine}{e}{Environment.NewLine}");
                    break;
                }
            }

            Logger?.Invoke($"{_header}disconnection detected");            

            _isConnected = false;

            if (!_isTimeout) _events.HandleClientDisconnected(this, new ConnectionEventArgs(ServerIpPort, DisconnectReason.Normal));
            else _events.HandleClientDisconnected(this, new ConnectionEventArgs(ServerIpPort, DisconnectReason.Timeout));

            Dispose();
        }

        private async Task<byte[]> DataReadAsync(CancellationToken token)
        {  
            byte[] buffer = new byte[_settings.StreamBufferSize];
            int read = 0;

            Task<int> readTask = null;

            if (!_ssl)
            {
                readTask = _networkStream.ReadAsync(buffer, 0, buffer.Length, token);
            }
            else
            {
                readTask = _sslStream.ReadAsync(buffer, 0, buffer.Length, token);
            }

            // see https://stackoverflow.com/a/20910003
            Task timeoutTask = Task.Delay(_settings.ReadTimeoutMs);

            byte[] result = await Task.Factory.ContinueWhenAny<byte[]>(new Task[] { readTask, timeoutTask }, (completedTask) =>
            {
                if (completedTask == timeoutTask) // timeout
                {
                    return null;
                }
                else // the readTask completed
                {
                    read = readTask.Result;
                    if (read > 0)
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            ms.Write(buffer, 0, read);
                            return ms.ToArray();
                        }
                    }
                    else
                    {
                        throw new SocketException();
                    }
                }
            });

            return result;
        }

        private void SendInternal(long contentLength, Stream stream)
        { 
            long bytesRemaining = contentLength;
            int bytesRead = 0;
            byte[] buffer = new byte[_settings.StreamBufferSize];

            try
            {
                _sendLock.Wait();

                while (bytesRemaining > 0)
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        if (!_ssl) _networkStream.Write(buffer, 0, bytesRead);
                        else _sslStream.Write(buffer, 0, bytesRead);

                        bytesRemaining -= bytesRead;
                        _statistics.SentBytes += bytesRead;
                    }
                }

                if (!_ssl) _networkStream.Flush();
                else _sslStream.Flush();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task SendInternalAsync(long contentLength, Stream stream, CancellationToken token)
        {
            try
            {
                long bytesRemaining = contentLength;
                int bytesRead = 0;
                byte[] buffer = new byte[_settings.StreamBufferSize];

                await _sendLock.WaitAsync(token).ConfigureAwait(false);

                while (bytesRemaining > 0)
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (bytesRead > 0)
                    {
                        if (!_ssl) await _networkStream.WriteAsync(buffer, 0, bytesRead, token).ConfigureAwait(false);
                        else await _sslStream.WriteAsync(buffer, 0, bytesRead, token).ConfigureAwait(false);

                        bytesRemaining -= bytesRead;
                        _statistics.SentBytes += bytesRead;
                    }
                }

                if (!_ssl) await _networkStream.FlushAsync(token).ConfigureAwait(false);
                else await _sslStream.FlushAsync(token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {

            }
            catch (OperationCanceledException)
            {

            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void EnableKeepalives()
        {
            try
            {
#if NETCOREAPP || NET5_0

                _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, _keepalive.TcpKeepAliveTime);
                _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, _keepalive.TcpKeepAliveInterval);
                _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, _keepalive.TcpKeepAliveRetryCount);

#elif NETFRAMEWORK

                byte[] keepAlive = new byte[12];

                // Turn keepalive on
                Buffer.BlockCopy(BitConverter.GetBytes((uint)1), 0, keepAlive, 0, 4);

                // Set TCP keepalive time
                Buffer.BlockCopy(BitConverter.GetBytes((uint)_keepalive.TcpKeepAliveTime), 0, keepAlive, 4, 4);

                // Set TCP keepalive interval
                Buffer.BlockCopy(BitConverter.GetBytes((uint)_keepalive.TcpKeepAliveInterval), 0, keepAlive, 8, 4);

                // Set keepalive settings on the underlying Socket
                _client.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);

#elif NETSTANDARD

#endif
            }
            catch (Exception)
            {
                Logger?.Invoke($"{_header}keepalives not supported on this platform, disabled");
                _keepalive.EnableTcpKeepAlives = false;
            }
        }

        private async Task IdleServerMonitor()
        {
            while (!_token.IsCancellationRequested)
            {
                await Task.Delay(_settings.IdleServerEvaluationIntervalMs, _token).ConfigureAwait(false);

                if (_settings.IdleServerTimeoutMs == 0) continue;

                DateTime timeoutTime = _lastActivity.AddMilliseconds(_settings.IdleServerTimeoutMs);

                if (DateTime.Now > timeoutTime)
                {
                    Logger?.Invoke($"{_header}disconnecting from {ServerIpPort} due to timeout");
                    _isConnected = false;
                    _isTimeout = true;
                    _tokenSource.Cancel(); // DataReceiver will fire events including dispose
                }
            }
        }

        #endregion
    }
}