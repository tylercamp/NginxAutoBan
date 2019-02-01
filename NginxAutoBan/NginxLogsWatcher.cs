using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NAB
{
    class NginxLogsWatcher : IDisposable
    {
        private FileSystemWatcher logFolderWatcher;
        private ILogger logger = Log.ForContext<NginxLogsWatcher>();
        private Task readTask;

        private class StreamingContext : IDisposable
        {
            private ILogger logger = Log.ForContext<StreamingContext>();

            public StreamingContext(String filePath)
            {
                FileStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                StreamReader = new StreamReader(FileStream, Encoding.Default);
            }

            public FileStream FileStream { get; set; }
            public StreamReader StreamReader { get; set; }
            
            public async Task<List<String>> ReadPendingText(CancellationToken cancellationToken)
            {
                List<String> result = new List<string>();
                while (!cancellationToken.IsCancellationRequested && !isDisposing)
                {
                    try
                    {
                        if (StreamReader.EndOfStream)
                        {
                            await Task.Delay(1);
                            continue;
                        }

                        char c = (char)StreamReader.Read();
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

            private StringBuilder pendingLine = new StringBuilder();
            private bool isDisposing = false;

            public void Dispose()
            {
                isDisposing = true;
                try { StreamReader.Dispose(); } catch { }
                try { FileStream.Dispose(); } catch { }
            }
        }

        private ConcurrentDictionary<String, StreamingContext> watchedFiles = new ConcurrentDictionary<string, StreamingContext>();

        public NginxLogsWatcher(String logFolderPath)
        {
            logger.Debug("Getting existing log files...");

            this.logFolderWatcher = new FileSystemWatcher(logFolderPath);
            this.logFolderWatcher.IncludeSubdirectories = false;
            this.logFolderWatcher.NotifyFilter =
                NotifyFilters.LastWrite | NotifyFilters.Size |
                NotifyFilters.Attributes | NotifyFilters.CreationTime |
                NotifyFilters.FileName | NotifyFilters.Security;

            this.logFolderWatcher.Created += LogFolderWatcher_Created;
            this.logFolderWatcher.Deleted += LogFolderWatcher_Deleted;
            this.logFolderWatcher.Renamed += LogFolderWatcher_Renamed;

            this.logFolderWatcher.EnableRaisingEvents = true;

            var existingFiles = Directory.EnumerateFiles(logFolderPath);
            foreach (var file in existingFiles.Where(f => !f.EndsWith(".gz")))
            {
                var filePath = Path.Combine(logFolderPath, file);
                watchedFiles.TryAdd(filePath, new StreamingContext(filePath));
            }

            logger.Debug("Found {count} existing files", watchedFiles.Count);

            readTask = Task.Factory.StartNew(ReadWatchedFiles, TaskCreationOptions.LongRunning);
        }

        public delegate void NewLineHandler(String sourceFileName, String text);
        public event NewLineHandler NewLine;

        public void Dispose()
        {
            var oldTask = readTask;
            readTask = null;
            oldTask.Wait();

            foreach (var context in watchedFiles.Values)
                context.Dispose();
        }
        
        private void ReadWatchedFiles()
        {
            while (readTask != null)
            {
                var tokenSource = new CancellationTokenSource(100);

                var readLineTasks = watchedFiles.Select(kvp =>
                {
                    return new
                    {
                        File = kvp.Key,
                        Task = kvp.Value.ReadPendingText(tokenSource.Token)
                    };
                }).ToList();


                Task.WaitAll(readLineTasks.Select(t => t.Task).ToArray());

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

                Task.Delay(10);
            }
        }

        private void LogFolderWatcher_Created(object sender, FileSystemEventArgs e)
        {
            lock (watchedFiles)
            {
                if (!e.Name.EndsWith(".gz"))
                {
                    logger.Debug("File {name} created, watching...", e.Name);
                    watchedFiles.TryAdd(e.FullPath, new StreamingContext(e.FullPath));
                }
            }
        }

        private void LogFolderWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            lock (watchedFiles)
            {
                logger.Debug("File {name} deleted, removing from watch-list...", e.Name);
                try
                {
                    StreamingContext stream;
                    if (watchedFiles.TryRemove(e.FullPath, out stream))
                        stream.Dispose();
                }
                catch (Exception ex)
                {
                    logger.Warning("Exception ({type}): {message}", ex.GetType().Name, ex.Message);
                }
            }
        }

        private void LogFolderWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            lock (watchedFiles)
            {
                logger.Debug("File renamed {oldName} -> {newName}, resetting stream context...", e.OldName, e.Name);

                try {
                    StreamingContext oldStream;
                    if (watchedFiles.TryRemove(e.OldFullPath, out oldStream))
                        oldStream.Dispose();
                }
                catch (Exception ex)
                {
                    logger.Warning("Exception ({type}): {message}", ex.GetType().Name, ex.Message);
                }

                if (!e.Name.EndsWith(".gz"))
                {
                    watchedFiles.TryAdd(e.FullPath, new StreamingContext(e.FullPath));
                }
                else
                {
                    logger.Debug("File renamed with .gz extension, no longer watching");
                }
            }
        }
    }
}
