using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TianshuQitanLauncher.Protocol;

namespace TianshuQitanLauncher
{
    public sealed class AudioFilterProxyOptions
    {
        public int ListenPort { get; set; }
        public string FfmpegPath { get; set; }
        public string FilterGraph { get; set; }
        public int BitrateKbps { get; set; }
        public int Mp3Quality { get; set; }
        public string CacheDirectory { get; set; }
        public string SoundHost { get; set; }
        public string SoundPathPrefix { get; set; }
        public int UpstreamTimeoutMs { get; set; }

        public AudioFilterProxyOptions()
        {
            ListenPort = 0;
            FfmpegPath = "ffmpeg";
            FilterGraph = "highpass=f=25,lowpass=f=19000,afftdn=nr=6:nf=-50:tn=1:gs=6,adeclick=t=3,alimiter=limit=0.97";
            BitrateKbps = 0;
            Mp3Quality = 2;
            CacheDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "audio-cache");
            SoundHost = "resource.t.imop.com";
            SoundPathPrefix = "/sound";
            UpstreamTimeoutMs = 15000;
        }
    }

    public sealed class AudioFilterProxy : IDisposable
    {
        private const int MaximumHeaderBytes = 65536;
        private readonly object stateGate = new object();
        private readonly object cacheGate = new object();
        private readonly object processGate = new object();
        private readonly Dictionary<string, object> cacheLocks = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Process> activeProcesses = new HashSet<Process>();
        private readonly AudioFilterProxyOptions options;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool stopping;
        private bool filteringEnabled;
        private bool ffmpegAvailable;
        private long filteredRequests;
        private long cacheHits;
        private long proxyRequests;
        private long soundRequests;
        private string lastStatus;

        public AudioFilterProxy(AudioFilterProxyOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            this.options = options;
            filteringEnabled = true;
            lastStatus = "尚未启动";
        }

        public event Action<string> StatusChanged;

        public int Port { get; private set; }
        public bool IsRunning { get { return listener != null && !stopping; } }
        public bool FfmpegAvailable { get { return ffmpegAvailable; } }
        public long FilteredRequests { get { return Interlocked.Read(ref filteredRequests); } }
        public long CacheHits { get { return Interlocked.Read(ref cacheHits); } }
        public long ProxyRequests { get { return Interlocked.Read(ref proxyRequests); } }
        public long SoundRequests { get { return Interlocked.Read(ref soundRequests); } }
        public string LastStatus { get { lock (stateGate) { return lastStatus; } } }
        public string OutputEncodingDescription
        {
            get
            {
                return options.BitrateKbps > 0
                    ? "MP3 固定 " + options.BitrateKbps.ToString(CultureInfo.InvariantCulture) + " kbps"
                    : "保留源采样率/声道，MP3 VBR q=" +
                        Math.Max(0, Math.Min(9, options.Mp3Quality)).ToString(CultureInfo.InvariantCulture);
            }
        }

        public bool FilteringEnabled
        {
            get { lock (stateGate) { return filteringEnabled; } }
            set
            {
                lock (stateGate) filteringEnabled = value;
                PublishStatus(value ? "BGM 实时降噪已开启" : "BGM 实时降噪已旁路");
            }
        }

        public void Start()
        {
            if (listener != null) return;
            Directory.CreateDirectory(options.CacheDirectory);
            ffmpegAvailable = ProbeFfmpeg();
            listener = new TcpListener(IPAddress.Loopback, Math.Max(0, options.ListenPort));
            listener.Start(64);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            stopping = false;
            acceptThread = new Thread(AcceptLoop);
            acceptThread.IsBackground = true;
            acceptThread.Name = "Tianshu BGM filter proxy";
            acceptThread.Start();
            PublishStatus(ffmpegAvailable
                ? "BGM 实时降噪代理已启动，端口 " + Port
                : "未找到可用 FFmpeg；音频代理将原样旁路");
        }

        public void Stop()
        {
            stopping = true;
            TcpListener current = listener;
            listener = null;
            if (current != null)
            {
                try { current.Stop(); } catch { }
            }
            if (acceptThread != null && acceptThread.IsAlive) acceptThread.Join(2000);
            acceptThread = null;
            lock (processGate)
            {
                foreach (Process process in activeProcesses)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                }
                activeProcesses.Clear();
            }
            PublishStatus("BGM 实时降噪代理已停止");
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            using (WinsockCaptureEngine.BypassCurrentThreadInterception())
            {
                while (!stopping)
                {
                    try
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        ThreadPool.QueueUserWorkItem(delegate { HandleClientSafely(client); });
                    }
                    catch (SocketException)
                    {
                        if (!stopping) Thread.Sleep(50);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!stopping) Logger.Error("Audio proxy accept failed", ex);
                    }
                }
            }
        }

        private void HandleClientSafely(TcpClient client)
        {
            using (WinsockCaptureEngine.BypassCurrentThreadInterception())
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = options.UpstreamTimeoutMs;
                    client.SendTimeout = options.UpstreamTimeoutMs;
                    HandleClient(client.GetStream());
                }
                catch (Exception ex)
                {
                    if (!stopping) Logger.Error("Audio proxy request failed", ex);
                }
            }
        }

        private void HandleClient(NetworkStream clientStream)
        {
            HttpRequestData request = ReadRequest(clientStream);
            if (request == null) return;
            Interlocked.Increment(ref proxyRequests);
            if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                TunnelConnect(clientStream, request);
                return;
            }
            ForwardOrFilter(clientStream, request);
        }

        private bool IsSoundRequest(HttpRequestData request)
        {
            string host;
            int ignoredPort;
            ResolveRequestDestination(request, out host, out ignoredPort);
            string path = GetOriginPath(request.Target);
            return string.Equals(host, options.SoundHost, StringComparison.OrdinalIgnoreCase) &&
                path.StartsWith(options.SoundPathPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsConfiguredSoundHost(HttpRequestData request)
        {
            string host;
            int ignoredPort;
            ResolveRequestDestination(request, out host, out ignoredPort);
            return string.Equals(host, options.SoundHost, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAudioExtension(HttpRequestData request)
        {
            string path = GetOriginPath(request.Target);
            int query = path.IndexOf('?');
            if (query >= 0) path = path.Substring(0, query);
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".aac", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".flac", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".wma", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAudioResponse(HttpRequestData request, HttpResponseData response)
        {
            if (response == null) return false;
            string type = response.ContentType ?? string.Empty;
            int separator = type.IndexOf(';');
            if (separator >= 0) type = type.Substring(0, separator);
            if (type.Trim().StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return true;
            if (HasAudioExtension(request) &&
                (string.IsNullOrWhiteSpace(type) ||
                 type.IndexOf("octet-stream", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            string disposition = response.GetHeader("Content-Disposition");
            return disposition.IndexOf(".mp3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                disposition.IndexOf(".aac", StringComparison.OrdinalIgnoreCase) >= 0 ||
                disposition.IndexOf(".wav", StringComparison.OrdinalIgnoreCase) >= 0 ||
                disposition.IndexOf(".ogg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                disposition.IndexOf(".flac", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ForwardOrFilter(NetworkStream clientStream, HttpRequestData request)
        {
            bool soundHint = IsSoundRequest(request) || HasAudioExtension(request);
            bool inspectHost = IsConfiguredSoundHost(request);
            string absoluteUrl = BuildAbsoluteUrl(request);
            string sourcePath = BuildOriginalCachePath(absoluteUrl);
            soundHint = soundHint || File.Exists(sourcePath);
            string cacheKey = BuildFilteredCacheKey(absoluteUrl);
            string cachePath = Path.Combine(options.CacheDirectory, cacheKey + ".mp3");

            if (soundHint && FilteringEnabled && ffmpegAvailable && File.Exists(cachePath) &&
                new FileInfo(cachePath).Length > 0)
            {
                CountSoundRequest(absoluteUrl);
                Interlocked.Increment(ref cacheHits);
                ServeCachedFile(clientStream, request, cachePath);
                PublishStatus("BGM 降噪缓存命中：" + GetOriginPath(request.Target));
                return;
            }

            using (TcpClient upstream = OpenUpstream(request))
            using (NetworkStream upstreamStream = upstream.GetStream())
            {
                WriteUpstreamRequest(upstreamStream, request, soundHint || inspectHost);
                HttpResponseData response = ReadResponse(upstreamStream);
                if (response == null) return;

                if (inspectHost)
                {
                    Logger.Info("AudioFilter inspect: " + absoluteUrl + " -> " + response.StatusCode + " " +
                        (string.IsNullOrWhiteSpace(response.ContentType) ? "(no content-type)" : response.ContentType));
                }

                bool soundResponse = soundHint || IsAudioResponse(request, response);
                if (!soundResponse)
                {
                    WriteRawResponse(clientStream, upstreamStream, response);
                    return;
                }

                CountSoundRequest(absoluteUrl);
                bool canFilter = FilteringEnabled && ffmpegAvailable && response.StatusCode == 200 &&
                    string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(response.GetHeader("Content-Encoding"));
                if (!canFilter)
                {
                    WriteRawResponse(clientStream, upstreamStream, response);
                    return;
                }

                object itemGate = GetCacheLock(cacheKey);
                lock (itemGate)
                {
                    if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                    {
                        Interlocked.Increment(ref cacheHits);
                        ServeCachedFile(clientStream, request, cachePath);
                        PublishStatus("BGM 降噪缓存命中：" + GetOriginPath(request.Target));
                        return;
                    }
                    TranscodeAndStream(clientStream, request, upstreamStream, response, absoluteUrl, cachePath);
                }
            }
        }

        private void CountSoundRequest(string absoluteUrl)
        {
            Interlocked.Increment(ref soundRequests);
            PublishStatus("捕获 BGM 请求：" + absoluteUrl);
        }

        private static void WriteRawResponse(Stream clientStream, Stream upstreamStream, HttpResponseData response)
        {
            clientStream.Write(response.RawHeader, 0, response.RawHeader.Length);
            CopyStream(upstreamStream, clientStream, null);
        }

        private void TunnelConnect(NetworkStream clientStream, HttpRequestData request)
        {
            string host;
            int port;
            ParseHost(request.Target, out host, out port);
            using (TcpClient upstream = new TcpClient())
            {
                upstream.NoDelay = true;
                upstream.ReceiveTimeout = Math.Max(60000, options.UpstreamTimeoutMs);
                upstream.SendTimeout = Math.Max(60000, options.UpstreamTimeoutMs);
                upstream.Connect(host, port);
                using (NetworkStream upstreamStream = upstream.GetStream())
                {
                    WriteAscii(clientStream, "HTTP/1.1 200 Connection Established\r\nConnection: close\r\n\r\n");
                    Thread clientToUpstream = new Thread(new ThreadStart(delegate
                    {
                        try { CopyStream(clientStream, upstreamStream, null); }
                        catch { }
                        try { upstream.Client.Shutdown(SocketShutdown.Send); } catch { }
                    }));
                    clientToUpstream.IsBackground = true;
                    clientToUpstream.Name = "Tianshu HTTP CONNECT upload";
                    clientToUpstream.Start();
                    try { CopyStream(upstreamStream, clientStream, null); } catch { }
                    try { clientToUpstream.Join(2000); } catch { }
                }
            }
        }

        private string BuildFilteredCacheKey(string absoluteUrl)
        {
            string encoding = options.BitrateKbps > 0
                ? "cbr:" + options.BitrateKbps.ToString(CultureInfo.InvariantCulture)
                : "vbr:" + Math.Max(0, Math.Min(9, options.Mp3Quality)).ToString(CultureInfo.InvariantCulture);
            return ComputeCacheKey(absoluteUrl + "\n" + options.FilterGraph + "\n" + encoding);
        }

        private string BuildOriginalCachePath(string absoluteUrl)
        {
            return Path.Combine(options.CacheDirectory, "original", ComputeCacheKey(absoluteUrl) + ".audio");
        }

        private void TranscodeAndStream(NetworkStream clientStream, HttpRequestData request,
            NetworkStream upstreamStream, HttpResponseData response, string absoluteUrl, string cachePath)
        {
            string filteredTemp = cachePath + ".filtered-" + Guid.NewGuid().ToString("N") + ".tmp";
            string originalTemp = cachePath + ".source-" + Guid.NewGuid().ToString("N") + ".tmp";
            string sourceDirectory = Path.Combine(options.CacheDirectory, "original");
            Directory.CreateDirectory(sourceDirectory);
            string sourcePath = BuildOriginalCachePath(absoluteUrl);
            if (response.StatusCode != 200)
            {
                WriteRawResponse(clientStream, upstreamStream, response);
                return;
            }

            Process process;
            try
            {
                process = StartFfmpeg();
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot start FFmpeg; returning original BGM", ex);
                WriteOriginalResponseHeader(clientStream, response, response.ContentLength);
                CopyResponseBody(upstreamStream, response, clientStream, null);
                return;
            }

            Exception feederError = null;
            Thread feeder;
            using (process)
            using (FileStream filteredFile = new FileStream(filteredTemp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (FileStream originalFile = new FileStream(originalTemp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                RegisterProcess(process);
                feeder = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        CopyResponseBody(upstreamStream, response, process.StandardInput.BaseStream, originalFile);
                    }
                    catch (Exception ex) { feederError = ex; }
                    finally
                    {
                        try { process.StandardInput.BaseStream.Close(); } catch { }
                        try { originalFile.Flush(); } catch { }
                    }
                }));
                feeder.IsBackground = true;
                feeder.Name = "Tianshu BGM FFmpeg input";
                feeder.Start();

                byte[] buffer = new byte[32768];
                int firstCount = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length);
                if (firstCount <= 0)
                {
                    feeder.Join(options.UpstreamTimeoutMs);
                    try { process.WaitForExit(3000); } catch { }
                    UnregisterProcess(process);
                    originalFile.Flush();
                    Logger.Error("FFmpeg produced no BGM output", feederError ?? new InvalidDataException("FFmpeg returned an empty audio stream."));
                    WriteOriginalResponseHeader(clientStream, response, originalFile.Length);
                    originalFile.Position = 0;
                    CopyStream(originalFile, clientStream, null);
                    filteredFile.Close();
                    originalFile.Close();
                    SafeDelete(filteredTemp);
                    PreserveOriginal(originalTemp, sourcePath);
                    return;
                }

                WriteFilteredResponseHeader(clientStream);
                bool clientConnected = TryWrite(clientStream, buffer, firstCount);
                filteredFile.Write(buffer, 0, firstCount);
                int count;
                while ((count = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    filteredFile.Write(buffer, 0, count);
                    if (clientConnected) clientConnected = TryWrite(clientStream, buffer, count);
                }
                feeder.Join(options.UpstreamTimeoutMs);
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    try { process.WaitForExit(3000); } catch { }
                }
                filteredFile.Flush();
                long filteredLength = filteredFile.Length;
                filteredFile.Close();
                originalFile.Close();
                UnregisterProcess(process);
                PreserveOriginal(originalTemp, sourcePath);

                if (process.ExitCode == 0 && feederError == null && filteredLength > 0)
                {
                    if (File.Exists(cachePath)) File.Delete(cachePath);
                    File.Move(filteredTemp, cachePath);
                    Interlocked.Increment(ref filteredRequests);
                    PublishStatus("已按方案 3 实时过滤 BGM：" + GetOriginPath(request.Target));
                }
                else
                {
                    Logger.Error("FFmpeg BGM pipeline failed", feederError ?? new InvalidDataException("FFmpeg exited with code " + process.ExitCode));
                    SafeDelete(filteredTemp);
                }
            }
        }

        private static void PreserveOriginal(string temporaryPath, string sourcePath)
        {
            try
            {
                if (!File.Exists(temporaryPath)) return;
                if (File.Exists(sourcePath))
                {
                    File.Delete(temporaryPath);
                    return;
                }
                File.Move(temporaryPath, sourcePath);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot preserve original BGM", ex);
                SafeDelete(temporaryPath);
            }
        }

        private Process StartFfmpeg()
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = options.FfmpegPath;
            string rateControl = options.BitrateKbps > 0
                ? "-b:a " + options.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k"
                : "-q:a " + Math.Max(0, Math.Min(9, options.Mp3Quality)).ToString(CultureInfo.InvariantCulture);
            start.Arguments = "-hide_banner -loglevel error -i pipe:0 -map 0:a:0 -vn -af \"" +
                options.FilterGraph.Replace("\"", "\\\"") + "\" -map_metadata -1 -c:a libmp3lame " +
                rateControl + " -f mp3 -flush_packets 1 pipe:1";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            Process process = Process.Start(start);
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Info("FFmpeg BGM: " + e.Data);
            };
            process.BeginErrorReadLine();
            return process;
        }

        private bool ProbeFfmpeg()
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(options.FfmpegPath, "-hide_banner -version");
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                using (Process process = Process.Start(start))
                {
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill();
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("FFmpeg probe failed", ex);
                return false;
            }
        }

        private TcpClient OpenUpstream(HttpRequestData request)
        {
            string host;
            int port;
            ResolveRequestDestination(request, out host, out port);
            TcpClient upstream = new TcpClient();
            upstream.NoDelay = true;
            upstream.ReceiveTimeout = options.UpstreamTimeoutMs;
            upstream.SendTimeout = options.UpstreamTimeoutMs;
            upstream.Connect(host, port);
            return upstream;
        }

        private static void WriteUpstreamRequest(Stream upstream, HttpRequestData request, bool audio)
        {
            StringBuilder header = new StringBuilder();
            header.Append(request.Method).Append(' ').Append(GetOriginPath(request.Target)).Append(' ')
                .Append(string.IsNullOrWhiteSpace(request.Version) ? "HTTP/1.1" : request.Version).Append("\r\n");
            foreach (KeyValuePair<string, string> item in request.Headers)
            {
                if (string.Equals(item.Key, "Connection", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Key, "Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                    (audio && (string.Equals(item.Key, "Range", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(item.Key, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))))
                    continue;
                header.Append(item.Key).Append(": ").Append(item.Value).Append("\r\n");
            }
            header.Append("Connection: close\r\n\r\n");
            byte[] bytes = Encoding.ASCII.GetBytes(header.ToString());
            upstream.Write(bytes, 0, bytes.Length);
            if (request.Body.Length > 0) upstream.Write(request.Body, 0, request.Body.Length);
            upstream.Flush();
        }

        private static HttpRequestData ReadRequest(Stream stream)
        {
            byte[] raw = ReadHeaderBytes(stream);
            if (raw == null) return null;
            string text = Encoding.GetEncoding(28591).GetString(raw);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(new[] { ' ' }, 3);
            if (first.Length < 2) throw new InvalidDataException("Invalid HTTP request line.");
            HttpRequestData request = new HttpRequestData
            {
                Method = first[0],
                Target = first[1],
                Version = first.Length > 2 ? first[2] : "HTTP/1.0"
            };
            ParseHeaders(lines, request.Headers);
            int length = ParseContentLength(request.GetHeader("Content-Length"));
            request.Body = ReadExact(stream, length);
            return request;
        }

        private static HttpResponseData ReadResponse(Stream stream)
        {
            byte[] raw = ReadHeaderBytes(stream);
            if (raw == null) return null;
            string text = Encoding.GetEncoding(28591).GetString(raw);
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(new[] { ' ' }, 3);
            int status;
            if (first.Length < 2 || !int.TryParse(first[1], out status)) throw new InvalidDataException("Invalid HTTP response line.");
            HttpResponseData response = new HttpResponseData { RawHeader = raw, StatusCode = status };
            ParseHeaders(lines, response.Headers);
            response.ContentLength = ParseLong(response.GetHeader("Content-Length"), -1);
            response.Chunked = response.GetHeader("Transfer-Encoding").IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
            response.ContentType = response.GetHeader("Content-Type");
            return response;
        }

        private static byte[] ReadHeaderBytes(Stream stream)
        {
            List<byte> bytes = new List<byte>(1024);
            int matched = 0;
            byte[] marker = { 13, 10, 13, 10 };
            while (bytes.Count < MaximumHeaderBytes)
            {
                int value = stream.ReadByte();
                if (value < 0) return bytes.Count == 0 ? null : bytes.ToArray();
                byte current = (byte)value;
                bytes.Add(current);
                if (current == marker[matched])
                {
                    matched++;
                    if (matched == marker.Length) return bytes.ToArray();
                }
                else
                {
                    matched = current == marker[0] ? 1 : 0;
                }
            }
            throw new InvalidDataException("HTTP header exceeds the safety limit.");
        }

        private static void ParseHeaders(string[] lines, IDictionary<string, string> headers)
        {
            for (int i = 1; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf(':');
                if (separator <= 0) continue;
                string name = lines[i].Substring(0, separator).Trim();
                string value = lines[i].Substring(separator + 1).Trim();
                string existing;
                headers[name] = headers.TryGetValue(name, out existing) ? existing + ", " + value : value;
            }
        }

        private static void CopyResponseBody(Stream source, HttpResponseData response, Stream destination, Stream mirror)
        {
            if (response.Chunked)
            {
                CopyChunkedBody(source, destination, mirror);
            }
            else
            {
                CopyStream(source, destination, mirror, response.ContentLength);
            }
        }

        private static void CopyChunkedBody(Stream source, Stream destination, Stream mirror)
        {
            while (true)
            {
                string line = ReadAsciiLine(source);
                int separator = line.IndexOf(';');
                string sizeText = separator < 0 ? line : line.Substring(0, separator);
                long size;
                if (!long.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size))
                    throw new InvalidDataException("Invalid chunked HTTP body.");
                if (size == 0)
                {
                    while (ReadAsciiLine(source).Length > 0) { }
                    return;
                }
                CopyStream(source, destination, mirror, size);
                ReadAsciiLine(source);
            }
        }

        private static string ReadAsciiLine(Stream stream)
        {
            List<byte> bytes = new List<byte>();
            while (bytes.Count < 8192)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException();
                if (value == 10) break;
                if (value != 13) bytes.Add((byte)value);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static void CopyStream(Stream source, Stream destination, Stream mirror)
        {
            CopyStream(source, destination, mirror, -1);
        }

        private static void CopyStream(Stream source, Stream destination, Stream mirror, long maximum)
        {
            byte[] buffer = new byte[32768];
            long remaining = maximum;
            while (maximum < 0 || remaining > 0)
            {
                int requested = maximum < 0 ? buffer.Length : (int)Math.Min(buffer.Length, remaining);
                int count = source.Read(buffer, 0, requested);
                if (count <= 0) break;
                destination.Write(buffer, 0, count);
                if (mirror != null) mirror.Write(buffer, 0, count);
                if (maximum >= 0) remaining -= count;
            }
            try { destination.Flush(); } catch { }
        }

        private static void ServeCachedFile(Stream client, HttpRequestData request, string path)
        {
            long length = new FileInfo(path).Length;
            long start = 0;
            long end = length - 1;
            bool partial = TryParseRange(request.GetHeader("Range"), length, out start, out end);
            long responseLength = end - start + 1;
            StringBuilder header = new StringBuilder();
            header.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            header.Append("Content-Type: audio/mpeg\r\nAccept-Ranges: bytes\r\nContent-Length: ")
                .Append(responseLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (partial)
                header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
            header.Append("Cache-Control: public, max-age=31536000\r\nConnection: close\r\n\r\n");
            WriteAscii(client, header.ToString());
            if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase)) return;
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                file.Position = start;
                CopyStream(file, client, null, responseLength);
            }
        }

        private static void WriteFilteredResponseHeader(Stream stream)
        {
            WriteAscii(stream,
                "HTTP/1.0 200 OK\r\nContent-Type: audio/mpeg\r\n" +
                "Cache-Control: no-cache\r\nConnection: close\r\n\r\n");
        }

        private static void WriteOriginalResponseHeader(Stream stream, HttpResponseData response, long length)
        {
            string type = string.IsNullOrWhiteSpace(response.ContentType) ? "application/octet-stream" : response.ContentType;
            StringBuilder header = new StringBuilder();
            header.Append("HTTP/1.0 200 OK\r\nContent-Type: ").Append(type).Append("\r\n");
            if (length >= 0)
            {
                header.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            header.Append("Connection: close\r\n\r\n");
            WriteAscii(stream, header.ToString());
        }

        private static bool TryWrite(Stream stream, byte[] bytes, int count)
        {
            try
            {
                stream.Write(bytes, 0, count);
                stream.Flush();
                return true;
            }
            catch { return false; }
        }

        private static void WriteAscii(Stream stream, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private object GetCacheLock(string key)
        {
            lock (cacheGate)
            {
                object value;
                if (!cacheLocks.TryGetValue(key, out value))
                {
                    value = new object();
                    cacheLocks.Add(key, value);
                }
                return value;
            }
        }

        private void RegisterProcess(Process process)
        {
            lock (processGate) activeProcesses.Add(process);
        }

        private void UnregisterProcess(Process process)
        {
            lock (processGate) activeProcesses.Remove(process);
        }

        private void PublishStatus(string value)
        {
            lock (stateGate) lastStatus = value;
            Logger.Info("AudioFilter: " + value);
            Action<string> handler = StatusChanged;
            if (handler != null) handler(value);
        }

        private static string BuildAbsoluteUrl(HttpRequestData request)
        {
            Uri absolute;
            if (Uri.TryCreate(request.Target, UriKind.Absolute, out absolute)) return absolute.AbsoluteUri;
            return "http://" + request.GetHeader("Host") + GetOriginPath(request.Target);
        }

        private static string GetOriginPath(string target)
        {
            Uri absolute;
            if (Uri.TryCreate(target, UriKind.Absolute, out absolute)) return absolute.PathAndQuery;
            return string.IsNullOrWhiteSpace(target) ? "/" : target;
        }

        private static void ParseHost(string value, out string host, out int port)
        {
            host = RemovePort(value);
            port = 80;
            int colon = value == null ? -1 : value.LastIndexOf(':');
            int parsed;
            if (colon > 0 && int.TryParse(value.Substring(colon + 1), out parsed)) port = parsed;
            if (string.IsNullOrWhiteSpace(host)) throw new InvalidDataException("HTTP Host header is missing.");
        }

        private static void ResolveRequestDestination(HttpRequestData request, out string host, out int port)
        {
            Uri absolute;
            if (Uri.TryCreate(request.Target, UriKind.Absolute, out absolute) &&
                (string.Equals(absolute.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(absolute.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                host = absolute.Host;
                port = absolute.IsDefaultPort
                    ? (string.Equals(absolute.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                    : absolute.Port;
                return;
            }
            ParseHost(request.GetHeader("Host"), out host, out port);
        }

        private static string RemovePort(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            int colon = value.LastIndexOf(':');
            return colon > 0 && value.IndexOf(':') == colon ? value.Substring(0, colon) : value;
        }

        private static int ParseContentLength(string value)
        {
            long parsed = ParseLong(value, 0);
            if (parsed < 0 || parsed > 16 * 1024 * 1024) throw new InvalidDataException("HTTP request body is too large.");
            return (int)parsed;
        }

        private static long ParseLong(string value, long defaultValue)
        {
            long result;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : defaultValue;
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            byte[] result = new byte[Math.Max(0, length)];
            int offset = 0;
            while (offset < result.Length)
            {
                int count = stream.Read(result, offset, result.Length - offset);
                if (count <= 0) throw new EndOfStreamException();
                offset += count;
            }
            return result;
        }

        private static bool TryParseRange(string value, long length, out long start, out long end)
        {
            start = 0;
            end = Math.Max(0, length - 1);
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            string[] parts = value.Substring(6).Split('-');
            long parsedStart;
            long parsedEnd;
            if (parts.Length != 2 || !long.TryParse(parts[0], out parsedStart)) return false;
            parsedEnd = string.IsNullOrWhiteSpace(parts[1]) ? length - 1 : ParseLong(parts[1], length - 1);
            if (parsedStart < 0 || parsedStart >= length || parsedEnd < parsedStart) return false;
            start = parsedStart;
            end = Math.Min(length - 1, parsedEnd);
            return true;
        }

        private static string ComputeCacheKey(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed class HttpRequestData
        {
            public readonly IDictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Method;
            public string Target;
            public string Version;
            public byte[] Body = new byte[0];
            public string GetHeader(string name) { string value; return Headers.TryGetValue(name, out value) ? value : string.Empty; }
        }

        private sealed class HttpResponseData
        {
            public readonly IDictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public byte[] RawHeader;
            public int StatusCode;
            public long ContentLength;
            public bool Chunked;
            public string ContentType;
            public string GetHeader(string name) { string value; return Headers.TryGetValue(name, out value) ? value : string.Empty; }
        }
    }
}
