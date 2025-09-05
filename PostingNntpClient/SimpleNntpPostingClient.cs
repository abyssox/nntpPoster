using log4net;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PostingNntpClient
{
    public class SimpleNntpPostingClient : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly Encoding Iso88591 = Encoding.GetEncoding("iso-8859-1");

        public NewsHostConnectionInfo ConnectionInfo { get; private set; }

        private TcpClient _tcpClient;
        private Stream _stream;
        private StreamWriter _writer;
        private StreamReader _reader;
        private bool _disposed;

        public SimpleNntpPostingClient(NewsHostConnectionInfo connectionInfo)
        {
            if (connectionInfo == null) throw new ArgumentNullException(nameof(connectionInfo));
            ConnectionInfo = connectionInfo;
        }

        private bool CertValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors != SslPolicyErrors.None)
            {
                log.WarnFormat("SSL certificate validation errors: {0}", errors);
            }
            return true;
        }

        private NntpResponse ReadResponse()
        {
            string line = _reader.ReadLine();
            if (line == null)
                throw new IOException("Connection closed by server while awaiting response.");
            return new NntpResponse(line);
        }

        public void Connect()
        {
            log.DebugFormat("Connecting to newshost: {0}:{1}", ConnectionInfo.Address, ConnectionInfo.Port);

            _tcpClient = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = ConnectionInfo.TcpTimeoutSeconds * 1000,
                SendTimeout = ConnectionInfo.TcpTimeoutSeconds * 1000
            };
            _tcpClient.Connect(ConnectionInfo.Address, ConnectionInfo.Port);

            var netStream = _tcpClient.GetStream();
            netStream.ReadTimeout = ConnectionInfo.TcpTimeoutSeconds * 1000;
            netStream.WriteTimeout = ConnectionInfo.TcpTimeoutSeconds * 1000;

            if (ConnectionInfo.UseSsl)
            {
                log.Debug("Using SSL.");
                var sslStream = new SslStream(netStream, leaveInnerStreamOpen: false, CertValidationCallback);
                sslStream.AuthenticateAsClient(ConnectionInfo.Address);
                _stream = sslStream;
            }
            else
            {
                _stream = netStream;
            }

            _writer = new StreamWriter(_stream, Iso88591)
            {
                NewLine = "\r\n", // NNTP requires CRLF
                AutoFlush = true
            };
            _reader = new StreamReader(_stream, Iso88591);

            var connectResponse = ReadResponse();
            log.DebugFormat("NewsHost response: [{0}] {1}", connectResponse.ResponseCode, connectResponse.ResponseMessage);
            if (connectResponse.ResponseCode != Rfc977ResponseCodes.ServerReadyPostingAllowed)
                throw new Exception("Could not open a posting connection: " + connectResponse.ResponseMessage);

            Authenticate();
        }

        private void Authenticate()
        {
            string user = (ConnectionInfo.Username ?? string.Empty).Replace("\r", "").Replace("\n", "");
            string pass = (ConnectionInfo.Password ?? string.Empty).Replace("\r", "").Replace("\n", "");

            _writer.WriteLine("AUTHINFO USER {0}", user);
            var response = ReadResponse();

            if (response.ResponseCode == Rfc4643ResponseCodes.PasswordRequired)
            {
                _writer.WriteLine("AUTHINFO PASS {0}", pass);
                response = ReadResponse();
                if (response.ResponseCode != Rfc4643ResponseCodes.AuthenticationAccepted)
                    throw new Exception("Could not authenticate: " + response.ResponseMessage);
                return;
            }

            if (response.ResponseCode == Rfc977ResponseCodes.ServerReadyPostingAllowed ||
                response.ResponseCode == Rfc4643ResponseCodes.AuthenticationAccepted)
            {
                return;
            }

            throw new Exception("Unable to authenticate: " + response.ResponseMessage);
        }

        public string PostYEncMessage(
            string overrideMessageId,
            string from,
            string subject,
            IEnumerable<string> newsGroups,
            DateTime postedDateTime,
            IEnumerable<string> yEncHeaders,
            byte[] yEncBody,
            IEnumerable<string> yEncFooters)
        {
            if (newsGroups == null) throw new ArgumentNullException(nameof(newsGroups));
            if (yEncHeaders == null) throw new ArgumentNullException(nameof(yEncHeaders));
            if (yEncBody == null) throw new ArgumentNullException(nameof(yEncBody));
            if (yEncFooters == null) throw new ArgumentNullException(nameof(yEncFooters));

            _writer.WriteLine("POST");
            var response = ReadResponse();
            if (response.ResponseCode != Rfc977ResponseCodes.SendArticleToPost)
                throw new Exception("Could not start posting message: " + response.ResponseMessage);

            // If server provides an ID, prefer it unless override was given
            var messageId = string.IsNullOrWhiteSpace(overrideMessageId)
                ? ExtractMessageID(response.ResponseMessage)
                : overrideMessageId;

            var dto = new DateTimeOffset(postedDateTime.ToUniversalTime(), TimeSpan.Zero);
            var dateHeader = dto.ToString("ddd, dd MMM yyyy HH':'mm':'ss +0000", CultureInfo.InvariantCulture);

            var safeFrom = SanitizeHeader(from);
            var safeSubject = SanitizeHeader(subject);

            _writer.WriteLine("From: {0}", safeFrom);
            _writer.WriteLine("Subject: {0}", safeSubject);
            _writer.WriteLine("Date: {0}", dateHeader);
            _writer.WriteLine("Message-ID: {0}", messageId);

            // Newsgroups: single header, comma-separated list
            var groupsCsv = string.Join(", ", newsGroups);
            _writer.WriteLine("Newsgroups: {0}", groupsCsv);

            // Blank line separates headers from body
            _writer.WriteLine();

            // yEnc headers (text) — dot-stuffed
            foreach (var line in yEncHeaders)
                _writer.WriteLine(DotStuff(line));

            // yEnc body (binary) — write as-is
            _stream.Write(yEncBody, 0, yEncBody.Length);

            // Ensure CRLF boundary before footers if body did not end with CRLF
            if (yEncBody.Length == 0 ||
                !(yEncBody.Length >= 2 && yEncBody[yEncBody.Length - 2] == (byte)'\r' && yEncBody[yEncBody.Length - 1] == (byte)'\n'))
            {
                _writer.WriteLine();
            }

            // yEnc footers (text) — dot-stuffed
            foreach (var line in yEncFooters)
                _writer.WriteLine(DotStuff(line));

            // End of article
            _writer.WriteLine(".");
            _writer.Flush(); // make sure everything is on the wire

            response = ReadResponse();
            if (response.ResponseCode != Rfc977ResponseCodes.ArticlePostedOk)
                throw new Exception("Article failed to post: " + response.ResponseMessage);

            return messageId;
        }

        private static string SanitizeHeader(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\r", "").Replace("\n", "");
        }

        private static string DotStuff(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;
            return line.StartsWith(".") ? "." + line : line;
        }

        private string ExtractMessageID(string serverResponse)
        {
            if (string.IsNullOrEmpty(serverResponse))
                return "<" + Guid.NewGuid().ToString("N") + "@" + ConnectionInfo.Address + ">";

            int open = serverResponse.IndexOf('<');
            int close = serverResponse.IndexOf('>');
            if (open >= 0 && close > open)
                return serverResponse.Substring(open, close - open + 1);

            return "<" + Guid.NewGuid().ToString("N") + "@" + ConnectionInfo.Address + ">";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

             try { _writer?.WriteLine("QUIT"); _writer?.Flush(); } catch { }
            try { _stream?.Flush(); } catch { }

            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            _reader = null;
            _writer = null;
            _stream = null;
            _tcpClient = null;
        }
    }
}
