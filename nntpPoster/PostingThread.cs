using log4net;
using PostingNntpClient;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Util;
using Util.Configuration;

namespace nntpPoster
{
    class PostingThread : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly object _monitor = new object();
        private volatile bool _stopRequested;
        private volatile bool _finished;
        private readonly Task _task;

        private SimpleNntpPostingClient _client;

        private readonly Settings _configuration;
        private readonly WatchFolderSettings _folderConfiguration;
        private readonly NewsHostConnectionInfo _connectionInfo;
        private readonly Queue<NntpMessage> _messageQueue;

        private const int IdleCloseMs = 5000;
        private const int IdleSleepMs = 100;

        public event EventHandler<NntpMessage> MessagePosted;
        protected virtual void OnMessagePosted(NntpMessage e) => MessagePosted?.Invoke(this, e);

        public PostingThread(
            Settings configuration,
            WatchFolderSettings folderConfiguration,
            NewsHostConnectionInfo connectionInfo,
            Queue<NntpMessage> messageQueue)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _folderConfiguration = folderConfiguration ?? throw new ArgumentNullException(nameof(folderConfiguration));
            _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
            _messageQueue = messageQueue ?? throw new ArgumentNullException(nameof(messageQueue));

            _task = new Task(PostingTask, TaskCreationOptions.LongRunning);
        }

        public void Start()
        {
            _stopRequested = false;
            _finished = false;
            _task.Start();
        }

        public void Stop()
        {
            lock (_monitor)
            {
                _stopRequested = true;
                Monitor.PulseAll(_monitor);
            }
            _task.Wait();
        }

        public Task RequestStop()
        {
            lock (_monitor)
            {
                _stopRequested = true;
                Monitor.PulseAll(_monitor);
            }
            return _task;
        }

        public void Signal()
        {
            lock (_monitor)
            {
                Monitor.PulseAll(_monitor);
            }
        }

        private void PostingTask()
        {
            try
            {
                var lastActivityUtc = DateTime.UtcNow;

                while (!_finished)
                {
                    var message = GetNextMessageToPost();
                    if (message != null)
                    {
                        log.DebugFormat("Posting message [{0}]", message.Subject);
                        PostMessage(message);
                        lastActivityUtc = DateTime.UtcNow;
                        continue;
                    }

                    if (_client != null)
                    {
                        var idleMs = (int)(DateTime.UtcNow - lastActivityUtc).TotalMilliseconds;
                        if (idleMs > IdleCloseMs)
                        {
                            log.Debug("Disposing client because of empty queue (idle timeout).");
                            CloseClient();
                        }
                    }

                    if (_stopRequested)
                    {
                        // If stop requested and queue is empty, we’re done
                        lock (_messageQueue)
                        {
                            if (_messageQueue.Count == 0)
                            {
                                _finished = true;
                                break;
                            }
                        }
                    }

                    // Wait a bit (or until signaled)
                    lock (_monitor)
                    {
                        if (_finished) break;
                        Monitor.Wait(_monitor, IdleSleepMs);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("Exception in the posting thread.", ex);
            }
            finally
            {
                CloseClient();
                log.Debug("Finished posting task, exiting thread.");
            }
        }

        private void PostMessage(NntpMessage message)
        {
            int attempt = 0;

            while (attempt < _configuration.MaxRetryCount)
            {
                try
                {
                    if (_client == null)
                    {
                        log.Debug("Constructing new client.");
                        _client = new SimpleNntpPostingClient(_connectionInfo);
                        _client.Connect();
                    }

                    string proposedMessageID = null;
                    if (_folderConfiguration.GenerateRandomMessageId)
                    {
                        proposedMessageID = string.Format("<{0}@{1}.{2}>",
                            RandomStringGenerator.GetRandomString(20, 30),
                            RandomStringGenerator.GetRandomString(5, 10),
                            RandomStringGenerator.GetRandomString(3));
                    }

                    var partMessageId = _client.PostYEncMessage(
                        proposedMessageID,
                        message.FromAddress,
                        message.Subject,
                        message.PostInfo.PostedGroups,
                        message.PostInfo.PostedDateTime,
                        message.Prefix,
                        message.YEncFilePart.EncodedLines,
                        message.Suffix);

                    log.DebugFormat("Message [{0}] posted. Adding to segments.", message.Subject);

                    lock (message.PostInfo.Segments)
                    {
                        message.PostInfo.Segments.Add(new PostedFileSegment
                        {
                            MessageId = partMessageId,
                            Bytes = message.YEncFilePart.Size,
                            SegmentNumber = message.YEncFilePart.Number
                        });
                    }

                    OnMessagePosted(message);
                    return;
                }
                catch (Exception ex)
                {
                    // Any failure → close & null client to force a clean reconnect
                    log.Warn("Posting yEnc message failed", ex);
                    CloseClient();

                    attempt++;

                    if (attempt >= _configuration.MaxRetryCount)
                    {
                        log.ErrorFormat("Maximum retry attempts reached ({0}). Posting is probably corrupt. Subject: {1}",
                            _configuration.MaxRetryCount, message.Subject);
                        return;
                    }

                    log.DebugFormat("Waiting {0} second(s) before retry.", _configuration.RetryDelaySeconds);
                    try
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0, _configuration.RetryDelaySeconds)));
                    }
                    catch { /* ignore */ }

                    log.InfoFormat("Retrying to post message, attempt {0} of {1}", attempt, _configuration.MaxRetryCount);
                }
            }
        }

        private NntpMessage GetNextMessageToPost()
        {
            NntpMessage message = null;

            lock (_messageQueue)
            {
                var count = _messageQueue.Count;
                log.DebugFormat("The messageQueue has {0} items.", count);
                if (count > 0)
                    message = _messageQueue.Dequeue();
            }

            if (message == null && !_stopRequested)
                log.DebugFormat("Posting thread is starved, reduce threads to make more optimal use of resources.");

            return message;
        }

        private void CloseClient()
        {
            try
            {
                if (_client != null)
                {
                    log.Debug("Disposing NNTP client.");
                    _client.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.Debug("Exception while disposing NNTP client.", ex);
            }
            finally
            {
                _client = null;
            }
        }

        public void Dispose()
        {
            CloseClient();
        }
    }
}
