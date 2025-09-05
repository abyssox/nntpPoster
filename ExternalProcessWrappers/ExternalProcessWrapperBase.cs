using log4net;
using System;
using System.Diagnostics;
using System.Text;

namespace ExternalProcessWrappers
{
    public abstract class ExternalProcessWrapperBase : IDisposable
    {
        protected static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected abstract string ProcessPathLocation { get; }

        protected DateTime LastOutputReceivedAt { get; set; }

        private string _processLocation;
        public string ProcessLocation
        {
            get { return _processLocation; }
            set
            {
                _processLocation = string.IsNullOrWhiteSpace(value) ? ProcessPathLocation : value;
            }
        }

        public int InactiveProcessTimeout { get; set; }

        private StringBuilder _outBuf;
        private StringBuilder _errBuf;
        private StdStreamReader _stdoutReader;
        private StdStreamReader _stderrReader;

        protected ExternalProcessWrapperBase(int inactiveProcessTimeout)
        {
            InactiveProcessTimeout = inactiveProcessTimeout;
            ProcessLocation = ProcessPathLocation;
        }

        protected ExternalProcessWrapperBase(int inactiveProcessTimeout, string processLocation)
        {
            InactiveProcessTimeout = inactiveProcessTimeout;
            ProcessLocation = processLocation;
        }

        protected void ExecuteProcess(string parameters, string workingDirectory = null)
        {
            _outBuf = new StringBuilder();
            _errBuf = new StringBuilder();
            _stdoutReader = new StdStreamReader();
            _stderrReader = new StdStreamReader();

            _stdoutReader.DataReceivedEvent += StdoutReader_DataReceivedEvent;
            _stderrReader.DataReceivedEvent += StderrReader_DataReceivedEvent;

            using (var process = new Process())
            {
                process.StartInfo.FileName = ProcessLocation;
                process.StartInfo.Arguments = parameters;

                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    process.StartInfo.WorkingDirectory = workingDirectory;
                    log.DebugFormat("Process working directory: [{0}]", workingDirectory);
                }

                log.DebugFormat("Executing process: [{0} {1}]", ProcessLocation, parameters);

                LastOutputReceivedAt = DateTime.UtcNow;

                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();

                _stdoutReader.StartReader(process.StandardOutput.BaseStream, process);
                _stderrReader.StartReader(process.StandardError.BaseStream, process);

                while (!process.WaitForExit(60 * 1000))
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        _stdoutReader.IsDone();
                        _stderrReader.IsDone();
                        break;
                    }

                    if (InactiveProcessTimeout > 0 &&
                        (DateTime.UtcNow - LastOutputReceivedAt).TotalMinutes > InactiveProcessTimeout)
                    {
                        log.WarnFormat("No output received for {0} minutes, killing external process.", InactiveProcessTimeout);
                        try
                        {
                            _stdoutReader.IsDone();
                            _stderrReader.IsDone();
                            process.Kill();
                        }
                        catch (Exception killEx)
                        {
                            log.Warn("Failed to kill external process after inactivity.", killEx);
                        }

                        string lastErr = PeekLastLine(_errBuf);
                        throw new Exception("External process had to be killed due to inactivity."
                            + (string.IsNullOrWhiteSpace(lastErr) ? "" : " Last stderr: " + lastErr));
                    }
                }

                _stdoutReader.IsDone();
                _stderrReader.IsDone();

                if (process.ExitCode != 0)
                {
                    string lastErr = PeekLastLine(_errBuf);
                    throw new Exception("Process has exited with errors. Exit code: " + process.ExitCode
                        + (string.IsNullOrWhiteSpace(lastErr) ? "" : " Last stderr: " + lastErr));
                }
            }
        }

        private void StdoutReader_DataReceivedEvent(object sender, DataReceived e)
        {
            LastOutputReceivedAt = DateTime.UtcNow;
            if (e == null || e.Data == null) return;

            _outBuf.Append(e.Data);
            ProcessBufferedLines(_outBuf, line => Process_OutputDataReceived(sender, line));
        }

        private void StderrReader_DataReceivedEvent(object sender, DataReceived e)
        {
            LastOutputReceivedAt = DateTime.UtcNow;
            if (e == null || e.Data == null) return;

            _errBuf.Append(e.Data);
            ProcessBufferedLines(_errBuf, line => Process_ErrorDataReceived(sender, line));
        }

        private static void ProcessBufferedLines(StringBuilder buffer, Action<string> handleLine)
        {
            int start = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == '\n')
                {
                    int length = i - start + 1;
                    var line = buffer.ToString(start, length);

                    line = line.TrimEnd('\n');
                    if (line.EndsWith("\r", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 1);

                    if (!string.IsNullOrWhiteSpace(line))
                        handleLine(line);

                    start = i + 1;
                }
            }

            if (start > 0)
                buffer.Remove(0, start);
        }

        private static string PeekLastLine(StringBuilder buffer)
        {
            if (buffer == null || buffer.Length == 0) return string.Empty;
            int take = Math.Min(512, buffer.Length);
            string tail = buffer.ToString(buffer.Length - take, take);
            int nl = Math.Max(tail.LastIndexOf('\n'), tail.LastIndexOf('\r'));
            string last = nl >= 0 ? tail.Substring(nl + 1) : tail;
            return last.Trim();
        }

        protected virtual void Process_OutputDataReceived(object sender, string outputLine)
        {
            if (!string.IsNullOrWhiteSpace(outputLine))
                log.Debug(outputLine);
        }

        protected virtual void Process_ErrorDataReceived(object sender, string outputLine)
        {
            if (!string.IsNullOrWhiteSpace(outputLine))
                log.Warn(outputLine);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_stdoutReader != null)
                {
                    _stdoutReader.DataReceivedEvent -= StdoutReader_DataReceivedEvent;
                    _stdoutReader.Dispose();
                    _stdoutReader = null;
                }
                if (_stderrReader != null)
                {
                    _stderrReader.DataReceivedEvent -= StderrReader_DataReceivedEvent;
                    _stderrReader.Dispose();
                    _stderrReader = null;
                }
            }
        }
    }
}