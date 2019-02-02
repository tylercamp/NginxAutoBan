using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NAB
{
    class NginxLogsWatcher : IDisposable
    {
        private class StreamingContext : IDisposable
        {
            private ILogger logger = Log.ForContext<StreamingContext>();
            private FileStream fileStream;
            private StreamReader streamReader;
            private GZipStream gzipStream;

            private StringBuilder pendingLine = new StringBuilder();
            private bool isDisposing = false;

            public StreamingContext(String filePath)
            {
                Reopen(filePath);
            }

            public void Reopen(String filePath)
            {
                long streamPosition = 0;
                if (fileStream != null)
                {
                    streamPosition = fileStream.Position;
                    streamReader.Dispose();
                    gzipStream?.Dispose();
                    fileStream.Dispose();
                }

                fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                if (filePath.EndsWith(".gz"))
                {
                    gzipStream = new GZipStream(fileStream, CompressionMode.Decompress, true);
                    streamReader = new StreamReader(gzipStream, Encoding.Default);
                }
                else
                {
                    streamReader = new StreamReader(fileStream, Encoding.Default);
                }

                fileStream.Position = Math.Min(streamPosition, fileStream.Length);
            }
            
            public List<String> ReadPendingText(CancellationToken cancellationToken)
            {
                List<String> result = new List<string>();
                while (!cancellationToken.IsCancellationRequested && !isDisposing)
                {
                    try
                    {
                        if (streamReader.EndOfStream)
                            break;

                        char c = (char)streamReader.Read();
                        if (c == '\n' || c == '\r')
                        {
                            if (pendingLine.Length > 0)
                            {
                                var line = pendingLine.ToString();
                                logger.Verbose("Got line: {line}", line);
                                result.Add(line);
                                pendingLine.Clear();
                            }
                        }
                        else
                        {
                            pendingLine.Append(c);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Warning("Exception occurred reading pending text: {message}", e.Message);
                    }
                }
                return result;
            }

            public void Dispose()
            {
                isDisposing = true;

                try { streamReader.Dispose(); } catch { }
                try { gzipStream?.Dispose(); } catch { }
                try { fileStream.Dispose(); } catch { }
            }
        }

        private class JobContext : IDisposable
        {
            NginxLogsWatcher target;
            public JobContext(NginxLogsWatcher target)
            {
                this.target = target;
                Interlocked.Increment(ref target.jobCounter);
            }

            public void Dispose()
            {
                Interlocked.Decrement(ref target.jobCounter);
            }
        }

        private FileSystemWatcher logFolderWatcher;
        private ILogger logger = Log.ForContext<NginxLogsWatcher>();
        private ConcurrentDictionary<String, StreamingContext> watchedFiles = new ConcurrentDictionary<string, StreamingContext>();
        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private int jobCounter = 0;

        public NginxLogsWatcher(String logFolderPath)
        {
            logFolderPath = Path.GetFullPath(logFolderPath);
            logger.Debug("Getting existing log files from {path}...", logFolderPath);

            var searchPattern = "*.log*";

            this.logFolderWatcher = new FileSystemWatcher(logFolderPath)
            {
                IncludeSubdirectories = false,
                Filter = searchPattern,
                NotifyFilter =
                    NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Security
            };

            this.logFolderWatcher.Created += LogFolderWatcher_Created;
            this.logFolderWatcher.Deleted += LogFolderWatcher_Deleted;
            this.logFolderWatcher.Renamed += LogFolderWatcher_Renamed;
            this.logFolderWatcher.Changed += LogFolderWatcher_Changed;

            var existingFiles = Directory.EnumerateFiles(logFolderPath, searchPattern);
            foreach (var file in existingFiles)
            {
                var filePath = Path.Combine(logFolderPath, file);
                watchedFiles.TryAdd(filePath, new StreamingContext(filePath));
            }

            logger.Debug("Found {count} existing files", watchedFiles.Count);

            Task.Factory.StartNew(ReadWatchedFiles, TaskCreationOptions.LongRunning);
        }

        public delegate void NewLineHandler(String sourceFileName, String text);
        public event NewLineHandler NewLine;

        public void Dispose()
        {
            this.logFolderWatcher.EnableRaisingEvents = false;
            cancellationTokenSource.Cancel();
            while (jobCounter > 0)
                Task.Delay(10).Wait();

            this.logFolderWatcher.Dispose();

            foreach (var context in watchedFiles.Values)
                context.Dispose();
        }
        
        private void ReadWatchedFiles()
        {
            logger.Debug("Reading current file contents...");
            var readLineTasks = watchedFiles.Select(kvp =>
            {
                return new
                {
                    File = kvp.Key,
                    Task = Task.Run(() => kvp.Value.ReadPendingText(cancellationTokenSource.Token))
                };
            }).ToList();

            var waitTask = Task.WhenAll(readLineTasks.Select(t => t.Task).ToArray());
            while (!waitTask.IsCompleted)
                Task.Delay(10);

            if (cancellationTokenSource.IsCancellationRequested)
                return;

            logger.Debug("Finished reading");

            foreach (var task in readLineTasks)
            {
                foreach (var line in task.Task.Result)
                {
                    try
                    {
                        NewLine?.Invoke(task.File, line);
                    }
                    catch (Exception e)
                    {
                        logger.Warning("Exception in NewLine callback for line '{line}': {exception}", line, e);
                    }
                }
            }

            logger.Debug("Enabling FS watcher");
            this.logFolderWatcher.EnableRaisingEvents = true;
        }

        private void LogFolderWatcher_Created(object sender, FileSystemEventArgs e)
        {
            lock (watchedFiles)
            {
                logger.Debug("File {name} created, watching...", e.Name);
                watchedFiles.TryAdd(e.FullPath, new StreamingContext(e.FullPath));
            }
        }

        private void LogFolderWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            lock (watchedFiles)
            {
                logger.Debug("File {name} deleted, removing from watch-list...", e.Name);
                try
                {
                    if (watchedFiles.TryRemove(e.FullPath, out StreamingContext stream))
                        stream.Dispose();
                }
                catch (Exception ex)
                {
                    logger.Warning("Exception handling file delete ({type}): {message}", ex.GetType().Name, ex.Message);
                }
            }
        }

        private void LogFolderWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            lock (watchedFiles)
            {
                logger.Debug("File renamed {oldName} -> {newName}, reloading stream context...", e.OldName, e.Name);

                try
                {
                    if (watchedFiles.TryRemove(e.OldFullPath, out StreamingContext stream))
                    {
                        stream.Reopen(e.FullPath);
                        watchedFiles.TryAdd(e.FullPath, stream);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning("Exception handling file rename ({type}): {message}", ex.GetType().Name, ex.Message);
                }
            }
        }

        void LogFolderWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            logger.Verbose("File {name} changed, checking for updates...", e.Name);

            lock (watchedFiles)
                if (!watchedFiles.ContainsKey(e.FullPath))
                    return;

            var context = watchedFiles[e.FullPath];

            Task.Run(() =>
            {
                using (new JobContext(this))
                {
                    foreach (var line in context.ReadPendingText(cancellationTokenSource.Token))
                        NewLine?.Invoke(e.FullPath, line);
                }
            });
        }
    }
}
